package com.freeremote;

import android.content.Context;
import android.content.Intent;
import android.util.Log;

import com.easytier.jni.EasyTierJNI;
import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.util.ArrayList;
import java.util.List;

/**
 * 组网：在手机上跑一个 EasyTier 实例（官方安卓 JNI 库），配合 FrVpnService 提供虚拟网卡。
 * 参数与电脑端一致：同样的网络名/密钥/中转节点，MTU 1280（避免 UDP 打洞隧道分片），可选“仅中转”。
 */
public final class FrNet {
    public static final String INSTANCE = "freeremote";
    private static final String TAG = "FrNet";

    /** 组网里的一台设备 */
    public static final class Peer {
        public String ip = "";
        public String name = "";
        public boolean p2p;
        public Integer latencyMs;
        public boolean online = true;
        /** 对方 NAT 类型（EasyTier 的编号，0 = 未知）、公网 IPv4 个数 */
        public int nat;
        public int publicV4;
    }

    /** 本机 NAT 类型（peers() 时顺便更新） */
    public static volatile int myNat;
    /** 已经连上的中转节点个数、组网启动时间（用来在“等待电脑”时显示进度） */
    public static volatile int relayNodes;
    public static volatile long startedAt;
    private static volatile boolean loggedFirstPc;
    private static volatile boolean ipConflict;
    /** 组网里有别的设备和本机虚拟 IP 相同（比如两台手机随机到同一个号） */
    public static boolean takeIpConflict() { boolean r = ipConflict; ipConflict = false; return r; }

    private static volatile boolean running;
    private static volatile android.content.Context appCtx;
    private static volatile String lastError = "";

    public static boolean isRunning() { return running; }
    public static String lastError() { return lastError; }

    /** 监听端口被别的程序占用时（例如手机上还装了 EasyTier）退回不监听 */
    private static boolean withListeners = true;

    static String toml(FrConfig c) {
        StringBuilder sb = new StringBuilder();
        sb.append("instance_name = \"").append(INSTANCE).append("\"\n");
        sb.append("hostname = \"").append(FrConfig.encodeHostname(c.deviceName())).append("\"\n");
        sb.append("ipv4 = \"").append(c.virtualIp()).append("\"\n");
        sb.append("dhcp = false\n");
        sb.append("\n[network_identity]\n");
        sb.append("network_name = \"").append(c.networkName()).append("\"\n");
        sb.append("network_secret = \"").append(c.networkSecret()).append("\"\n");
        for (String p : c.peers()) sb.append("\n[[peer]]\nuri = \"").append(p).append("\"\n");
        // 和电脑端一样监听 11010（UDP/TCP）：EasyTier 的 UPnP 映射、“向监听端口打洞”都需要监听端口。
        // 以前手机端没有任何监听，是手机比电脑难打洞的原因之一
        if (withListeners) sb.append("listeners = [\"udp://0.0.0.0:11010\", \"tcp://0.0.0.0:11010\"]\n");
        sb.append("\n[flags]\n");
        sb.append("mtu = 1280\n");
        if (c.forceRelay()) sb.append("disable_p2p = true\n");
        else if (c.p2pOnly()) sb.append("p2p_only = true\n");
        if (!c.useVpn()) {
            // 免 VPN：不建虚拟网卡，每台电脑映射到本机 127.144.144.N，用到的端口逐个转发
            sb.append("no_tun = true\n");
            for (String ip : c.knownHosts()) {
                String local = endpointFor(ip);
                for (int[] pp : FORWARDS) {
                    sb.append("\n[[port_forward]]\n");
                    sb.append("bind_addr = \"").append(local).append(':').append(pp[0]).append("\"\n");
                    sb.append("dst_addr = \"").append(ip).append(':').append(pp[0]).append("\"\n");
                    sb.append("proto = \"").append(pp[1] == 0 ? "tcp" : "udp").append("\"\n");
                }
            }
        }
        return sb.toString();
    }

    /** 免 VPN 模式要转发的端口：{端口, 0=TCP 1=UDP}。FreeRemote 会话 + Sunshine（配对、应用列表、RTSP、视频、控制、音频） */
    static final int[][] FORWARDS = {
            {FrSession.PORT, 0}, {47984, 0}, {47989, 0}, {48010, 0},
            {47998, 1}, {47999, 1}, {48000, 1}, {48010, 1},
    };

