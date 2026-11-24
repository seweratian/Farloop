package com.freeremote;

import android.util.Log;

import com.limelight.Game;
import com.limelight.binding.input.KeyboardTranslator;
import com.limelight.nvstream.NvConnection;
import com.limelight.nvstream.input.KeyboardPacket;

/**
 * 串流画面（Artemis 的 Game）里的 FreeRemote 钩子。Game.java 只改了三处，都调到这里：
 * 视频包大小、画面开始、画面结束。
 */
public final class FrStreamHooks {
    private static final String TAG = "FrStreamHooks";

    private FrStreamHooks() {}

    private static volatile String pendingReconnect;

    // ---------------- 画质预设：流畅 / 标准 / 高清 / 超清，最后一个“自定义”= 用“画面与操控设置”里的值 ----------------
    public static final String[] QUALITY_NAMES = {"流畅", "标准", "高清", "超清", "自定义"};
    public static final String QUALITY_DESC = "流畅 720P·8M　标准 1080P·20M　高清 2K·35M　超清 4K·60M\n码率要小于电脑那边宽带的上行速度（家用宽带上行常见 30~50M），否则会大量丢帧、卡顿。看字清楚选“高清”通常就够了。自定义 = 用“画面与操控设置”里的值";
    private static final int[][] QUALITY = {{1280, 720, 8000}, {1920, 1080, 20000}, {2560, 1440, 35000}, {3840, 2160, 60000}};

    public static int quality(android.content.Context c) {
        return c.getSharedPreferences("freeremote_stream", android.content.Context.MODE_PRIVATE).getInt("quality", 1);
    }

    public static void setQuality(android.content.Context c, int q) {
        c.getSharedPreferences("freeremote_stream", android.content.Context.MODE_PRIVATE).edit().putInt("quality", q).apply();
    }

    /** PreferenceConfiguration 读完设置后调用：套用画质预设（Artemis 默认 720P，用流量时还会把码率砍到 1/4，非常糊） */
    /** 第 q 档的码率（kbps），自定义档返回 0 */
    public static int qualityBitrate(int q) {
        return q >= 0 && q < QUALITY.length ? QUALITY[q][2] : 0;
    }

    /** 码率自适应时编码器按这个最高码率建，画质档位只是上限（这样串流中能无缝升降档） */
    public static final int ADAPTIVE_CEILING_KBPS = 60000;

    /**
     * 码率自适应可用时：分辨率跟随电脑当前屏幕（最高 4K），返回 {宽, 高}；不知道就返回 null（用原来的设置）。
     * 画质档位只改码率上限，切档不用重连
     */
    public static int[] adaptiveResolution(android.content.Context c, String host) {
        if (quality(c) >= QUALITY.length && customRes(c) != 0) return null; // 自定义且指定了分辨率：按指定的来
        com.google.gson.JsonObject info = FrHub.get(c).deviceInfo(host);
        if (info == null || !info.has("Displays") || !info.get("Displays").isJsonArray()) return null;
        String cur = info.has("CurrentDisplay") && !info.get("CurrentDisplay").isJsonNull() ? info.get("CurrentDisplay").getAsString() : "";
        com.google.gson.JsonObject pick = null;
        for (com.google.gson.JsonElement el : info.getAsJsonArray("Displays")) {
            com.google.gson.JsonObject d = el.getAsJsonObject();
            boolean primary = d.has("Primary") && d.get("Primary").getAsBoolean();
            if ((cur.isEmpty() && primary) || (!cur.isEmpty() && cur.equals(d.get("Id").getAsString()))) { pick = d; break; }
            if (pick == null && primary) pick = d;
        }
        if (pick == null || !pick.has("Width")) return null;
        int w = pick.get("Width").getAsInt(), h = pick.get("Height").getAsInt();
        if (w < 640 || h < 360) return null;
        if (w > 3840 || h > 2160) { double s = Math.min(3840.0 / w, 2160.0 / h); w = (int) (w * s) & ~1; h = (int) (h * s) & ~1; }
        return new int[]{w, h};
    }

    // ---- 自定义档（第 5 档）：分辨率（0 = 跟随电脑，1~4 = 720P/1080P/2K/4K）、帧率、码率 ----
    public static final String[] CUSTOM_RES_NAMES = {"跟随电脑", "720P", "1080P", "2K", "4K"};
    public static final int[] CUSTOM_FPS = {30, 60, 90, 120};

