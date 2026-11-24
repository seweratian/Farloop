using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>被控端：Sunshine（NVENC 硬件编码串流服务）</summary>
public static class Sunshine
{
    public static string Exe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "sunshine.exe");
    public static bool Installed => File.Exists(Exe);

    /// <summary>用命令行设置 Sunshine 的网页管理账号，用户不用手动去网页注册。</summary>
    public static async Task<(bool Ok, string Message)> SetCredentialsAsync(string user, string pass)
    {
        if (!Installed) return (false, "未安装 Sunshine");
        var (code, output) = await Proc.RunAsync(Exe, $"--creds {user} {pass}", 30_000, Path.GetDirectoryName(Exe));
        Log.Info($"sunshine --creds 退出码 {code}: {output.Trim()}");
        await Proc.RunAsync("net", "stop SunshineService", 30_000);
        await Proc.RunAsync("sc", "config SunshineService start= auto", 10_000);
        await Proc.RunAsync("net", "start SunshineService", 30_000);
        return (code == 0, code == 0 ? "已设置" : $"设置失败（退出码 {code}）");
    }

    const string Api = "https://localhost:47990/api/pin";

    /// <summary>删除 Sunshine 里某个名字的配对（远程协助结束后删掉协助者的证书）</summary>
    public static async Task<(bool Ok, string Message)> UnpairByNameAsync(AppConfig cfg, string name)
    {
        if (!Installed || cfg.SunshineUser == "") return (false, "没有 Sunshine 账号");
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.SunshineUser}:{cfg.SunshinePassword}")));
            http.DefaultRequestHeaders.Add("Origin", "https://localhost:47990"); // 同源请求不需要 CSRF 令牌
            var list = await http.GetStringAsync("https://localhost:47990/api/clients/list");
            using var doc = System.Text.Json.JsonDocument.Parse(list);
            if (!doc.RootElement.TryGetProperty("named_certs", out var certs)) return (false, "读取配对列表失败");
            int removed = 0;
            foreach (var c in certs.EnumerateArray())
            {
                if (!c.TryGetProperty("name", out var n) || n.GetString() != name) continue;
                var uuid = c.TryGetProperty("uuid", out var u) ? u.GetString() : null;
                if (uuid == null) continue;
                var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { uuid }), Encoding.UTF8, "application/json");
                var r = await http.PostAsync("https://localhost:47990/api/clients/unpair", body);
                if (r.IsSuccessStatusCode) removed++;
            }
            return (removed > 0, removed > 0 ? "已删除" : "没找到这个配对");
        }
        catch (Exception e) { return (false, e.Message); }
    }

    public static bool IsRunning => Process.GetProcessesByName("sunshine").Length > 0;

    public static string ConfPath => Path.Combine(Path.GetDirectoryName(Exe)!, "config", "sunshine.conf");

    // 默认 nvenc_preset=1（最快、同码率下画质最差）。P4 在 40 系上只多约 1ms 编码时间，同码率明显更清晰；
    // spatial AQ 让平坦区域少占码率，低码率下更稳。只在用户没设置过时写入。
    static readonly (string Key, string Value)[] Tuning =
    {
        ("nvenc_preset", "4"),
        ("nvenc_spatial_aq", "enabled"),
        // 被控端是 HDR 屏、控制端不是时，HDR 桌面转 SDR 会按“SDR 内容亮度”换算而严重过曝（已实测）。
        // 让 Sunshine 按控制端的请求自动开关被控端 HDR（总开关 verify_only 才会生效），
        // 但不改分辨率和刷新率（默认会改成控制端请求的值，实测把 4K 160Hz 改成了 720p 60Hz）。
        ("dd_configuration_option", "verify_only"),
        ("dd_hdr_option", "auto"),
        ("dd_resolution_option", "disabled"),
        ("dd_refresh_rate_option", "disabled"),
    };

    /// <summary>
    /// 结束 Sunshine 当前的应用会话。Sunshine 只在应用关闭时还原显示设置（HDR），
    /// 控制端断开后如果不关，被控端的 HDR 会一直处于关闭状态。
    /// </summary>
    public static async Task CloseAppAsync(AppConfig cfg)
    {
        if (!Installed || cfg.SunshineUser == "") return;
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.SunshineUser}:{cfg.SunshinePassword}")));
            using var r = await http.PostAsync("https://localhost:47990/api/apps/close", new ByteArrayContent(Array.Empty<byte>()));
            Log.Info($"远程结束，关闭 Sunshine 会话（还原 HDR）：{(int)r.StatusCode}");
        }
        catch (Exception e) { Log.Error("关闭 Sunshine 会话失败", e); }
    }

    /// <summary>被控端启动时调用：补上画质调优参数，有改动就重启 Sunshine</summary>
    public static async Task<bool> ApplyTuningAsync()
    {
        if (!Installed) return false;
        var lines = File.Exists(ConfPath) ? File.ReadAllLines(ConfPath).ToList() : new System.Collections.Generic.List<string>();
        bool changed = false;
        foreach (var (k, v) in Tuning)
        {
            if (lines.Any(l => l.Split('=')[0].Trim().Equals(k, StringComparison.OrdinalIgnoreCase))) continue;
            lines.Add($"{k} = {v}");
            changed = true;
        }
        if (!changed) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(ConfPath)!);
        File.WriteAllLines(ConfPath, lines);
        Log.Info("已写入 Sunshine 画质调优参数，重启 Sunshine");
        await Proc.RunAsync("net", "stop SunshineService", 30_000);
        await Proc.RunAsync("net", "start SunshineService", 30_000);
        return true;
    }

    static DateTime _lastStartAttempt = DateTime.MinValue;

    /// <summary>
    /// 看门狗：Sunshine 被人从托盘退出或崩溃后自动拉起（实测：托盘“退出”会把服务停掉，之后再也连不上）。
    /// </summary>
    /// <remarks>
    /// Windows 上 SunshineService 只是个“启动器”（tools\sunshinesvc.exe），真正干活的 sunshine.exe 由它拉起。
    /// sunshine.exe 一启动就退出时服务仍显示“正在运行”，以前只 sc start 一下什么都不会发生，日志里无限重复“正在启动服务”。
    /// 现在：服务不存在就重新注册；服务在运行但没有 sunshine.exe 就重启服务；还是起不来就从 Sunshine 自己的日志里找出原因。
    /// </remarks>
    /// <returns>null = 在运行（或这次不用试）；否则是起不来的原因</returns>
    public static async Task<string?> EnsureRunningAsync()
    {
        if (!Installed || IsRunning) return null;
        if (DateTime.UtcNow - _lastStartAttempt < TimeSpan.FromSeconds(20)) return null;
        _lastStartAttempt = DateTime.UtcNow;
        // 服务登记的程序（tools\sunshinesvc.exe）不见了：多半是被杀毒 / 公司安全软件删了。重装一次同版本 Sunshine 补回文件（配置和配对都保留）
        var svcExe = await ServiceBinaryAsync();
        if (svcExe != null && !File.Exists(svcExe) || !File.Exists(Exe))
        {
            if (_repairTried)
                return $"Sunshine 的文件 {(svcExe != null && !File.Exists(svcExe) ? svcExe : Exe)} 又不见了：被杀毒软件或公司安全软件删除了。请把 {Path.GetDirectoryName(Exe)} 加入白名单（或请公司 IT 放行 Sunshine）";
            _repairTried = true;
            Log.Info($"Sunshine 服务程序不存在（{svcExe}），重新安装 Sunshine 修复");
            await Installer.WingetInstallAsync("LizardByte.Sunshine", SunshineFarloop.BaseVersion, force: true);
            var script = Path.Combine(Path.GetDirectoryName(Exe)!, "scripts", "install-service.bat");
            if (File.Exists(script)) await Proc.RunAsync("cmd.exe", $"/c \"\"{script}\"\"", 60_000, Path.GetDirectoryName(script));
            svcExe = await ServiceBinaryAsync();
            if (svcExe != null && !File.Exists(svcExe))
                return $"重新安装后 {svcExe} 仍然不存在：被杀毒软件或公司安全软件删除了。请把 {Path.GetDirectoryName(Exe)} 加入白名单（或请公司 IT 放行 Sunshine）";
            Log.Info("Sunshine 文件已修复");
        }
        var (qc, q) = await Proc.RunAsync("sc", "query SunshineService", 10_000);
        if (q.Contains("1060") || (qc != 0 && !q.Contains("STATE")))
        {
            Log.Info("Sunshine 服务不存在，重新注册");
            var script = Path.Combine(Path.GetDirectoryName(Exe)!, "scripts", "install-service.bat");
            if (File.Exists(script)) await Proc.RunAsync("cmd.exe", $"/c \"\"{script}\"\"", 60_000, Path.GetDirectoryName(script));
            await Proc.RunAsync("sc", "config SunshineService start= auto", 10_000);
            await Proc.RunAsync("net", "start SunshineService", 30_000);
        }
        else if (q.Contains("RUNNING"))
        {
            Log.Info("Sunshine 服务在运行但 sunshine.exe 没有运行，重启服务");
            await Proc.RunAsync("net", "stop SunshineService", 30_000);
            await Proc.RunAsync("net", "start SunshineService", 30_000);
        }
        else
        {
            Log.Info("Sunshine 未运行，正在启动服务");
            await Proc.RunAsync("sc", "config SunshineService start= auto", 10_000);
            await Proc.RunAsync("net", "start SunshineService", 30_000);
        }
        for (int i = 0; i < 40 && !IsRunning; i++) await Task.Delay(500);
        if (IsRunning) { Log.Info("Sunshine 已启动"); return null; }
        var why = await DiagnoseAsync();
        Log.Error("Sunshine 启动失败：" + why);
        _lastStartAttempt = DateTime.UtcNow + TimeSpan.FromMinutes(2); // 起不来时别每 20 秒重启一次服务
        return why;
    }

    public static string LogPath => Path.Combine(Path.GetDirectoryName(Exe)!, "config", "sunshine.log");

    /// <summary>
    /// 清掉 Sunshine 记着的“要还原的显示设置”（/api/reset-display-device-persistence）。
    /// 实测：它记着一个早已不存在的显示器，每次启动、每次结束串流、每次显示器增减都去“还原”，还原失败就“启用所有显示器”，
    /// 息屏时刚撤下的实体屏就被它接回来了。Farloop 启动时和每次息屏前清一次
    /// </summary>
    public static async Task ResetDisplayPersistenceAsync(AppConfig cfg)
    {
        if (!Installed || !IsRunning || cfg.SunshineUser == "") return;
        try
        {
            using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.SunshineUser}:{cfg.SunshinePassword}")));
            http.DefaultRequestHeaders.Add("Origin", "https://localhost:47990");
            var r = await http.PostAsync("https://localhost:47990/api/reset-display-device-persistence", new StringContent("", Encoding.UTF8, "application/json"));
            Log.Info($"清除 Sunshine 记着的显示设置：{(int)r.StatusCode}");
        }
        catch (Exception e) { Log.Info("清除 Sunshine 记着的显示设置失败：" + e.Message); }
    }

    static bool _repairTried;

    /// <summary>SunshineService 登记的程序路径（sc qc 的 BINARY_PATH_NAME），服务不存在时返回 null</summary>
    static async Task<string?> ServiceBinaryAsync()
    {
        try
        {
            var (code, o) = await Proc.RunAsync("sc", "qc SunshineService", 10_000);
            if (code != 0) return null;
            var line = o.Split('\n').FirstOrDefault(l => l.Contains("BINARY_PATH_NAME"));
            if (line == null) return null;
            var v = line[(line.IndexOf(':') + 1)..].Trim();
            if (v.StartsWith('"')) { var end = v.IndexOf('"', 1); return end > 0 ? v[1..end] : v.Trim('"'); }
            var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            return exe > 0 ? v[..(exe + 4)] : v;
        }
        catch { return null; }
    }

    /// <summary>Sunshine 起不来的原因：服务状态 + Sunshine 自己日志里最后几条 Fatal / Error</summary>
    public static async Task<string> DiagnoseAsync()
    {
        var (_, q) = await Proc.RunAsync("sc", "query SunshineService", 10_000);
        var bin = await ServiceBinaryAsync();
        if (bin != null && !File.Exists(bin))
            return $"服务启动失败：服务登记的程序 {bin} 不存在（多半被杀毒软件或公司安全软件删除了）。Farloop 会自动重装一次 Sunshine 补回文件；如果还是被删，请把 {Path.GetDirectoryName(Exe)} 加入白名单";
        var svc = q.Contains("1060") ? "服务不存在" : q.Contains("RUNNING") ? "服务在运行，但 sunshine.exe 启动后就退出了" : q.Contains("STOPPED") ? "服务启动失败" : "服务状态未知";
        var lines = new System.Collections.Generic.List<string>();
        bool everStarted = false;
        try
        {
            if (File.Exists(LogPath))
            {
                using var f = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (f.Length > 64 * 1024) f.Seek(-64 * 1024, SeekOrigin.End);
                using var r = new StreamReader(f, Encoding.UTF8);
                var text = r.ReadToEnd();
                // 真正启动过的 Sunshine 会写一大段（编码器探测、网页界面地址…）；只有“New credentials”一行 = 从没被拉起来过
                everStarted = text.Contains("Configuration UI available") || text.Contains("Testing for available encoders") || text.Contains("Starting system tray");
                var all = text.Split('\n');
                lines = all.Where(l => l.Contains("Fatal:") || l.Contains("Error:"))
                           .Where(l => !l.Contains("YUV444") && !l.Contains("NvEncUnregisterAsyncEvent")) // 探测编码器时的正常报错
                           .Select(l => l.Trim()).TakeLast(4).ToList();
            }
        }
        catch (Exception e) { lines.Add("读不了 Sunshine 日志：" + e.Message); }
        var fatal = lines.LastOrDefault(l => l.Contains("Fatal:"));
        var hint = fatal == null ? "" :
            fatal.Contains("bind", StringComparison.OrdinalIgnoreCase) || fatal.Contains("address", StringComparison.OrdinalIgnoreCase) ? "（端口被别的程序占用，可能装了另一个串流软件）" :
            fatal.Contains("encoder", StringComparison.OrdinalIgnoreCase) || fatal.Contains("display", StringComparison.OrdinalIgnoreCase) ? "（找不到可用的显示器或显卡编码器：笔记本合盖 / 没接显示器 / 显卡驱动太旧）" : "";
        var extra = new System.Collections.Generic.List<string>();
        if (!everStarted && File.Exists(LogPath))
            extra.Add("sunshine.exe 从没真正启动过（它的日志里没有任何启动记录），多半是被安全软件拦截，或者服务拉不起它");
        extra.AddRange(WindowsEvidence());
        if (await IsRemoteDesktopLoginAsync()) extra.Add("当前是通过远程桌面（RDP）登录的：Sunshine 只能在本机屏幕（控制台）会话里工作，请在电脑前登录，或断开远程桌面时选“断开”而不是注销");
        return svc + hint + (lines.Count > 0 ? "。Sunshine 日志：" + string.Join(" | ", lines) : "。Sunshine 日志里没有错误记录")
               + (extra.Count > 0 ? "。" + string.Join("。", extra) : "");
    }

    /// <summary>Windows 事件日志里和 Sunshine 有关的证据：程序崩溃、服务管理器报错、Windows 安全中心拦截（最近 2 天）</summary>
    static System.Collections.Generic.List<string> WindowsEvidence()
    {
        var found = new System.Collections.Generic.List<string>();
        void Query(string log, string xpath, Func<System.Diagnostics.Eventing.Reader.EventRecord, string, string?> pick)
        {
            try
            {
                var q = new System.Diagnostics.Eventing.Reader.EventLogQuery(log, System.Diagnostics.Eventing.Reader.PathType.LogName, xpath) { ReverseDirection = true };
                using var r = new System.Diagnostics.Eventing.Reader.EventLogReader(q);
                int n = 0;
                for (var e = r.ReadEvent(); e != null && n < 200; e = r.ReadEvent(), n++)
                {
                    using (e)
                    {
                        string msg;
                        try { msg = e.FormatDescription() ?? ""; } catch { msg = ""; }
                        if (msg == "") msg = string.Join(" ", e.Properties.Select(p => p.Value?.ToString() ?? ""));
                        var t = pick(e, msg);
                        if (t != null) { found.Add($"{e.TimeCreated:MM-dd HH:mm} {t}"); if (found.Count >= 4) return; }
                    }
                }
            }
            catch (Exception ex) { Log.Info($"读取事件日志 {log} 失败：{ex.Message}"); }
        }
        const string recent = "TimeCreated[timediff(@SystemTime) <= 172800000]";
        // 程序崩溃 / 无响应（事件 1000、1002）
        Query("Application", $"*[System[(EventID=1000 or EventID=1002) and {recent}]]",
            (e, m) => m.Contains("sunshine", StringComparison.OrdinalIgnoreCase) ? "Sunshine 崩溃：" + OneLine(m) : null);
        // 服务管理器：服务启动失败 / 意外终止
        Query("System", $"*[System[Provider[@Name='Service Control Manager'] and (EventID=7000 or EventID=7009 or EventID=7023 or EventID=7024 or EventID=7031 or EventID=7034) and {recent}]]",
            (e, m) => m.Contains("Sunshine", StringComparison.OrdinalIgnoreCase) ? "服务报错：" + OneLine(m) : null);
        // Windows 安全中心（Defender）检测 / 拦截
        Query("Microsoft-Windows-Windows Defender/Operational", $"*[System[(EventID=1116 or EventID=1117 or EventID=1121 or EventID=1123) and {recent}]]",
            (e, m) => m.Contains("sunshine", StringComparison.OrdinalIgnoreCase) ? "Windows 安全中心拦截了 Sunshine：" + OneLine(m) : null);
        // 应用程序控制（AppLocker / WDAC，公司电脑常见）
        Query("Microsoft-Windows-CodeIntegrity/Operational", $"*[System[(EventID=3077 or EventID=3033 or EventID=3034) and {recent}]]",
            (e, m) => m.Contains("sunshine", StringComparison.OrdinalIgnoreCase) ? "系统策略禁止运行 Sunshine：" + OneLine(m) : null);
        Query("Microsoft-Windows-AppLocker/EXE and DLL", $"*[System[(EventID=8004 or EventID=8007) and {recent}]]",
            (e, m) => m.Contains("sunshine", StringComparison.OrdinalIgnoreCase) ? "AppLocker 禁止运行 Sunshine：" + OneLine(m) : null);
        return found;
    }

    static string OneLine(string s)
    {
        s = string.Join(" ", s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        return s.Length > 220 ? s[..220] + "…" : s;
    }

    /// <summary>当前登录用户是不是经远程桌面登录的（本机屏幕上没人）</summary>
    static async Task<bool> IsRemoteDesktopLoginAsync()
    {
        try
        {
            var (_, o) = await Proc.RunAsync("query", "user", 10_000);
            var active = o.Split('\n').Where(l => l.Contains('>') || l.Contains("Active") || l.Contains("运行中")).ToList();
            return active.Count > 0 && active.All(l => l.Contains("rdp-tcp", StringComparison.OrdinalIgnoreCase))
                   && !o.Split('\n').Any(l => l.Contains("console", StringComparison.OrdinalIgnoreCase) && (l.Contains("Active") || l.Contains("运行中")));
        }
        catch { return false; }
    }

    /// <summary>
    /// 把 Moonlight 端生成的 PIN 交给 Sunshine，完成配对。
    /// 新版 Sunshine（2026.914 起）：先 GET /api/pin 拿到待配对列表，按来源 IP 找到对应的 pairing_id，再 POST {pairing_id, pin, name}。
    /// 旧版没有 GET /api/pin（返回 404），退回只 POST {pin, name}。
    /// </summary>
    public static async Task<(bool Ok, string Message)> SubmitPinAsync(AppConfig cfg, string pin, string clientName, string clientIp, CancellationToken ct)
    {
        if (!Installed) return (false, "被控端没有安装 Sunshine");
        if (cfg.SunshineUser == "" || cfg.SunshinePassword == "") return (false, "被控端没有设置串流账号（设置 → 远程控制 → 重新设置串流账号）");

        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        // 新版 POST 会一直挂起到 Moonlight 握手完成，所以超时要留足
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{cfg.SunshineUser}:{cfg.SunshinePassword}")));

        string last = "";
        for (int i = 0; i < 15 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                using var list = await http.GetAsync(Api, ct);
                if (list.StatusCode == System.Net.HttpStatusCode.Unauthorized) return (false, "Sunshine 账号密码不对");

                object body;
                if (list.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    body = new { pin, name = clientName }; // 旧版 Sunshine
                }
                else
                {
                    var id = PickPairing(await list.Content.ReadAsStringAsync(ct), clientIp);
                    if (id == null) { last = "还没收到 Moonlight 的配对请求"; await Task.Delay(1500, ct); continue; }
                    body = new { pairing_id = id, pin, name = clientName };
                }

                using var content = new ByteArrayContent(Frames.Json(body));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                using var resp = await http.PostAsync(Api, content, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode && IsStatusTrue(text)) return (true, "配对成功");
                last = $"{(int)resp.StatusCode} {text}";
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { last = e.Message; }
            await Task.Delay(1500, ct);
        }
        return (false, "Sunshine 没有接受 PIN：" + last);
    }

    /// <summary>从 {"pairings":[{"address","id","name"}]} 里选出来自 clientIp 的那条；只有一条时直接用它</summary>
    public static string? PickPairing(string json, string clientIp)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pairings", out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var items = arr.EnumerateArray()
            .Select(e => (
                Id: e.TryGetProperty("id", out var i) ? i.GetString() : null,
                Addr: e.TryGetProperty("address", out var a) ? a.GetString() ?? "" : ""))
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .ToList();
        // 地址可能带 IPv4 映射前缀 ::ffff:
        var match = items.LastOrDefault(x => x.Addr == clientIp || x.Addr.EndsWith(":" + clientIp));
        if (match.Id != null) return match.Id;
        return items.Count == 1 ? items[0].Id : null;
    }

    static bool IsStatusTrue(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("status", out var s)) return false;
            return s.ValueKind == System.Text.Json.JsonValueKind.True ||
                   (s.ValueKind == System.Text.Json.JsonValueKind.String && s.GetString() == "true");
        }
        catch { return false; }
    }
}

