package com.freeremote;

import android.util.Base64;
import android.util.Log;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.security.SecureRandom;

import javax.crypto.Mac;
import javax.crypto.spec.SecretKeySpec;

/**
 * 与电脑端 FreeRemote 的加密会话（电脑端 Core/Protocol.cs 的 PeerSession，这里只实现“控制端”一侧）。
 * 帧：[1 字节类型][4 字节长度，小端][负载]；JSON 字段名与 C# 一致（PascalCase）。
 * 握手：双方互发 Hello(随机数) → 各自回 HMAC-SHA256(网络密钥, "FreeRemote/1|角色|对方随机数")。
 */
public final class FrSession implements AutoCloseable {
    public static final int PORT = 47900;
    private static final String TAG = "FrSession";

    // 与 C# MsgType 相同
    public static final int HELLO = 1, AUTH = 2, CLIPBOARD = 10,
            PAIR_REQUEST = 30, PAIR_RESULT = 31, PRIVACY = 40, PRIVACY_STATE = 41,
            LOCAL_CURSOR = 50, CURSOR_MODE = 51, DEVICE_INFO = 70, WAKE_REQUEST = 71, WAKE_RESULT = 72;

    public interface Listener {
        void onMessage(FrSession s, int type, byte[] payload);
        void onClosed(FrSession s);
    }

    private final Socket socket;
    private final InputStream in;
    private final OutputStream out;
    private volatile boolean closed;
    private Listener listener;

    public final String remoteIp;
    public String remoteName = "";
    public String remoteVersion = "";

    private FrSession(Socket s, String ip) throws IOException {
        socket = s;
        in = s.getInputStream();
        out = s.getOutputStream();
        remoteIp = ip;
    }

    /**
     * 连接并完成双向认证（阻塞，网络线程里调用）。
     * ip = 对方的虚拟 IP（作为名字）；addr = 实际去连的地址（免 VPN 模式下是本机映射地址 127.144.144.N）
     */
    public static FrSession connect(String ip, String addr, String myName, String myIp, String secret, String purpose) throws IOException {
        Socket s = new Socket();
        s.setTcpNoDelay(true);
        s.connect(new InetSocketAddress(addr, PORT), 8000);
        s.setSoTimeout(10000); // 握手超时
        FrSession fs = new FrSession(s, ip);
        try {
            fs.handshake(myName, myIp, secret, purpose);
            s.setSoTimeout(0);
            return fs;
        } catch (IOException e) {
            fs.close();
            throw e;
        }
    }

    private void handshake(String myName, String myIp, String secret, String purpose) throws IOException {
        byte[] nb = new byte[16];
        new SecureRandom().nextBytes(nb);
        String nonce = Base64.encodeToString(nb, Base64.NO_WRAP);

        JsonObject hello = new JsonObject();
        hello.addProperty("Name", myName);
        hello.addProperty("Ip", myIp);
        hello.addProperty("Nonce", nonce);
        hello.addProperty("Version", FrConfig.VERSION);
        hello.addProperty("Platform", "android"); // 电脑端据此不拿手机版本号和自己比
        if (purpose != null) hello.addProperty("Purpose", purpose);
        write(HELLO, hello.toString().getBytes(StandardCharsets.UTF_8));

        Frame f1 = read();
        if (f1.type != HELLO) throw new IOException("握手失败");
        JsonObject h = json(f1.payload);
        String peerNonce = h.get("Nonce").getAsString();
        if (peerNonce.equals(nonce)) throw new IOException("握手失败");
        remoteName = h.has("Name") ? h.get("Name").getAsString() : remoteIp;
        remoteVersion = h.has("Version") && !h.get("Version").isJsonNull() ? h.get("Version").getAsString() : "";

        JsonObject auth = new JsonObject();
        auth.addProperty("Mac", mac(secret, peerNonce, "C"));
        write(AUTH, auth.toString().getBytes(StandardCharsets.UTF_8));

        Frame f2 = read();
        if (f2.type != AUTH) throw new IOException("握手失败");
        byte[] got = Base64.decode(json(f2.payload).get("Mac").getAsString(), Base64.DEFAULT);
        byte[] expected = Base64.decode(mac(secret, nonce, "S"), Base64.DEFAULT);
        if (!MessageDigest.isEqual(got, expected)) throw new IOException("认证失败：网络密钥不一致");
    }

    private static String mac(String secret, String nonce, String role) {
        try {
            Mac m = Mac.getInstance("HmacSHA256");
            m.init(new SecretKeySpec(secret.getBytes(StandardCharsets.UTF_8), "HmacSHA256"));
            byte[] r = m.doFinal(("FreeRemote/1|" + role + "|" + nonce).getBytes(StandardCharsets.UTF_8));
            return Base64.encodeToString(r, Base64.NO_WRAP);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    /** 开始接收消息（后台线程） */
    public void start(Listener l) {
        listener = l;
        Thread t = new Thread(() -> {
            try {
                while (!closed) {
                    Frame f = read();
                    try { if (listener != null) listener.onMessage(this, f.type, f.payload); }
                    catch (Exception e) { Log.w(TAG, "处理消息 " + f.type + " 出错", e); }
                }
            } catch (IOException ignored) {
            } finally {
                close();
            }
        }, "FrSession-" + remoteIp);
        t.setDaemon(true);
        t.start();
    }

    public boolean isOpen() { return !closed; }

    public synchronized void send(int type, byte[] payload) throws IOException {
        write(type, payload);
    }

    public void sendJson(int type, JsonObject o) throws IOException {
        send(type, o.toString().getBytes(StandardCharsets.UTF_8));
    }

    private synchronized void write(int type, byte[] p) throws IOException {
        byte[] h = new byte[5];
        h[0] = (byte) type;
        int n = p.length;
        h[1] = (byte) n; h[2] = (byte) (n >> 8); h[3] = (byte) (n >> 16); h[4] = (byte) (n >> 24);
        out.write(h);
        if (n > 0) out.write(p);
        out.flush();
    }

    private static final class Frame { int type; byte[] payload; }

    private Frame read() throws IOException {
        DataInputStream d = new DataInputStream(in);
        byte[] h = new byte[5];
        d.readFully(h);
        int len = (h[1] & 255) | ((h[2] & 255) << 8) | ((h[3] & 255) << 16) | ((h[4] & 255) << 24);
        if (len < 0 || len > 64 * 1024 * 1024) throw new IOException("帧长度异常");
        Frame f = new Frame();
        f.type = h[0] & 255;
        f.payload = new byte[len];
        if (len > 0) d.readFully(f.payload);
        return f;
    }

    public static JsonObject json(byte[] b) {
        return JsonParser.parseString(new String(b, StandardCharsets.UTF_8)).getAsJsonObject();
    }

    @Override
    public void close() {
        if (closed) return;
        closed = true;
        try { socket.close(); } catch (IOException ignored) { }
        Listener l = listener;
        if (l != null) l.onClosed(this);
    }
}
