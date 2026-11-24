using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// 出口检测：用多个本地端口向几台公共 STUN 服务器发绑定请求，统计每次看到的公网 IP。
/// 多个公网 IP = 多条宽带 / 负载均衡；同一台服务器从不同端口看到不同 IP = 路由器按连接随机选线路（对打洞最不利）。
/// 出现次数最多的 IP 当作主线路，建议只用它打洞。
/// </summary>
public static class NetProbe
{
    static readonly (string Host, int Port)[] Servers =
    {
        ("stun.miwifi.com", 3478), ("stun.chat.bilibili.com", 3478), ("stun.hitv.com", 3478),
        ("stun.qq.com", 3478), ("stun.cdnbye.com", 3478), ("stun.douyucdn.cn", 18000),
    };

    public sealed record Result(List<(string Ip, int Count)> Ips, int Total, bool PerFlow)
    {
        public bool Multi => Ips.Count > 1;
        public string? Main => Ips.Count > 0 ? Ips[0].Ip : null;

        /// <summary>建议的打洞 IP 规则：能区分出主线路的最短前缀（通常前两段，运营商动态 IP 前两段一般不变）</summary>
        public string? SuggestedRule
        {
            get
            {
                if (!Multi || Main == null) return null;
                var parts = Main.Split('.');
                for (int n = 2; n <= 4; n++)
                {
                    var prefix = string.Join(".", parts.Take(n));
                    if (Ips.Skip(1).All(x => !x.Ip.StartsWith(prefix + ".") && x.Ip != prefix)) return prefix;
                }
                return Main;
            }
        }
    }

    public static async Task<Result> RunAsync(int socketsPerServer = 3)
    {
        var counts = new Dictionary<string, int>();
        bool perFlow = false;
        int total = 0;
        var tasks = Servers.Select(async s =>
        {
            IPAddress[] addrs;
            try { addrs = (await Dns.GetHostAddressesAsync(s.Host)).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray(); }
            catch { return new List<string>(); }
            if (addrs.Length == 0) return new List<string>();
            var ep = new IPEndPoint(addrs[0], s.Port);
            var seen = new List<string>();
            for (int i = 0; i < socketsPerServer; i++)
            {
                var ip = await QueryAsync(ep);
                if (ip != null) seen.Add(ip);
            }
            return seen;
        }).ToList();
        foreach (var t in tasks)
        {
            var seen = await t;
            if (seen.Distinct().Count() > 1) perFlow = true;
            foreach (var ip in seen) { counts[ip] = counts.GetValueOrDefault(ip) + 1; total++; }
        }
        var list = counts.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList();
        Log.Info($"出口检测：{string.Join("，", list.Select(x => $"{x.Key}×{x.Value}"))}" + (perFlow ? "（同一服务器看到不同 IP：按连接随机选线路）" : ""));
        return new Result(list, total, perFlow);
    }

    /// <summary>一次 STUN Binding 请求（RFC 5389），返回对方看到的公网 IPv4</summary>
    static async Task<string?> QueryAsync(IPEndPoint server)
    {
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var req = new byte[20];
            req[0] = 0x00; req[1] = 0x01;                    // Binding Request
            req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42; // magic cookie
            RandomNumberGenerator.Fill(req.AsSpan(8, 12));
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await udp.SendAsync(req, req.Length, server);
                var recv = udp.ReceiveAsync();
                if (await Task.WhenAny(recv, Task.Delay(1500)) != recv)
                {
                    // 超时没回包：这个接收任务会在关闭 socket 时出错，这里接住，免得日志里刷“后台异常”
                    _ = recv.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    continue;
                }
                var b = recv.Result.Buffer;
                if (b.Length < 20 || b[0] != 0x01 || b[1] != 0x01) continue;
                int len = (b[2] << 8) | b[3], pos = 20;
                while (pos + 4 <= 20 + len && pos + 4 <= b.Length)
                {
                    int type = (b[pos] << 8) | b[pos + 1], alen = (b[pos + 2] << 8) | b[pos + 3];
                    int v = pos + 4;
                    if ((type == 0x0020 || type == 0x0001) && alen >= 8 && b[v + 1] == 0x01)
                    {
                        var ip = new byte[] { b[v + 4], b[v + 5], b[v + 6], b[v + 7] };
                        if (type == 0x0020) { ip[0] ^= 0x21; ip[1] ^= 0x12; ip[2] ^= 0xA4; ip[3] ^= 0x42; }
                        return new IPAddress(ip).ToString();
                    }
                    pos = v + ((alen + 3) & ~3);
                }
            }
        }
        catch { }
        return null;
    }
}