/// <summary>控制端：Moonlight（解码 + 显示 + 键鼠手柄）</summary>
public static class Moonlight
{
    public static string? Exe =>
        new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Moonlight Game Streaming", "Moonlight.exe"),
        }.FirstOrDefault(File.Exists);

    public static bool Installed => Exe != null;

    static Process Start(params string[] args)
    {
        var psi = new ProcessStartInfo(Exe ?? throw new FileNotFoundException("没有安装 Moonlight")) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Moonlight 启动失败");
        p.EnableRaisingEvents = true;
        return p;
    }

    public static Process StartPair(string ip, string pin) => Start("pair", ip, "--pin", pin);
    public static Process StartStream(string ip, StreamSettings s)
    {
        DisableConnectionWarnings();
        return Start(BuildStreamArgs(ip, s).ToArray());
    }

    /// <summary>
    /// 关掉 Moonlight 网络卡顿时左下角的红字提示（“Slow connection to PC”等）——我们有自己的信息卡片。
    /// 命令行没有这个选项；它是 Moonlight 设置里的 connwarnings（源码 streamingpreferences.cpp），
    /// Qt 在 Windows 上存在注册表 HKCU\Software\Moonlight Game Streaming Project\Moonlight，布尔值存成字符串。
    /// </summary>
    public static void DisableConnectionWarnings()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Moonlight Game Streaming Project\Moonlight");
            k.SetValue("connwarnings", "false", Microsoft.Win32.RegistryValueKind.String);
        }
        catch (Exception e) { Log.Error("关闭 Moonlight 网络提示失败", e); }
    }

    /// <summary>选项名来自 moonlight-qt 源码 app/cli/commandlineparser.cpp</summary>
    public static System.Collections.Generic.List<string> BuildStreamArgs(string ip, StreamSettings s)
    {
        var a = new System.Collections.Generic.List<string> { "stream", ip, "Desktop" };
        void Toggle(string name, bool on) => a.Add(on ? "--" + name : "--no-" + name);

        a.AddRange(new[] { "--display-mode", s.DisplayMode });
        a.AddRange(new[] { "--resolution", s.Resolution });
        // 视频包大小：Moonlight 看到 10.x 内网地址会当成局域网，用 1392 字节的大包。
        // 经 EasyTier 的 UDP 打洞隧道再封装一层后，外层包超过很多宽带/移动网络的路径 MTU，被分片，
        // 而 NAT 经常丢分片 → P2P 下画面不动、但小的鼠标键盘包照常送达（中转走 TCP 没这问题）。1024 是 Moonlight 对公网串流的默认值。
        a.AddRange(new[] { "--packet-size", "1024" });
        a.AddRange(new[] { "--fps", s.Fps.ToString() });
        if (s.BitrateMbps > 0) a.AddRange(new[] { "--bitrate", (s.BitrateMbps * 1000).ToString() });
        a.AddRange(new[] { "--video-codec", s.Codec });
        Toggle("hdr", s.Hdr);
        Toggle("yuv444", s.Yuv444);
        Toggle("vsync", s.VSync);
        Toggle("frame-pacing", s.FramePacing);
        Toggle("absolute-mouse", s.DesktopMouse);
        Toggle("audio-on-host", s.AudioOnHost);
        Toggle("performance-overlay", s.PerfOverlay);
        Toggle("keep-awake", true);
        return a;
    }
}
