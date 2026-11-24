package com.freeremote;

import android.content.Context;
import android.util.Log;

import com.google.gson.JsonObject;

import java.io.IOException;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.TimeUnit;
import java.util.function.Consumer;

/**
 * 与各台电脑的控制会话（每台一条，串流期间保持）。负责：自动配对时把 PIN 交给对方、
 * 让对方显示画面里的光标、关闭对方屏幕（隐私屏）。
 */
public final class FrHub {
    private static final String TAG = "FrHub";
    private static FrHub instance;

    public static synchronized FrHub get(Context ctx) {
        if (instance == null) instance = new FrHub(ctx.getApplicationContext());
        return instance;
    }

    private final Context app;
    private final Map<String, FrSession> sessions = new ConcurrentHashMap<>();
    private final Map<String, CompletableFuture<JsonObject>> pendingPair = new ConcurrentHashMap<>();
    private final Map<String, Consumer<Boolean>> cursorModeHandlers = new ConcurrentHashMap<>();
    private final Map<String, Boolean> privacy = new ConcurrentHashMap<>();

    private FrHub(Context app) { this.app = app; }

    public final FrTransfer transfer = new FrTransfer();
    private final Map<String, CompletableFuture<JsonObject>> pendingList = new ConcurrentHashMap<>();
    private final Map<String, CompletableFuture<JsonObject>> pendingOp = new ConcurrentHashMap<>();
    private final Map<String, JsonObject> deviceInfo = new ConcurrentHashMap<>();

    public static final int FS_LIST = 90, FS_LIST_RESULT = 91, FS_PULL = 92, FS_OP = 93, FS_OP_RESULT = 94,
            SET_DISPLAY = 80, DISPLAY_RESULT = 81, DISPLAY_LIVE = 82;

    /** 列电脑上的目录（path 为空 = 磁盘和常用文件夹）。阻塞。 */
    public JsonObject fsList(String ip, String path) throws Exception {
        ip = FrNet.toVirtual(ip);
        FrSession s = session(ip);
        if (FrConfig.isOlder(s.remoteVersion, "0.12.0"))
            throw new Exception("电脑端 Farloop 是 " + s.remoteVersion + "，文件管理需要 0.12.0 以上，请先升级电脑端");
        CompletableFuture<JsonObject> f = new CompletableFuture<>();
        pendingList.put(ip, f);
        JsonObject m = new JsonObject();
        m.addProperty("Path", path == null ? "" : path);
        s.sendJson(FS_LIST, m);
        return f.get(20, TimeUnit.SECONDS);
    }

    /** 新建文件夹 mkdir / 重命名 rename / 删除 delete（进回收站）。返回 null = 成功，否则错误说明 */
    public String fsOp(String ip, String op, String path, String newName) {
        ip = FrNet.toVirtual(ip);
        try {
            FrSession s = session(ip);
            CompletableFuture<JsonObject> f = new CompletableFuture<>();
            pendingOp.put(ip, f);
            JsonObject m = new JsonObject();
            m.addProperty("Op", op);
            m.addProperty("Path", path);
            if (newName != null) m.addProperty("NewName", newName);
            s.sendJson(FS_OP, m);
            JsonObject r = f.get(30, TimeUnit.SECONDS);
            return r.get("Ok").getAsBoolean() ? null : r.get("Message").getAsString();
        } catch (Exception e) {
            return e.getMessage();
        }
    }

    /** 让电脑把这些文件发到手机的 dir */
    public void fsPull(String ip, java.util.List<String> paths, java.io.File dir, FrTransfer.Progress p) throws Exception {
        ip = FrNet.toVirtual(ip);
        FrSession s = session(ip);
        String token = transfer.expectPull(dir, p);
        JsonObject m = new JsonObject();
        com.google.gson.JsonArray arr = new com.google.gson.JsonArray();
        for (String x : paths) arr.add(x);
        m.add("Paths", arr);
        m.addProperty("Token", token);
        s.sendJson(FS_PULL, m);
    }

    /** 对方发来的设备信息（含显示器列表 Displays / CurrentDisplay） */
    public JsonObject deviceInfo(String ip) {
        ip = FrNet.toVirtual(ip); return deviceInfo.get(ip); }

    /** 取得（或建立）到某台电脑的控制会话。阻塞，别在界面线程调用。 */
    private final java.util.concurrent.ConcurrentHashMap<String, Object> sessionLocks = new java.util.concurrent.ConcurrentHashMap<>();

    public FrSession session(String ip) throws IOException {
        ip = FrNet.toVirtual(ip);
        FrSession s = sessions.get(ip);
        if (s != null && s.isOpen()) return s;
        // 每台电脑各用一把锁：连一台慢 / 不在线的电脑时，不会卡住发往其他电脑的请求
        synchronized (sessionLocks.computeIfAbsent(ip, k -> new Object())) {
            return sessionLocked(ip);
        }
    }

