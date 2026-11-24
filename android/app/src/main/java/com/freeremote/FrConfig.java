package com.freeremote;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.util.Base64;

import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import java.util.regex.Pattern;

/** FreeRemote 安卓端配置。格式与电脑端 AppConfig 一致（邀请码、节点列表、主机名编码）。 */
public final class FrConfig {
    public static final String VERSION = "0.21.1";

    /** a 比 b 旧（与电脑端 PeerSession.IsOlder 相同） */
    public static boolean isOlder(String a, String b) {
        String[] x = (a == null ? "" : a).split("\\."), y = b.split("\\.");
        for (int i = 0; i < Math.max(x.length, y.length); i++) {
            int p = i < x.length ? parse(x[i]) : 0, q = i < y.length ? parse(y[i]) : 0;
            if (p != q) return p < q;
        }
        return false;
    }

    private static int parse(String s) { try { return Integer.parseInt(s.replaceAll("[^0-9].*$", "")); } catch (Exception e) { return 0; } }
    public static final String SUBNET = "10.144.144.0";

    /** 与电脑端 AppConfig.DefaultPeers 相同 */
    public static final String[] DEFAULT_PEERS = {
            "tcp://et.sumnight.top:11010",
            "tcp://183.230.36.171:11010",
            "tcp://easytier.weiai.org.cn:11010",
            "tcp://225284.xyz:11010",
            "wss://et.chinokou.cn",
    };

    private static final Pattern SAFE_TOKEN = Pattern.compile("^[A-Za-z0-9-]{8,64}$");
    private static final Pattern PEER_RX = Pattern.compile("^(tcp|udp|ws|wss|quic|wg|txt)://[A-Za-z0-9.\\-\\[\\]:]+(/[A-Za-z0-9._\\-/]*)?$");

    private final SharedPreferences sp;

    public FrConfig(Context ctx) {
        sp = ctx.getApplicationContext().getSharedPreferences("freeremote", Context.MODE_PRIVATE);
    }

    public String networkName() { return sp.getString("networkName", ""); }
    public String networkSecret() { return sp.getString("networkSecret", ""); }
    public String virtualIp() { return sp.getString("virtualIp", ""); }
    public String deviceName() { return sp.getString("deviceName", defaultDeviceName()); }
    public boolean forceRelay() { return sp.getBoolean("forceRelay", false); }
    public boolean isConfigured() { return !networkName().isEmpty() && !networkSecret().isEmpty() && !virtualIp().isEmpty(); }

    public List<String> peers() {
        String s = sp.getString("peers", "");
        List<String> out = new ArrayList<>();
        for (String p : s.split("\n")) if (PEER_RX.matcher(p.trim()).matches()) out.add(p.trim());
        if (out.isEmpty()) out.addAll(Arrays.asList(DEFAULT_PEERS));
        return out;
    }

    public void save(String name, String secret, String deviceName) {
        String ip = virtualIp();
        if (ip.isEmpty()) ip = randomIp();
        sp.edit().putString("networkName", name).putString("networkSecret", secret)
                .putString("deviceName", deviceName.trim().isEmpty() ? defaultDeviceName() : deviceName.trim())
                .putString("virtualIp", ip).apply();
    }

    public void setVirtualIp(String ip) { sp.edit().putString("virtualIp", ip).apply(); }

    /** VPN 模式：用安卓 VPN 接口建虚拟网卡（会占用 VPN）。默认关 = 免 VPN 的端口转发模式 */
    public boolean useVpn() { return sp.getBoolean("useVpn", false); }
    public void setUseVpn(boolean on) { sp.edit().putBoolean("useVpn", on).apply(); }

    /** 见过的电脑（虚拟 IP），免 VPN 模式下给它们建端口转发 */
    public java.util.Set<String> knownHosts() { return new java.util.TreeSet<>(sp.getStringSet("knownHosts", new java.util.HashSet<>())); }

    /** 新电脑：记下来，返回 true 表示列表变了（需要重建端口转发） */
    public boolean addKnownHost(String ip) {
        java.util.Set<String> s = knownHosts();
        if (!s.add(ip)) return false;
        sp.edit().putStringSet("knownHosts", s).apply();
        return true;
    }
    public void setForceRelay(boolean on) { sp.edit().putBoolean("forceRelay", on).apply(); }

