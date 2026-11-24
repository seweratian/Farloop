using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FreeRemote.Core;

public sealed record PeerInfo(string Ip, string Hostname, string Cost, double? LatencyMs, string NatType, string Tunnel)
{
    public string LossRate { get; init; } = "";
    public bool IsLocal => Cost.Equals("Local", StringComparison.OrdinalIgnoreCase);
    public bool IsP2P => Cost.Contains("p2p", StringComparison.OrdinalIgnoreCase);
    public string DisplayName => EasyTier.DecodeHostname(Hostname);
    /// <summary>FreeRemote 安卓端（主机名 frm_ 开头）：只能控制别人，不能被连接、也不能远程开机</summary>
    public bool IsMobile => Hostname.StartsWith("frm_");
}

public static class EasyTier
{
    public const string Subnet = "10.144.144.0/24";

    public static string Dir => Path.Combine(AppConfig.DataDir, "easytier");
    public static string CoreExe => Path.Combine(Dir, "easytier-core.exe");
    public static string CliExe => Path.Combine(Dir, "easytier-cli.exe");
    public static bool Installed => File.Exists(CoreExe);
    public static bool IsRunning
    {
        get { var a = Assist.Pids; return Process.GetProcessesByName("easytier-core").Any(p => !a.Contains(p.Id)); }
    }

    // EasyTier 的主机名只放 ASCII，中文设备名用 base64url 编码进去，界面上再解码
    public static string EncodeHostname(string name)
    {
        if (name.Length > 40) name = name[..40];
        return "fr_" + B64.Enc(Encoding.UTF8.GetBytes(name));
    }

    public static string DecodeHostname(string host)
    {
        // fr_ = 电脑，frm_ = 手机（安卓端）
        var body = host.StartsWith("frm_") ? host[4..] : host.StartsWith("fr_") ? host[3..] : null;
        if (body != null)
        {
            try { return Encoding.UTF8.GetString(B64.Dec(body)); } catch { }
        }
        return host;
    }

    public static string BuildArgs(AppConfig cfg)
    {
        var sb = new StringBuilder();
        sb.Append($"--network-name {cfg.NetworkName} --network-secret {cfg.NetworkSecret} ");
        sb.Append($"-i {cfg.VirtualIp} --hostname {EncodeHostname(cfg.DeviceName)}");
        // 虚拟网卡 MTU 从默认 1360 降到 1280：UDP 打洞隧道的外层包控制在约 1360 以内，
        // 不会在 PPPoE / 移动网络上被分片（NAT 常丢分片，表现为 P2P 下大包丢失、中转正常）
        sb.Append(" --mtu 1280");
        if (cfg.NoUpnp) sb.Append(" --disable-upnp");
        if (cfg.ForceRelay) sb.Append(" --disable-p2p");
        else if (cfg.P2POnly) sb.Append(" --p2p-only");
        foreach (var p in cfg.Peers.Where(IsValidPeer)) sb.Append($" -p {p}");
        // 组网内核日志（打洞过程等），排查 P2P 问题用；最多 3 个 5MB 文件
        sb.Append($" --file-log-level info --file-log-dir {LogDir} --file-log-size 5 --file-log-count 3");
        return sb.ToString();
    }

    public static string LogDir => System.IO.Path.Combine(AppConfig.DataDir, "etlog");

    // 只接受 协议://主机[:端口][/路径] 这种格式，防止把奇怪字符写进开机脚本
    static readonly System.Text.RegularExpressions.Regex PeerRx =
        new(@"^(tcp|udp|ws|wss|quic|wg|txt)://[A-Za-z0-9.\-\[\]:]+(/[A-Za-z0-9._\-/]*)?$");
    public static bool IsValidPeer(string p) => PeerRx.IsMatch(p.Trim());

    /// <summary>公网 IP 规则：一到多段数字，多个规则用逗号分隔，例如 "203.0" 或 "203.0,198.51"</summary>
    static readonly System.Text.RegularExpressions.Regex IpRuleRx = new(@"^\d{1,3}(\.\d{1,3}){0,3}(,\d{1,3}(\.\d{1,3}){0,3})*$");
    public static string NormalizeIpRule(string? r)
    {
        r = (r ?? "").Replace(" ", "").Replace("，", ",").Replace(".*", "").Trim(',', '.');
        return IpRuleRx.IsMatch(r) ? r : "";
    }