    private static String endpointFor(String virtualIp) {
        return virtualIp.startsWith("10.144.144.") ? "127.144.144." + virtualIp.substring(11) : virtualIp;
    }

    /** 程序里实际去连的地址：免 VPN 模式下是本机映射地址，VPN 模式下就是虚拟 IP */
    public static String endpoint(android.content.Context ctx, String ip) {
        String v = toVirtual(ip);
        if (FrAssist.isAssistIp(v)) return "127.145.0." + v.substring(9); // 协助组网总是走端口转发
        return new FrConfig(ctx).useVpn() ? v : endpointFor(v);
    }

    /** 127.144.144.N → 10.144.144.N（界面和会话一律用虚拟 IP 当名字） */
    public static String toVirtual(String ip) {
        if (ip != null && ip.startsWith("127.145.0.")) return "10.145.0." + ip.substring(10);
        return ip != null && ip.startsWith("127.144.144.") ? "10.144.144." + ip.substring(12) : ip;
    }

    private static volatile boolean needRestart;
    /** 本次启动时已建好端口转发的电脑 */
    private static final java.util.Set<String> forwarded = java.util.Collections.synchronizedSet(new java.util.HashSet<>());

    /**
     * 免 VPN 模式：要连某台电脑前确保它的端口转发已建好。没有就把它记下、重启组网（约 1~3 秒）并等它的路由回来。
     * 以前是“一发现新电脑就重启”，重启期间设备列表会消失，第一次加入时看起来电脑上线很慢；现在改成真正要连时才重建。
     * 阻塞，别在界面线程调用。
     */
    /** 不重启组网，直接给正在运行的 EasyTier 加上这台电脑的端口转发（已经打通的 P2P 不受影响） */
    static boolean hotAdd(String ip) {
        if (forwarded.contains(ip)) return true;
        StringBuilder spec = new StringBuilder();
        String local = endpointFor(ip);
        for (int[] pp : FORWARDS)
            spec.append(pp[1] == 0 ? "tcp " : "udp ").append(local).append(':').append(pp[0]).append(' ').append(ip).append(':').append(pp[0]).append('\n');
        try {
            if (EasyTierJNI.addPortForwards(INSTANCE, spec.toString()) == 0) {
                forwarded.add(ip);
                Log.i(TAG, "已为 " + ip + " 加上端口转发（不重启组网）");
                return true;
            }
            Log.w(TAG, "运行中加端口转发失败：" + EasyTierJNI.getLastError());
        } catch (Throwable t) {
            Log.w(TAG, "运行中加端口转发失败", t);
        }
        return false;
    }

    public static void ensureForward(Context ctx, String ip) {
        ip = toVirtual(ip);
        FrConfig c = new FrConfig(ctx);
        if (c.useVpn() || forwarded.contains(ip)) return;
        c.addKnownHost(ip);
        if (running && hotAdd(ip)) return;
        Log.i(TAG, "给 " + ip + " 建端口转发，重启组网");
        synchronized (FrNet.class) {
            if (forwarded.contains(ip)) return;
            start(ctx);
        }
        long end = System.currentTimeMillis() + 20_000;
        while (System.currentTimeMillis() < end) {
            for (Peer p : peers(c.virtualIp())) if (p.ip.equals(ip)) { sleepQuiet(300); return; }
            sleepQuiet(250);
        }
    }

    private static void sleepQuiet(long ms) { try { Thread.sleep(ms); } catch (InterruptedException ignored) { } }
    /** 发现了新电脑（免 VPN 模式需要重建端口转发） */
    public static boolean takeNeedRestart() { boolean r = needRestart; needRestart = false; return r; }

