using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// 远程协助（像 UU 远程 / 向日葵 / RustDesk 那样）：不在同一个组网里的陌生电脑，凭“设备码 + 临时密码”连进来。
///
/// 做法：设备码和临时密码换算成一个**临时组网**（网络名 farloop-assist-设备码，密钥 = 哈希(设备码, 密码)），
/// 被协助的电脑在里面是 10.145.0.1，协助者是 10.145.0.2。两边各起一个独立的 EasyTier 进程（自己的虚拟网卡、RPC 端口、监听端口），
/// 和自己平时的组网互不影响；经公共中转节点相遇，照样打洞走 P2P。之后的配对、串流、剪贴板、传文件都复用原来的流程。
/// 密码一换，网络密钥就变了，之前知道密码的人再也进不来；每次协助结束都会自动换密码。
///
/// 安全：被协助端每次有人请求控制都要点“允许”；协助会话不能用终端、浏览/管理文件、启动程序、远程开机这些“自己人”功能；
/// 协助结束后从 Sunshine 里删掉对方的配对证书。
/// </summary>
public static class Assist
{
    public const string Subnet = "10.145.0.0/24";
    public const string HostIp = "10.145.0.1";     // 被协助的电脑
    public const string HelperIp = "10.145.0.2";   // 协助者
    const int HostRpc = 15889, HelperRpc = 15890;
    const int HostListen = 11030, HelperListen = 11040;

    public static bool IsAssistIp(string? ip) => ip != null && ip.StartsWith("10.145.0.");

    // ---------------- 设备码 / 密码 ----------------
    const string PwChars = "abcdefghjkmnpqrstuvwxyz23456789"; // 去掉容易看错的 i l o 0 1

    public static string NewId()
    {
        var sb = new StringBuilder();
        sb.Append((char)('1' + RandomNumberGenerator.GetInt32(0, 9)));
        for (int i = 0; i < 8; i++) sb.Append((char)('0' + RandomNumberGenerator.GetInt32(0, 10)));
        return sb.ToString();
    }

    public static string NewPassword()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 6; i++) sb.Append(PwChars[RandomNumberGenerator.GetInt32(0, PwChars.Length)]);
        return sb.ToString();
    }

    public static string FormatId(string id) => id.Length == 9 ? $"{id[..3]} {id[3..6]} {id[6..]}" : id;
    public static string NormalizeId(string s) => new(s.Where(char.IsDigit).ToArray());
    public static string NormalizePassword(string s) => s.Trim().ToLowerInvariant();

    public static string NetworkName(string id) => "farloop-assist-" + id;

    public static string Secret(string id, string pw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("farloop-assist-v1|" + id + "|" + NormalizePassword(pw))))[..40].ToLowerInvariant();

    // ---------------- EasyTier 进程 ----------------
    public enum Role { Host, Helper }

    static readonly Dictionary<Role, Process> _procs = new();
    static string PidFile(Role r) => Path.Combine(AppConfig.DataDir, $"assist-{r.ToString().ToLowerInvariant()}.pid");

    /// <summary>所有协助用的 EasyTier 进程 ID（主组网的启停、“是否在运行”判断要排除它们）</summary>
    public static HashSet<int> Pids
    {
        get { lock (_procs) return _procs.Values.Where(p => { try { return !p.HasExited; } catch { return false; } }).Select(p => p.Id).ToHashSet(); }
    }

    public static string Rpc(Role r) => $"127.0.0.1:{(r == Role.Host ? HostRpc : HelperRpc)}";

    public static bool IsRunning(Role r)
    {
        lock (_procs) return _procs.TryGetValue(r, out var p) && !p.HasExited;
    }

    /// <summary>启动（或换密码后重启）一个协助用的 EasyTier</summary>
    public static void Start(Role r, AppConfig cfg, string id, string pw, string myName)
    {
        Stop(r);
        if (!File.Exists(EasyTier.CoreExe)) throw new InvalidOperationException("没有找到组网组件 EasyTier");
        int listen = r == Role.Host ? HostListen : HelperListen;
        var args = new StringBuilder();
        args.Append($"--network-name {NetworkName(id)} --network-secret {Secret(id, pw)} ");
        args.Append($"-i {(r == Role.Host ? HostIp : HelperIp)} --hostname {EasyTier.EncodeHostname(myName)} ");
        args.Append($"--dev-name farloop-assist-{(r == Role.Host ? "h" : "c")} --rpc-portal {Rpc(r)} --mtu 1280 ");
        args.Append($"-l tcp://0.0.0.0:{listen} -l udp://0.0.0.0:{listen}");
        if (cfg.NoUpnp) args.Append(" --disable-upnp");
        foreach (var p in cfg.Peers.Where(EasyTier.IsValidPeer)) args.Append($" -p {p}");
        var psi = new ProcessStartInfo(EasyTier.CoreExe, args.ToString())
        {
            WorkingDirectory = EasyTier.Dir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (cfg.EffectiveIpRule is { Length: > 0 } rule) psi.Environment["FR_PUBLIC_IP_PREFIX"] = rule;
        var proc = Process.Start(psi)!;
        proc.OutputDataReceived += (_, _) => { };
        proc.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data) && e.Data.Contains("ERROR")) Log.Info($"协助组网({r}): {e.Data}"); };
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        lock (_procs) _procs[r] = proc;
        try { File.WriteAllText(PidFile(r), proc.Id.ToString()); } catch { }
        Log.Info($"协助组网已启动（{(r == Role.Host ? "被协助" : "协助者")}，设备码 {FormatId(id)}）");
    }

    public static void Stop(Role r)
    {
        Process? p;
        lock (_procs) { _procs.TryGetValue(r, out p); _procs.Remove(r); }
        if (p != null) { try { if (!p.HasExited) { p.Kill(); p.WaitForExit(3000); } } catch { } }
        try { File.Delete(PidFile(r)); } catch { }
    }

    public static void StopAll() { Stop(Role.Host); Stop(Role.Helper); }

    /// <summary>上次 Farloop 异常退出留下的协助进程：按 PID 文件结束掉</summary>
    public static void CleanupStale()
    {
        foreach (var r in new[] { Role.Host, Role.Helper })
        {
            try
            {
                if (!File.Exists(PidFile(r))) continue;
                if (int.TryParse(File.ReadAllText(PidFile(r)), out var pid))
                {
                    try { var p = Process.GetProcessById(pid); if (p.ProcessName.Equals("easytier-core", StringComparison.OrdinalIgnoreCase)) p.Kill(); } catch { }
                }
                File.Delete(PidFile(r));
            }
            catch { }
        }
    }

    /// <summary>协助组网里的其他设备</summary>
    public static Task<List<PeerInfo>> PeersAsync(Role r) => EasyTier.GetPeersAsync(Rpc(r));
}