    private static android.content.SharedPreferences sp(android.content.Context c) {
        return c.getSharedPreferences("freeremote_stream", android.content.Context.MODE_PRIVATE);
    }

    public static int customRes(android.content.Context c) { return sp(c).getInt("custom_res", 0); }
    public static int customFps(android.content.Context c) { return sp(c).getInt("custom_fps", 60); }
    public static int customKbps(android.content.Context c) { return sp(c).getInt("custom_kbps", 20000); }

    public static void setCustom(android.content.Context c, int res, int fps, int kbps) {
        sp(c).edit().putInt("custom_res", res).putInt("custom_fps", fps).putInt("custom_kbps", kbps).putInt("quality", QUALITY.length).apply();
    }

    public static String customSummary(android.content.Context c) {
        return CUSTOM_RES_NAMES[Math.max(0, Math.min(4, customRes(c)))] + " · " + customFps(c) + "fps · " + customKbps(c) / 1000 + "M";
    }

    public static volatile int streamFps;

    /** 被控端的 Sunshine 支持串流中途改帧率（电脑端 Farloop 0.21 起报告 LiveFramerate） */
    public static boolean liveFramerate(android.content.Context c, String host) {
        com.google.gson.JsonObject info = FrHub.get(c).deviceInfo(host);
        return info != null && info.has("LiveFramerate") && info.get("LiveFramerate").getAsBoolean();
    }

    /** 画面开始失败（例如偶尔的“无法连接 DESKTOP-xxx”）：同一台电脑 60 秒内自动重试一次，不弹错误框 */
    private static final java.util.Map<String, Long> lastAutoRetry = new java.util.concurrent.ConcurrentHashMap<>();

    public static boolean shouldAutoRetry(String host) {
        if (!FrHub.isFreeRemoteHost(host)) return false;
        long now = System.currentTimeMillis();
        Long last = lastAutoRetry.get(host);
        if (last != null && now - last < 60_000) return false;
        lastAutoRetry.put(host, now);
        return true;
    }

    public static void applyQuality(android.content.Context c, com.limelight.preferences.PreferenceConfiguration cfg) {
        int q = quality(c);
        if (q == QUALITY.length) {
            int r = customRes(c);
            if (r >= 1 && r <= 4) { cfg.width = QUALITY[r - 1][0]; cfg.height = QUALITY[r - 1][1]; }
            cfg.fps = customFps(c);
            cfg.bitrate = customKbps(c);
            cfg.meteredBitrate = cfg.bitrate;
            return;
        }
        if (q < 0 || q >= QUALITY.length) return;
        cfg.width = QUALITY[q][0];
        cfg.height = QUALITY[q][1];
        cfg.bitrate = QUALITY[q][2];
        cfg.meteredBitrate = cfg.bitrate;
    }

    // ---------------- 串流画面方向：横屏 / 竖屏 / 自动（跟随手机方向），记在 freeremote_stream 里 ----------------
    public static final int ORIENT_LANDSCAPE = 0, ORIENT_PORTRAIT = 1, ORIENT_AUTO = 2;

    public static int orientation(android.content.Context c) {
        return c.getSharedPreferences("freeremote_stream", android.content.Context.MODE_PRIVATE).getInt("orient", ORIENT_LANDSCAPE);
    }

    public static void setOrientation(android.content.Context c, int o) {
        c.getSharedPreferences("freeremote_stream", android.content.Context.MODE_PRIVATE).edit().putInt("orient", o).apply();
    }

    /** Game 用：要请求的 Activity 方向（替代 Artemis 自己“锁定为开始时方向”的逻辑） */
    public static int activityOrientation(android.content.Context c) {
        switch (orientation(c)) {
            case ORIENT_PORTRAIT: return android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT;
            case ORIENT_AUTO: return android.content.pm.ActivityInfo.SCREEN_ORIENTATION_FULL_USER;
            default: return android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE;
        }
    }
    private static volatile boolean startViewOnly;

    /** 设备页“观看模式”：下一次打开画面时先进入仅观看 */
    public static void setStartViewOnly(boolean on) { startViewOnly = on; }

    public static boolean takeStartViewOnly() { boolean v = startViewOnly; startViewOnly = false; return v; }

    /** 画面里切换了被控端屏幕：回到首页后自动重新连接这台电脑 */
    public static void setPendingReconnect(String ip) { pendingReconnect = ip; }

    public static String takePendingReconnect() {
        String s = pendingReconnect;
        pendingReconnect = null;
        return s;
    }