    /** 启动组网（调用前 Activity 必须已经拿到 VpnService.prepare 的授权） */
    public static synchronized boolean start(Context ctx) {
        FrConfig c = new FrConfig(ctx);
        if (!c.isConfigured()) { lastError = "还没有加入网络"; return false; }
        // 本机的号和某台已知电脑一样（以前随机到的）：先换掉，否则那台电脑在线时两边都会连接错乱
        if (c.knownHosts().contains(c.virtualIp())) {
            String nip = c.freshIp(new java.util.HashSet<>());
            Log.w(TAG, "虚拟 IP " + c.virtualIp() + " 和已知电脑重复，换成 " + nip);
            c.setVirtualIp(nip);
        }
        stop(ctx);
        String cfg = toml(c);
        if (EasyTierJNI.parseConfig(cfg) != 0) {
            lastError = "组网配置无效：" + EasyTierJNI.getLastError();
            Log.e(TAG, lastError);
            return false;
        }
        // 手机上开着别的 VPN / 代理（Clash 等）：让组网直接走真实的 4G / Wi-Fi，不经过代理，否则打洞地址对不上，P2P 打不通
        bypassOtherVpn(ctx, c);
        // 打洞用的公网 IP 规则（多条宽带时只公布匹配的 IP），由打过补丁的 EasyTier 读取
        try { android.system.Os.setenv("FR_PUBLIC_IP_PREFIX", c.ipRule(), true); } catch (Exception e) { Log.w(TAG, "setenv", e); }
        if (EasyTierJNI.runNetworkInstance(cfg) != 0) {
            String err = EasyTierJNI.getLastError();
            if (withListeners) {
                Log.w(TAG, "带监听端口启动失败，改为不监听：" + err);
                withListeners = false;
                cfg = toml(c);
                if (EasyTierJNI.runNetworkInstance(cfg) == 0) err = null; else err = EasyTierJNI.getLastError();
            }
            if (err != null) {
                lastError = "组网启动失败：" + err;
                Log.e(TAG, lastError);
                return false;
            }
        }
        if (c.useVpn()) {
            Intent i = new Intent(ctx, FrVpnService.class);
            i.putExtra(FrVpnService.EXTRA_IP, c.virtualIp());
            ctx.startService(i);
        }
        running = true;
        lastError = "";
        startedAt = System.currentTimeMillis();
        relayNodes = 0;
        loggedFirstPc = false;
        forwarded.clear();
        if (!c.useVpn()) forwarded.addAll(c.knownHosts());
        appCtx = ctx.getApplicationContext();
        Log.i(TAG, "EasyTier 已启动：" + c.virtualIp() + (c.forceRelay() ? "（仅中转）" : "") + (c.useVpn() ? "（VPN 模式）" : "（免 VPN，转发 " + c.knownHosts().size() + " 台电脑）"));
        return true;
    }

    // ======================= 绕过手机上的其他 VPN / 代理 =======================
    /** 已经绕过了别的 VPN（组网走真实网络） */
    public static volatile boolean vpnBypassed;
    /** 手机开着别的 VPN，而且它不允许 App 绕过：只能请用户在代理 App 里把 Farloop 设为不走代理 */
    public static volatile boolean vpnBlocked;
    private static volatile android.net.Network boundNet;
    private static android.net.ConnectivityManager.NetworkCallback netCb, defaultCb;