    private FrSession sessionLocked(String ip) throws IOException {
        FrSession s = sessions.get(ip);
        if (s != null && s.isOpen()) return s;
        FrConfig c = new FrConfig(app);
        if (FrAssist.isAssistIp(ip)) {
            // 远程协助：临时组网，用设备码和密码换算出的密钥
            if (!FrAssist.isRunning()) throw new IOException("远程协助已结束");
            s = FrSession.connect(ip, FrNet.endpoint(app, ip), c.deviceName(), FrAssist.MY_IP, FrAssist.secret(), null);
            s.start(new FrSession.Listener() {
                @Override public void onMessage(FrSession ss, int type, byte[] p) { handle(ss, type, p); }
                @Override public void onClosed(FrSession ss) { sessions.remove(ss.remoteIp, ss); }
            });
            sessions.put(ip, s);
            Log.i(TAG, "协助会话建立：" + s.remoteName + " 对方版本 " + s.remoteVersion);
            return s;
        }
        FrNet.ensureForward(app, ip);
        s = FrSession.connect(ip, FrNet.endpoint(app, ip), c.deviceName(), c.virtualIp(), c.networkSecret(), null);
        s.start(new FrSession.Listener() {
            @Override public void onMessage(FrSession ss, int type, byte[] p) { handle(ss, type, p); }
            @Override public void onClosed(FrSession ss) { sessions.remove(ss.remoteIp, ss); }
        });
        sessions.put(ip, s);
        Log.i(TAG, "会话建立：" + s.remoteName + " " + ip + " 对方版本 " + s.remoteVersion);
        return s;
    }

    public void close(String ip) {
        ip = FrNet.toVirtual(ip);
        FrSession s = sessions.remove(ip);
        if (s != null) s.close();
        privacy.remove(ip);
    }

    private void handle(FrSession s, int type, byte[] p) {
        switch (type) {
            case FrSession.PAIR_RESULT: {
                CompletableFuture<JsonObject> f = pendingPair.remove(s.remoteIp);
                if (f != null) f.complete(FrSession.json(p));
                break;
            }
            case FrSession.CURSOR_MODE: {
                // 对方要我们按一次 Ctrl+Alt+Shift+N（切换 Sunshine 是否在画面里画光标）
                Consumer<Boolean> h = cursorModeHandlers.get(s.remoteIp);
                if (h != null && p.length == 1) h.accept(p[0] == 1);
                break;
            }
            case FrSession.PRIVACY_STATE:
                if (p.length == 1) privacy.put(s.remoteIp, p[0] == 1);
                break;
            case FrTransfer.FILE_BEGIN: case FrTransfer.FILE_CHUNK: case FrTransfer.FILE_END: case FrTransfer.FILE_ABORT:
                transfer.handle(type, p);
                break;
            case FS_LIST_RESULT: {
                CompletableFuture<JsonObject> f = pendingList.remove(s.remoteIp);
                if (f != null) f.complete(FrSession.json(p));
                break;
            }
            case FS_OP_RESULT: {
                CompletableFuture<JsonObject> f = pendingOp.remove(s.remoteIp);
                if (f != null) f.complete(FrSession.json(p));
                break;
            }
            case DISPLAY_RESULT: {
                CompletableFuture<JsonObject> f = pendingDisplay.remove(s.remoteIp);
                if (f != null) f.complete(FrSession.json(p));
                break;
            }
            case FrSession.DEVICE_INFO:
                deviceInfo.put(s.remoteIp, FrSession.json(p));
                break;
            case SYS_INFO: case TERM_OPENED: case TERM_OUTPUT: case APP_LIST: case LAUNCH_RESULT:
                for (Listener l : listeners) {
                    try { l.onMessage(s.remoteIp, type, p); } catch (Exception e) { Log.w(TAG, "监听器出错", e); }
                }
                break;
            case GOODBYE:
                // 电脑退出了 Farloop（组网也停了）：立刻从列表去掉，不等组网路由超时
                Log.i(TAG, s.remoteName + " 已退出 Farloop");
                FrNet.markGone(s.remoteIp);
                break;
            default:
                break; // 剪贴板、文件等暂不处理
        }
    }

    /**
     * 自动配对第二步：把 Moonlight 这边生成的 PIN 交给对方，对方转交给它的 Sunshine。
     * 返回 null 表示成功，否则是错误说明。
     */
    public String submitPin(String ip, String pin) {
        ip = FrNet.toVirtual(ip);
        try {
            FrSession s = session(ip);
            CompletableFuture<JsonObject> f = new CompletableFuture<>();
            pendingPair.put(ip, f);
            JsonObject m = new JsonObject();
            m.addProperty("Pin", pin);
            m.addProperty("ClientName", new FrConfig(app).deviceName());
            s.sendJson(FrSession.PAIR_REQUEST, m);
            JsonObject r = f.get(45, TimeUnit.SECONDS);
            boolean ok = r.has("Ok") && r.get("Ok").getAsBoolean();
            return ok ? null : (r.has("Message") ? r.get("Message").getAsString() : "配对失败");
        } catch (Exception e) {
            pendingPair.remove(ip);
            return "配对请求失败：" + e.getMessage();
        }
    }

