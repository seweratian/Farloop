package com.freeremote;

import android.util.Log;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.easytier.jni.EasyTierJNI;

import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;

/**
 * 远程协助（协助者一端，和电脑端 Core/Assist.cs 同一套规则）：凭对方电脑的“设备码 + 临时密码”连过去，
 * 不需要在同一个组网里。
 *
 * 设备码和密码换算成一个临时组网（网络名 farloop-assist-设备码，密钥 = sha256("farloop-assist-v1|设备码|密码") 前 40 位），
 * 对方电脑在里面是 10.145.0.1，本机是 10.145.0.2。本机另起一个 EasyTier 实例（和平时的组网在同一进程里、互不影响），
 * 不建虚拟网卡，把用到的端口从 127.145.0.1 转发过去。之后的会话、配对、画面都复用原来的流程。
 */
public final class FrAssist {
    private FrAssist() { }

    private static final String TAG = "FrAssist";
    public static final String INSTANCE = "farloop-assist";
    public static final String HOST_IP = "10.145.0.1";
    public static final String MY_IP = "10.145.0.2";
    public static final String HOST_LOCAL = "127.145.0.1";

    private static volatile boolean running;
    private static volatile String secret = "";
    private static volatile String hostName = "";

    public static boolean isRunning() { return running; }
    public static String secret() { return secret; }
    public static String hostName() { return hostName; }

    public static boolean isAssistIp(String ip) {
        return ip != null && (ip.startsWith("10.145.0.") || ip.startsWith("127.145.0."));
    }

    public static String normalizeId(String s) {
        StringBuilder sb = new StringBuilder();
        for (char c : s.toCharArray()) if (c >= '0' && c <= '9') sb.append(c);
        return sb.toString();
    }

    public static String normalizePassword(String s) {
        return s.trim().toLowerCase(java.util.Locale.ROOT);
    }

    static String secretFor(String id, String pw) {
        try {
            byte[] h = MessageDigest.getInstance("SHA-256").digest(("farloop-assist-v1|" + id + "|" + normalizePassword(pw)).getBytes(StandardCharsets.UTF_8));
            StringBuilder sb = new StringBuilder();
            for (byte b : h) sb.append(String.format("%02x", b));
            return sb.substring(0, 40);
        } catch (Exception e) {
            throw new RuntimeException(e);
        }
    }

    static String toml(FrConfig c, String id, String pw) {
        StringBuilder sb = new StringBuilder();
        sb.append("instance_name = \"").append(INSTANCE).append("\"\n");
        sb.append("hostname = \"").append(FrConfig.encodeHostname(c.deviceName())).append("\"\n");
        sb.append("ipv4 = \"").append(MY_IP).append("\"\n");
        sb.append("dhcp = false\n");
        sb.append("listeners = []\n"); // 平时的组网已经占了 11010
        sb.append("\n[network_identity]\n");
        sb.append("network_name = \"farloop-assist-").append(id).append("\"\n");
        sb.append("network_secret = \"").append(secretFor(id, pw)).append("\"\n");
        for (String p : c.peers()) sb.append("\n[[peer]]\nuri = \"").append(p).append("\"\n");
        sb.append("\n[flags]\n");
        sb.append("mtu = 1280\n");
        sb.append("no_tun = true\n");
        for (int[] pp : FrNet.FORWARDS) {
            sb.append("\n[[port_forward]]\n");
            sb.append("bind_addr = \"").append(HOST_LOCAL).append(':').append(pp[0]).append("\"\n");
            sb.append("dst_addr = \"").append(HOST_IP).append(':').append(pp[0]).append("\"\n");
            sb.append("proto = \"").append(pp[1] == 0 ? "tcp" : "udp").append("\"\n");
        }
        return sb.toString();
    }

    /** 加入对方的临时组网。返回 null 表示已启动，否则是错误说明 */
    public static synchronized String start(android.content.Context ctx, String id, String pw) {
        stop();
        FrConfig c = new FrConfig(ctx);
        String cfg = toml(c, id, pw);
        if (EasyTierJNI.parseConfig(cfg) != 0) return "协助组网配置无效：" + EasyTierJNI.getLastError();
        if (EasyTierJNI.runNetworkInstance(cfg) != 0) return "协助组网启动失败：" + EasyTierJNI.getLastError();
        secret = secretFor(id, pw);
        hostName = "";
        running = true;
        Log.i(TAG, "已加入协助组网，设备码 " + id);
        return null;
    }

    /** 退出临时组网（平时的组网不受影响） */
    public static synchronized void stop() {
        if (!running) return;
        running = false;
        secret = "";
        try {
            EasyTierJNI.retainNetworkInstance(FrNet.isRunning() ? new String[]{FrNet.INSTANCE} : new String[0]);
        } catch (Throwable t) { Log.w(TAG, "退出协助组网失败", t); }
        Log.i(TAG, "已退出协助组网");
    }

    /** 对方电脑出现了没有（出现了返回它的名字，否则 null），附带是否 P2P */
    public static String findHost(boolean[] p2pOut) {
        String json;
        try { json = EasyTierJNI.collectNetworkInfos(10); } catch (Throwable t) { return null; }
        if (json == null || json.isEmpty()) return null;
        try {
            JsonObject root = JsonParser.parseString(json).getAsJsonObject();
            JsonObject map = root.has("map") ? root.getAsJsonObject("map") : root;
            if (!map.has(INSTANCE)) return null;
            JsonObject inst = map.getAsJsonObject(INSTANCE);
            JsonArray pairs = inst.has("peer_route_pairs") ? inst.getAsJsonArray("peer_route_pairs") : new JsonArray();
            for (JsonElement e : pairs) {
                JsonObject pr = e.getAsJsonObject();
                JsonObject route = pr.has("route") && pr.get("route").isJsonObject() ? pr.getAsJsonObject("route") : null;
                if (route == null || !HOST_IP.equals(FrNet.ipv4(route.get("ipv4_addr")))) continue;
                JsonObject peer = pr.has("peer") && pr.get("peer").isJsonObject() ? pr.getAsJsonObject("peer") : null;
                boolean direct = peer != null && peer.has("conns") && peer.getAsJsonArray("conns").size() > 0;
                int cost = route.has("cost") ? route.get("cost").getAsInt() : 0;
                if (p2pOut != null && p2pOut.length > 0) p2pOut[0] = direct && cost <= 1;
                String host = route.has("hostname") ? route.get("hostname").getAsString() : "";
                String name = FrConfig.decodeHostname(host);
                hostName = name.isEmpty() ? "对方电脑" : name;
                return hostName;
            }
        } catch (Exception e) { Log.w(TAG, "读取协助组网信息失败", e); }
        return null;
    }
}