    private static void bypassOtherVpn(Context ctx, FrConfig c) {
        android.net.ConnectivityManager cm = ctx.getSystemService(android.net.ConnectivityManager.class);
        if (cm == null) return;
        // 先解除旧的绑定，才能看到“没绑定时默认走的是不是 VPN”
        if (boundNet != null) { cm.bindProcessToNetwork(null); boundNet = null; }
        vpnBypassed = false;
        vpnBlocked = false;
        if (c.useVpn()) return; // Farloop 自己的 VPN 模式：流量要进自己的虚拟网卡，不能绑到真实网络
        watchNetworks(cm); // 之后用户开 / 关 VPN、切换网络都能自动跟上
        android.net.Network active = cm.getActiveNetwork();
        android.net.NetworkCapabilities ac = active == null ? null : cm.getNetworkCapabilities(active);
        if (ac == null || !ac.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)) return; // 没开别的 VPN，或者 Farloop 已被设为不走代理
        android.net.Network best = null;
        int bestScore = -1;
        for (android.net.Network n : cm.getAllNetworks()) {
            android.net.NetworkCapabilities nc = cm.getNetworkCapabilities(n);
            if (nc == null || nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN)
                    || !nc.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) continue;
            int score = (nc.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_VALIDATED) ? 10 : 0)
                    + (nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) ? 2 : 0)
                    + (nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_CELLULAR) ? 1 : 0);
            if (score > bestScore) { best = n; bestScore = score; }
        }
        if (best == null) { vpnBlocked = true; Log.w(TAG, "手机开着 VPN/代理，但没找到可以直连的真实网络"); return; }
        if (cm.bindProcessToNetwork(best)) {
            boundNet = best;
            vpnBypassed = true;
            Log.i(TAG, "检测到手机开着 VPN/代理，组网改走真实网络（绕过代理）");
        } else {
            vpnBlocked = true;
            Log.w(TAG, "手机开着 VPN/代理，而且它不允许绕过：P2P 可能打不通");
        }
    }

    /** 绑定的真实网络断了（Wi-Fi ↔ 4G 切换）或者别的 VPN 开 / 关了：重新组网，换到新的网络上 */
    private static void watchNetworks(android.net.ConnectivityManager cm) {
        if (netCb != null) return;
        netCb = new android.net.ConnectivityManager.NetworkCallback() {
            @Override public void onLost(android.net.Network n) {
                if (n.equals(boundNet)) restartLater("绑定的网络断开");
            }
        };
        defaultCb = new android.net.ConnectivityManager.NetworkCallback() {
            @Override public void onAvailable(android.net.Network n) {
                // 默认网络变了：可能是 VPN 刚开 / 刚关
                android.net.NetworkCapabilities nc = cm.getNetworkCapabilities(n);
                boolean vpn = nc != null && nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN);
                if (running && vpn != (boundNet != null || vpnBlocked)) restartLater(vpn ? "VPN 已开启" : "VPN 已关闭");
            }
        };
        try {
            cm.registerNetworkCallback(new android.net.NetworkRequest.Builder()
                    .addCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)
                    .build(), netCb); // 默认只匹配非 VPN 的真实网络
            cm.registerDefaultNetworkCallback(defaultCb);
        } catch (Exception e) { Log.w(TAG, "监听网络变化失败", e); }
    }

    private static long lastAutoRestart;
    private static void restartLater(String why) {
        Context ctx = appCtx;
        if (ctx == null || !running) return;
        long now = System.currentTimeMillis();
        if (now - lastAutoRestart < 5000) return;
        lastAutoRestart = now;
        Log.i(TAG, why + "，重新组网");
        new Thread(() -> { try { Thread.sleep(1500); } catch (InterruptedException ignored) { } if (running) start(ctx); }).start();
    }

    public static synchronized void stop(Context ctx) {
        try { ctx.stopService(new Intent(ctx, FrVpnService.class)); } catch (Exception ignored) { }
        // 远程协助的临时组网（FrAssist）在同一进程里，重启平时的组网时保留它
        try { EasyTierJNI.retainNetworkInstance(FrAssist.isRunning() ? new String[]{FrAssist.INSTANCE} : new String[0]); } catch (Throwable ignored) { }
        running = false;
    }

    /** 组网里的其他设备（不含本机），按名字排序 */
    public static List<Peer> peers(String myIp) {
        List<Peer> out = new ArrayList<>();
        String json;
        try { json = EasyTierJNI.collectNetworkInfos(10); } catch (Throwable t) { return out; }
        if (json == null || json.isEmpty()) return out;
        try {
            JsonObject root = JsonParser.parseString(json).getAsJsonObject();
            JsonObject map = root.has("map") ? root.getAsJsonObject("map") : root;
            JsonObject inst = map.has(INSTANCE) ? map.getAsJsonObject(INSTANCE) : null;
            if (inst == null) return out;
            if (inst.has("error_msg") && !inst.get("error_msg").isJsonNull())
                lastError = inst.get("error_msg").getAsString();
            JsonObject me = inst.has("my_node_info") && inst.get("my_node_info").isJsonObject() ? inst.getAsJsonObject("my_node_info") : null;
            JsonObject mst = me != null && me.has("stun_info") && me.get("stun_info").isJsonObject() ? me.getAsJsonObject("stun_info") : null;
            myNat = num(mst, "udp_nat_type");
            String myHost = appCtx != null ? FrConfig.encodeHostname(new FrConfig(appCtx).deviceName()) : null;
            JsonArray pairs = inst.has("peer_route_pairs") ? inst.getAsJsonArray("peer_route_pairs") : new JsonArray();
            int relays = 0;
            for (JsonElement e : pairs) {
                JsonObject pr0 = e.getAsJsonObject();
                JsonObject r0 = pr0.has("route") && pr0.get("route").isJsonObject() ? pr0.getAsJsonObject("route") : null;
                JsonObject pe0 = pr0.has("peer") && pr0.get("peer").isJsonObject() ? pr0.getAsJsonObject("peer") : null;
                if (r0 != null && str(r0, "hostname").startsWith("PublicServer") && pe0 != null && pe0.has("conns") && pe0.getAsJsonArray("conns").size() > 0) relays++;
            }
            if (relays != relayNodes) Log.i(TAG, "已连接中转节点 " + relays + " 个（启动后 " + (System.currentTimeMillis() - startedAt) / 1000 + " 秒）");
            relayNodes = relays;
            for (JsonElement e : pairs) {
                JsonObject pr = e.getAsJsonObject();
                JsonObject route = pr.has("route") && pr.get("route").isJsonObject() ? pr.getAsJsonObject("route") : null;
                if (route == null) continue;
                Peer p = new Peer();
                p.ip = ipv4(route.get("ipv4_addr"));
                String host = str(route, "hostname");
                if (p.ip.equals(myIp) && myHost != null && !host.isEmpty() && !host.equals(myHost)) {
                    // 别的设备占了本机的虚拟 IP：两边都会收发错乱（表现为显示 P2P 却 Connection reset）
                    Log.w(TAG, "虚拟 IP 冲突：" + myIp + " 也被 " + host + " 使用");
                    ipConflict = true;
                }
                if (p.ip.isEmpty() || p.ip.equals(myIp)) continue;
                JsonObject rs = route.has("stun_info") && route.get("stun_info").isJsonObject() ? route.getAsJsonObject("stun_info") : null;
                try {
                    if (rs != null) {
                        p.nat = num(rs, "udp_nat_type");
                        if (rs.has("public_ip") && rs.get("public_ip").isJsonArray())
                            for (JsonElement x : rs.getAsJsonArray("public_ip"))
                                if (x.isJsonPrimitive() && !x.getAsString().contains(":")) p.publicV4++;
                    }
                } catch (Exception ex) { Log.w(TAG, "读取对方 NAT 信息失败", ex); }
                if (FrConfig.isMobileHostname(host)) continue; // 其他手机不是可控制的电脑
                long peerId = route.has("peer_id") ? route.get("peer_id").getAsLong() : -1L;
                if (peerId != -1L) lastPeerId.put(p.ip, peerId);
                if (isGone(p.ip, peerId)) continue; // 刚退出 Farloop 的电脑
                p.name = FrConfig.decodeHostname(host);
                if (!loggedFirstPc) { loggedFirstPc = true; Log.i(TAG, "看到电脑 " + p.name + "（启动后 " + (System.currentTimeMillis() - startedAt) / 1000 + " 秒）"); }
                int cost = route.has("cost") ? route.get("cost").getAsInt() : 0;
                JsonObject peer = pr.has("peer") && pr.get("peer").isJsonObject() ? pr.getAsJsonObject("peer") : null;
                boolean direct = peer != null && peer.has("conns") && peer.getAsJsonArray("conns").size() > 0;
                p.p2p = direct && cost <= 1;
                p.latencyMs = latency(route, peer);
                out.add(p);
                // 免 VPN 模式：第一次见到这台电脑，要给它建端口转发
                if (appCtx != null) {
                    FrConfig c = new FrConfig(appCtx);
                    c.addKnownHost(p.ip);
                    // 一发现新电脑就在运行中加上端口转发：不用重启组网，点“连接”时也不会打断已经打通的 P2P
                    if (!c.useVpn() && !forwarded.contains(p.ip)) hotAdd(p.ip);
                }
            }
        } catch (Exception ex) {
            Log.w(TAG, "解析组网信息失败", ex);
        }
        // 电脑重启组网后，旧节点的路由要过一会儿才消失：同一个虚拟 IP 只留一个（优先 P2P、其次延迟低的）
        java.util.Map<String, Peer> byIp = new java.util.LinkedHashMap<>();
        for (Peer p : out) {
            Peer o = byIp.get(p.ip);
            if (o == null || (p.p2p && !o.p2p) || (p.p2p == o.p2p && p.latencyMs != null && (o.latencyMs == null || p.latencyMs < o.latencyMs)))
                byIp.put(p.ip, p);
        }
        out = new ArrayList<>(byIp.values());
        // 打洞计时写进日志，方便看到底花了多久
        long now = System.currentTimeMillis();
        for (Peer p : out) {
            Long since = firstSeen.get(p.ip);
            if (since == null) { firstSeen.put(p.ip, now); since = now; }
            Boolean was = wasP2p.put(p.ip, p.p2p);
            if (p.p2p && (was == null || !was))
                Log.i(TAG, p.name + " P2P 打通（看到它后 " + (now - since) / 1000.0 + " 秒，启动后 " + (now - startedAt) / 1000.0 + " 秒）");
            else if (!p.p2p && was != null && was)
                Log.i(TAG, p.name + " P2P 断开，暂时走中转（启动后 " + (now - startedAt) / 1000 + " 秒）");
        }
        punchWatchdog(out, now);
        out.sort((a, b) -> a.name.compareToIgnoreCase(b.name));
        return out;
    }

    // ---- 说过“再见”的电脑：记下它当时的节点 ID，路由超时前也不显示；它重开 Farloop 后节点 ID 会变，自然重新出现
    private static final java.util.Map<String, Long> gonePeerId = new java.util.concurrent.ConcurrentHashMap<>();
    private static final java.util.Map<String, Long> lastPeerId = new java.util.concurrent.ConcurrentHashMap<>();
    private static final java.util.Map<String, Long> goneAt = new java.util.concurrent.ConcurrentHashMap<>();

    public static void markGone(String ip) {
        Long id = lastPeerId.get(ip);
        gonePeerId.put(ip, id != null ? id : -1L);
        goneAt.put(ip, System.currentTimeMillis());
    }

    private static boolean isGone(String ip, long peerId) {
        Long g = gonePeerId.get(ip);
        if (g == null) return false;
        Long at = goneAt.get(ip);
        // 记不到节点 ID 时只隐藏 3 分钟；节点 ID 变了 = 它重新上线了
        if ((g == -1L && at != null && System.currentTimeMillis() - at > 180_000) || (g != -1L && g != peerId)) {
            gonePeerId.remove(ip); goneAt.remove(ip);
            return false;
        }
        return true;
    }

    // ---- 自动重新组网：P2P 掉线后（比如 App 在后台被系统冻结、手机网络切换），EasyTier 的重试常常再也打不通
    //      （NAT 映射和 STUN 结果都旧了），而重新组网 10 秒左右就能打通。中转超过 20 秒就重来，没打通最多连试 3 次
    private static final java.util.Map<String, Long> relayFrom = new java.util.concurrent.ConcurrentHashMap<>();
    private static int watchdogRestarts;
    private static long lastWatchdog;
    private static volatile long fastUntil;

    private static void punchWatchdog(List<Peer> list, long now) {
        if (appCtx == null || !running || FrStreamHooks.streaming) return;
        if (new FrConfig(appCtx).forceRelay()) return;
        boolean anyP2p = false;
        String stuck = null;
        for (Peer p : list) {
            if (p.p2p) { relayFrom.remove(p.ip); anyP2p = true; continue; }
            Long f = relayFrom.get(p.ip);
            if (f == null) { relayFrom.put(p.ip, now); continue; }
            if (now - f > (now < fastUntil ? 0 : 20_000)) stuck = p.name;
        }
        if (anyP2p && stuck == null) watchdogRestarts = 0;
        if (stuck == null || now - startedAt < 30_000 || watchdogRestarts >= 3) return;
        if (now - lastWatchdog < 60_000 && now >= fastUntil) return;
        fastUntil = 0;
        lastWatchdog = now;
        watchdogRestarts++;
        relayFrom.clear();
        restartLater(stuck + " 在走中转、P2P 没打通（第 " + watchdogRestarts + " 次自动重新组网）");
    }

    /** 回到前台时调用：在后台待久了，连接多半已被系统掐断，允许看门狗重新计次 */
    public static void onForeground(long bgMs) {
        if (bgMs <= 60_000) return;
        // 后台待了 1 分钟以上：系统可能冻结过 App，P2P 和 NAT 映射会失效。接下来 15 秒内一发现掉到中转就立刻重新组网
        //（重新组网约 10 秒打通；P2P 还好好的就什么都不做，比如刚看完远程画面回来）
        watchdogRestarts = 0; relayFrom.clear();
        fastUntil = System.currentTimeMillis() + 15_000;
    }

    private static final java.util.Map<String, Long> firstSeen = new java.util.concurrent.ConcurrentHashMap<>();
    private static final java.util.Map<String, Boolean> wasP2p = new java.util.concurrent.ConcurrentHashMap<>();

    private static final String[] NAT_NAMES = {"未知", "公网直连", "公网（无端口转换）", "全锥形 NAT1", "受限锥形 NAT2",
            "端口受限锥形 NAT3", "对称型 NAT4", "对称型（UDP 受限）", "对称型（端口递增，较易打洞）", "对称型（端口递减，较易打洞）"};

    public static String natName(int t) { return t >= 0 && t < NAT_NAMES.length ? NAT_NAMES[t] : "未知"; }

    /** 网络诊断：本机 NAT 类型、公网 IP、每台电脑的连接方式（“我的 → 网络诊断”） */
    public static String diagnose(String myIp) {
        StringBuilder sb = new StringBuilder();
        String json;
        try { json = EasyTierJNI.collectNetworkInfos(10); } catch (Throwable t) { return "组网未运行"; }
        try {
            JsonObject root = JsonParser.parseString(json).getAsJsonObject();
            JsonObject map = root.has("map") ? root.getAsJsonObject("map") : root;
            JsonObject inst = map.has(INSTANCE) ? map.getAsJsonObject(INSTANCE) : null;
            if (inst == null) return "组网未运行";
            JsonObject me = inst.has("my_node_info") && inst.get("my_node_info").isJsonObject() ? inst.getAsJsonObject("my_node_info") : null;
            JsonObject stun = me != null && me.has("stun_info") && me.get("stun_info").isJsonObject() ? me.getAsJsonObject("stun_info") : null;
            int nat = num(stun, "udp_nat_type");
            sb.append("本机 NAT 类型：").append(natName(nat)).append('\n');
            if (stun != null && stun.has("public_ip")) sb.append("本机公网 IP：").append(stun.get("public_ip").toString().replaceAll("[\\[\\]\"]", "")).append('\n');
            if (nat == 6 || nat == 7) sb.append("提示：对称型 NAT 只能和锥形 NAT（NAT1~3）打洞，对方是多条宽带时成功率更低。\n");
            sb.append("手机 VPN / 代理：").append(vpnBypassed ? "检测到，已绕过（组网走真实网络）" : vpnBlocked ? "检测到，但它不允许绕过，P2P 可能打不通" : "未检测到").append('\n');
            sb.append('\n');
            JsonArray pairs = inst.has("peer_route_pairs") ? inst.getAsJsonArray("peer_route_pairs") : new JsonArray();
            for (JsonElement e : pairs) {
                JsonObject pr = e.getAsJsonObject();
                JsonObject route = pr.has("route") && pr.get("route").isJsonObject() ? pr.getAsJsonObject("route") : null;
                if (route == null) continue;
                String ip = ipv4(route.get("ipv4_addr"));
                String host = str(route, "hostname");
                if (ip.isEmpty() || ip.equals(myIp) || FrConfig.isMobileHostname(host)) continue;
                JsonObject peer = pr.has("peer") && pr.get("peer").isJsonObject() ? pr.getAsJsonObject("peer") : null;
                boolean direct = peer != null && peer.has("conns") && peer.getAsJsonArray("conns").size() > 0;
                int cost = route.has("cost") ? route.get("cost").getAsInt() : 0;
                JsonObject rs = route.has("stun_info") && route.get("stun_info").isJsonObject() ? route.getAsJsonObject("stun_info") : null;
                int pn = num(rs, "udp_nat_type");
                sb.append(FrConfig.decodeHostname(host)).append("（").append(ip).append("）\n");
                sb.append("  连接：").append(direct && cost <= 1 ? "P2P 直连" : "中转").append("\n");
                sb.append("  对方 NAT：").append(natName(pn));
                if (rs != null && rs.has("public_ip") && rs.getAsJsonArray("public_ip").size() > 0) {
                    JsonArray ips = rs.getAsJsonArray("public_ip");
                    int v4 = 0;
                    for (JsonElement x : ips) if (!x.getAsString().contains(":")) v4++;
                    sb.append("，公网 IP ").append(ips.toString().replaceAll("[\\[\\]\"]", ""));
                    if (v4 > 1) sb.append("\n  ⚠ 对方有 ").append(v4).append(" 个公网 IP（多条宽带），打洞容易失败，建议在对方电脑上设置“打洞用的公网 IP”");
                }
                sb.append("\n\n");
            }
        } catch (Exception ex) {
            sb.append("读取失败：").append(ex.getMessage());
        }
        return sb.toString().trim();
    }

    /** 调试用：原始组网信息 */
    public static String rawInfo() {
        try { return EasyTierJNI.collectNetworkInfos(10); } catch (Throwable t) { return t.toString(); }
    }

    private static Integer latency(JsonObject route, JsonObject peer) {
        // 直连：取连接统计里的延迟（微秒）；中转：路由的路径延迟（毫秒）
        try {
            if (peer != null && peer.has("conns")) {
                for (JsonElement c : peer.getAsJsonArray("conns")) {
                    JsonObject co = c.getAsJsonObject();
                    if (co.has("stats") && co.get("stats").isJsonObject()) {
                        JsonObject st = co.getAsJsonObject("stats");
                        if (st.has("latency_us")) return (int) Math.round(st.get("latency_us").getAsDouble() / 1000.0);
                    }
                }
            }
            if (route.has("path_latency")) {
                int v = route.get("path_latency").getAsInt();
                return v > 0 ? v : null;
            }
        } catch (Exception ignored) { }
        return null;
    }

    /** Ipv4Inet {address:{addr:uint32}, network_length} 或直接字符串 */
    /** 安全读取数字字段：缺失、null、字符串（枚举名）都不抛异常 */
    static int num(JsonObject o, String k) {
        if (o == null || !o.has(k) || o.get(k).isJsonNull() || !o.get(k).isJsonPrimitive()) return 0;
        try { return o.get(k).getAsInt(); } catch (Exception e) {
            String v = o.get(k).getAsString();
            for (int i = 0; i < NAT_ENUM.length; i++) if (NAT_ENUM[i].equalsIgnoreCase(v)) return i;
            return 0;
        }
    }

    private static final String[] NAT_ENUM = {"Unknown", "OpenInternet", "NoPAT", "FullCone", "Restricted", "PortRestricted",
            "Symmetric", "SymUdpFirewall", "SymmetricEasyInc", "SymmetricEasyDec"};

    static String ipv4(JsonElement e) {
        if (e == null || e.isJsonNull()) return "";
        if (e.isJsonPrimitive()) {
            String s = e.getAsString();
            int slash = s.indexOf('/');
            return slash > 0 ? s.substring(0, slash) : s;
        }
        JsonObject o = e.getAsJsonObject();
        JsonElement a = o.has("address") ? o.get("address") : o;
        if (a.isJsonObject() && a.getAsJsonObject().has("addr")) {
            long v = a.getAsJsonObject().get("addr").getAsLong() & 0xFFFFFFFFL;
            return ((v >> 24) & 255) + "." + ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
        }
        return "";
    }

    private static String str(JsonObject o, String k) {
        return o.has(k) && !o.get(k).isJsonNull() ? o.get(k).getAsString() : "";
    }
}