    /** 仅 P2P：不经中转节点传数据（打洞不成功就连不上） */
    public boolean p2pOnly() { return sp.getBoolean("p2pOnly", false); }
    public void setP2pOnly(boolean on) { sp.edit().putBoolean("p2pOnly", on).apply(); }

    /** 打洞用的公网 IP 规则，例如 "203.0"；空 = 自动 */
    public String ipRule() { return sp.getString("ipRule", ""); }
    public void setIpRule(String r) { sp.edit().putString("ipRule", r == null ? "" : r.trim()).apply(); }
    public void setPeers(List<String> peers) { sp.edit().putString("peers", String.join("\n", peers)).apply(); }
    public void clear() { sp.edit().clear().apply(); }

    /** 已配对过的电脑（虚拟 IP） */
    public boolean isPaired(String ip) { return sp.getBoolean("paired_" + ip, false); }
    public void setPaired(String ip, boolean on) { sp.edit().putBoolean("paired_" + ip, on).apply(); }

    // ---------------- 邀请码：FR1- + base64url(网络名 \n 密钥)，与电脑端完全一致 ----------------
    /** 成功返回 {网络名, 密钥}，否则 null */
    public static String[] parseInvite(String code) {
        if (code == null) return null;
        code = code.trim();
        if (!code.startsWith("FR1-")) return null;
        try {
            String[] parts = new String(b64Dec(code.substring(4)), StandardCharsets.UTF_8).split("\n");
            if (parts.length != 2 || !SAFE_TOKEN.matcher(parts[0]).matches() || !SAFE_TOKEN.matcher(parts[1]).matches()) return null;
            return parts;
        } catch (Exception e) {
            return null;
        }
    }

    /**
     * 组网主机名：中文名 base64url 编进 ASCII。电脑用 “fr_”，手机用 “frm_”——
     * 电脑端（0.11.1 起）据此把手机显示成“手机”，不显示“连接 / 远程开机”。
     */
    public static String encodeHostname(String name) {
        if (name.length() > 40) name = name.substring(0, 40);
        return "frm_" + b64Enc(name.getBytes(StandardCharsets.UTF_8));
    }

    public static String decodeHostname(String host) {
        if (host == null) return "";
        String body = host.startsWith("frm_") ? host.substring(4) : host.startsWith("fr_") ? host.substring(3) : null;
        if (body != null) {
            try { return new String(b64Dec(body), StandardCharsets.UTF_8); } catch (Exception ignored) { }
        }
        return host;
    }

    public static boolean isMobileHostname(String host) { return host != null && host.startsWith("frm_"); }

    static String b64Enc(byte[] b) {
        return Base64.encodeToString(b, Base64.NO_WRAP | Base64.NO_PADDING | Base64.URL_SAFE);
    }

    static byte[] b64Dec(String s) {
        return Base64.decode(s, Base64.URL_SAFE | Base64.NO_PADDING | Base64.NO_WRAP);
    }

    /** 手机用 200–253 段，降低和电脑（随机 2–253）撞号的概率；入网后还会检查冲突 */
    public static String randomIp() {
        return "10.144.144." + (200 + new SecureRandom().nextInt(54));
    }

    /** 选一个不在 avoid 里、也不是已知电脑的虚拟 IP（电脑的号是 2–253 随机的，可能落在手机段里） */
    public String freshIp(java.util.Set<String> avoid) {
        java.util.Set<String> used = new java.util.HashSet<>(avoid);
        used.addAll(knownHosts());
        used.add(virtualIp());
        for (int i = 0; i < 200; i++) {
            String ip = randomIp();
            if (!used.contains(ip)) return ip;
        }
        return "10.144.144." + (150 + new SecureRandom().nextInt(50));
    }

    private static String defaultDeviceName() {
        String m = Build.MODEL == null ? "手机" : Build.MODEL;
        return m.length() > 24 ? m.substring(0, 24) : m;
    }
}