    /**
     * 视频包大小。FreeRemote 组网（10.144.144.x）是私网地址，Moonlight 会当成局域网用 1392 字节的大包；
     * 经 EasyTier 的 UDP 打洞隧道再封装后超过很多网络的路径 MTU，被分片后常被 NAT 丢弃，
     * 表现为 P2P 时画面不动（电脑端 0.11.0 已改成 1024，这里保持一致）。
     */
    public static int packetSize(String host) {
        return FrHub.isFreeRemoteHost(host) ? 1024 : 1392;
    }

    /** 画面已开始：让被控端在画面里画光标（手机没有本地光标，靠画面里的光标定位） */
    /** 码率自适应：被控端的 Farloop 报告了它的 Sunshine 支持串流中途改码率（Farloop 改过的版本），并且用户没关掉 */
    public static boolean adaptiveFor(android.content.Context c, String host) {
        if (!FrHub.isFreeRemoteHost(host)) return false;
        if (!adaptiveEnabled(c)) return false;
        com.google.gson.JsonObject info = FrHub.get(c).deviceInfo(host);
        boolean ok = info != null && info.has("AdaptiveBitrate") && info.get("AdaptiveBitrate").getAsBoolean();
        Log.i(TAG, "码率自适应：" + (ok ? "开启" : "被控端不支持（需要电脑端 Farloop 0.15 以上）"));
        return ok;
    }

    public static boolean adaptiveEnabled(android.content.Context c) {
        return c.getSharedPreferences("farloop", android.content.Context.MODE_PRIVATE).getBoolean("adaptive_bitrate", true);
    }

    public static void setAdaptiveEnabled(android.content.Context c, boolean on) {
        c.getSharedPreferences("farloop", android.content.Context.MODE_PRIVATE).edit().putBoolean("adaptive_bitrate", on).apply();
    }

    /** 正在串流（此时不自动重新组网，免得画面断开） */
    public static volatile boolean streaming;

    public static void onStreamStarted(Game game, String host) {
        streaming = true;
        if (!FrHub.isFreeRemoteHost(host)) return;
        new Thread(() -> FrHub.get(game).setLocalCursor(host, false, need -> {
            if (!need) return;
            NvConnection conn = game.conn;
            if (conn == null) return;
            Log.i(TAG, "让被控端在画面里显示光标（Ctrl+Alt+Shift+N）");
            sendCombo(conn, KeyboardTranslator.VK_A + ('N' - 'A'));
        }), "FrStreamStart").start();
    }

    public static void onStreamEnded(Game game, String host) {
        streaming = false;
        if (!FrHub.isFreeRemoteHost(host)) return;
        // 关闭控制会话：最后一个控制者离开时，被控端会结束 Sunshine 会话并还原 HDR 等显示设置
        new Thread(() -> {
            FrHub.get(game).close(host);
            // 远程协助：关掉画面 = 结束协助，退出临时组网（对方那边会自动换密码）
            if (FrAssist.isAssistIp(host)) FrAssist.stop();
        }, "FrStreamEnd").start();
    }

    /** Ctrl+Alt+Shift+键，经 Moonlight 发给 Sunshine（Sunshine 自己识别这个组合键） */
    static void sendCombo(NvConnection conn, int vk) {
        byte mods = (byte) (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT | KeyboardPacket.MODIFIER_SHIFT);
        try {
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LCONTROL, KeyboardPacket.KEY_DOWN, KeyboardPacket.MODIFIER_CTRL, (byte) 0);
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LMENU, KeyboardPacket.KEY_DOWN, (byte) (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT), (byte) 0);
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LSHIFT, KeyboardPacket.KEY_DOWN, mods, (byte) 0);
            conn.sendKeyboardInput((short) vk, KeyboardPacket.KEY_DOWN, mods, (byte) 0);
            Thread.sleep(40);
            conn.sendKeyboardInput((short) vk, KeyboardPacket.KEY_UP, mods, (byte) 0);
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LSHIFT, KeyboardPacket.KEY_UP, (byte) (KeyboardPacket.MODIFIER_CTRL | KeyboardPacket.MODIFIER_ALT), (byte) 0);
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LMENU, KeyboardPacket.KEY_UP, KeyboardPacket.MODIFIER_CTRL, (byte) 0);
            conn.sendKeyboardInput((short) KeyboardTranslator.VK_LCONTROL, KeyboardPacket.KEY_UP, (byte) 0, (byte) 0);
        } catch (InterruptedException ignored) { }
    }
}