    /** 本地光标开/关（手机上用 0：让被控端在画面里画光标）。handler 收到“需要按一次 N”时调用。 */
    public void setLocalCursor(String ip, boolean on, Consumer<Boolean> handler) {
        ip = FrNet.toVirtual(ip);
        try {
            cursorModeHandlers.put(ip, handler);
            session(ip).send(FrSession.LOCAL_CURSOR, new byte[]{(byte) (on ? 1 : 0)});
        } catch (Exception e) {
            Log.w(TAG, "设置光标模式失败", e);
        }
    }

    public boolean privacyOn(String ip) {
        ip = FrNet.toVirtual(ip);
        Boolean b = privacy.get(ip);
        return b != null && b;
    }

    public void setPrivacy(String ip, boolean on) {
        ip = FrNet.toVirtual(ip);
        try {
            session(ip).send(FrSession.PRIVACY, new byte[]{(byte) (on ? 1 : 0)});
            privacy.put(ip, on);
        } catch (Exception e) {
            Log.w(TAG, "切换对方屏幕失败", e);
        }
    }

    private final Map<String, CompletableFuture<JsonObject>> pendingDisplay = new ConcurrentHashMap<>();

    // ---------------- 设备详情 / 终端 / 快速启动（与电脑端 RemoteTools 对应） ----------------
    public static final int GOODBYE = 123;
    public static final int SYS_INFO_REQ = 110, SYS_INFO = 111, TERM_OPEN = 112, TERM_OPENED = 113, TERM_INPUT = 114,
            TERM_OUTPUT = 115, TERM_CLOSE = 116, TERM_INTERRUPT = 117, APP_LIST_REQ = 118, APP_LIST = 119,
            LAUNCH_APP = 120, LAUNCH_RESULT = 121, SET_IP_RULE = 122;

    /** 对方发来的消息（在网络线程里回调，界面自己切回主线程） */
    public interface Listener { void onMessage(String ip, int type, byte[] payload); }
    private final java.util.List<Listener> listeners = new java.util.concurrent.CopyOnWriteArrayList<>();
    public void addListener(Listener l) { listeners.add(l); }
    public void removeListener(Listener l) { listeners.remove(l); }

    /** 给对方发一条消息（阻塞，后台线程调用）。返回 null = 已发出，否则错误说明 */
    public String send(String ip, int type, byte[] payload) {
        try { session(ip).send(type, payload); return null; }
        catch (Exception e) { return e.getMessage(); }
    }

    public String sendJson(String ip, int type, JsonObject o) {
        return send(ip, type, o.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8));
    }

    /** 对方电脑端版本是否支持这些功能（0.12.1 起） */
    public boolean supportsTools(String ip) {
        try { return !FrConfig.isOlder(session(ip).remoteVersion, "0.12.1"); } catch (Exception e) { return false; }
    }

    /** 串流中已用快捷键切到这块屏：告诉电脑记下来（不重启 Sunshine），电脑会回一份新的设备信息 */
    public void rememberDisplay(String ip, String displayId) {
        try {
            FrSession s = session(FrNet.toVirtual(ip));
            JsonObject m = new JsonObject();
            m.addProperty("Id", displayId);
            s.sendJson(DISPLAY_LIVE, m);
        } catch (Exception e) { Log.w(TAG, "记住屏幕失败", e); }
    }

    /** 让被控端换一块屏幕串流（对方重启 Sunshine，约 5 秒）。返回 null = 成功 */
    public String switchDisplay(String ip, String displayId) {
        ip = FrNet.toVirtual(ip);
        try {
            FrSession s = session(ip);
            CompletableFuture<JsonObject> f = new CompletableFuture<>();
            pendingDisplay.put(ip, f);
            JsonObject m = new JsonObject();
            m.addProperty("Id", displayId);
            s.sendJson(SET_DISPLAY, m);
            JsonObject r = f.get(60, TimeUnit.SECONDS);
            return r.get("Ok").getAsBoolean() ? null : r.get("Message").getAsString();
        } catch (Exception e) {
            return e.getMessage();
        }
    }

    public static boolean isFreeRemoteHost(String ip) {
        return ip != null && (ip.startsWith("10.144.144.") || ip.startsWith("127.144.144.") || FrAssist.isAssistIp(ip));
    }
}