    /// <summary>本机 EasyTier 通过 STUN 看到的公网 IPv4（多条宽带时会有多个）</summary>
    public static async Task<List<string>> GetPublicIpv4Async()
    {
        var list = new List<string>();
        if (!File.Exists(CliExe)) return list;
        try
        {
            var (_, o) = await Proc.RunAsync(CliExe, "node", 6000);
            foreach (var line in o.Split('\n'))
                if (line.Contains("Public IPv4"))
                {
                    var v = line.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
                    if (v != null && System.Net.IPAddress.TryParse(v, out _) && !list.Contains(v)) list.Add(v);
                }
        }
        catch { }
        return list;
    }

    /// <param name="rpc">查哪个 EasyTier（默认主组网；远程协助的进程用自己的 RPC 端口）</param>
    public static async Task<List<PeerInfo>> GetPeersAsync(string? rpc = null)
    {
        if (!File.Exists(CliExe)) return new();
        var p = rpc != null ? $"-p {rpc} " : "";
        try
        {
            var (code, json) = await Proc.RunAsync(CliExe, p + "-o json peer", 6000);
            var t = json.TrimStart();
            if (code == 0 && t.StartsWith("[")) return ParseJson(t);
            var (_, table) = await Proc.RunAsync(CliExe, p + "peer", 6000);
            return ParseTable(table);
        }
        catch (Exception e)
        {
            Log.Error("查询 EasyTier 节点失败", e);
            return new();
        }
    }

    static string Str(JsonElement e, params string[] names)
    {
        foreach (var p in e.EnumerateObject())
            foreach (var n in names)
                if (p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    return p.Value.ValueKind switch
                    {
                        JsonValueKind.String => p.Value.GetString() ?? "",
                        JsonValueKind.Null => "",
                        _ => p.Value.ToString(),
                    };
        return "";
    }

    static double? Num(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    static string CleanIp(string ip) => ip.Split('/')[0].Trim();

    public static List<PeerInfo> ParseJson(string json)
    {
        var list = new List<PeerInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            list.Add(new PeerInfo(
                CleanIp(Str(e, "ipv4", "virtual_ipv4")),
                Str(e, "hostname"),
                Str(e, "cost"),
                Num(Str(e, "lat_ms", "latency_ms")),
                Str(e, "nat_type", "udp_nat_type"),
                Str(e, "tunnel_proto", "tunnel"))
            { LossRate = Str(e, "loss_rate") });
        }
        return list;
    }

    // 兼容表格输出（│ 或 | 分隔）
    public static List<PeerInfo> ParseTable(string text)
    {
        var list = new List<PeerInfo>();
        var lines = text.Replace("\r", "").Split('\n');
        int hi = Array.FindIndex(lines, l => l.Contains("ipv4", StringComparison.OrdinalIgnoreCase) && l.Contains("hostname", StringComparison.OrdinalIgnoreCase));
        if (hi < 0) return list;
        char sep = lines[hi].Contains('│') ? '│' : '|';
        string[] Cells(string l) => l.Trim().Trim(sep).Split(sep).Select(c => c.Trim()).ToArray();
        var header = Cells(lines[hi]).Select(h => h.ToLowerInvariant()).ToArray();
        int Col(string n) => Array.FindIndex(header, h => h == n);
        int ci = Col("ipv4"), ch = Col("hostname"), cc = Col("cost"), cl = Col("lat_ms"), cn = Col("nat_type"), ct = Col("tunnel_proto");

        for (int i = hi + 1; i < lines.Length; i++)
        {
            if (!lines[i].Contains(sep)) continue;
            var c = Cells(lines[i]);
            if (c.Length != header.Length) continue;
            string G(int idx) => idx >= 0 ? c[idx] : "";
            list.Add(new PeerInfo(CleanIp(G(ci)), G(ch), G(cc), Num(G(cl)), G(cn), G(ct)));
        }
        return list;
    }
}
