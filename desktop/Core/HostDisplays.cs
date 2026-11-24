using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>被控端的一块显示器。Id 是写进 Sunshine output_name 的值（新版 Sunshine 用 device_id）。</summary>
/// <param name="StreamIndex">在 Sunshine 显示器列表里的序号：串流中按 Ctrl+Alt+Shift+F(序号+1) 直接切到这块屏（不重启、不重连）。-1 = 不知道</param>
/// <param name="Virtual">Farloop 的虚拟显示器（息屏用），平时不列出来</param>
public sealed record HostDisplay(string Id, string Name, string DeviceName, bool Primary, int Width, int Height, int StreamIndex = -1, bool Virtual = false);

/// <summary>
/// 被控端：多显示器。Sunshine 一次只串流一块屏幕（sunshine.conf 的 output_name，空 = 主屏）。
/// 显示器列表取自 Sunshine 日志里它自己打印的 “Currently available display devices”，
/// 这样 Id 一定是 Sunshine 认的格式；读不到时退回 Windows 的屏幕列表（\\.\DISPLAYn，旧版 Sunshine 也认）。
/// </summary>
public static class HostDisplays
{
    static string LogPath => Path.Combine(Path.GetDirectoryName(Sunshine.ConfPath)!, "sunshine.log");

    public static List<HostDisplay> List()
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (VirtualDisplay.IsActive)
        {
            // 息屏中：桌面上只有虚拟屏（每块对应一块原来的实体屏），按位置从左到右叫“虚拟屏 1、2…”
            var vorder = DxgiOrder();
            var sorted = screens.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToList();
            return sorted.Select((s, i) => new HostDisplay(s.DeviceName,
                    $"虚拟屏 {i + 1}{(sorted.Count > 1 ? (i == 0 ? "（左）" : i == sorted.Count - 1 ? "（右）" : "") : "")}",
                    s.DeviceName, s.Primary, s.Bounds.Width, s.Bounds.Height,
                    vorder.FindIndex(n => n.Equals(s.DeviceName, StringComparison.OrdinalIgnoreCase)), false))
                .OrderByDescending(d => d.Primary).ThenBy(d => d.DeviceName).ToList();
        }
        var list = new List<HostDisplay>();
        try
        {
            if (File.Exists(LogPath))
            {
                string[] lines;
                using (var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs)) lines = r.ReadToEnd().Split('\n');
                int start = Array.FindLastIndex(lines, l => l.Contains("Currently available display devices"));
                if (start >= 0)
                {
                    var json = new System.Text.StringBuilder();
                    for (int i = start + 1; i < lines.Length; i++)
                    {
                        json.AppendLine(lines[i]);
                        if (lines[i].TrimEnd('\r') == "]") break;
                    }
                    using var doc = JsonDocument.Parse(json.ToString());
                    foreach (var d in doc.RootElement.EnumerateArray())
                    {
                        string id = Str(d, "device_id"), dn = Str(d, "display_name"), fn = Str(d, "friendly_name");
                        if (id == "" || dn == "") continue; // 没有 display_name = 当前没在用的显示器
                        string maker = d.TryGetProperty("edid", out var edid) && edid.ValueKind == JsonValueKind.Object ? Str(edid, "manufacturer_id") : "";
                        bool isVirtual = maker.Equals("MTT", StringComparison.OrdinalIgnoreCase) || fn.Contains("VDD", StringComparison.OrdinalIgnoreCase);
                        bool hasInfo = d.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object;
                        bool primary = hasInfo && info.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                        // 注意：Sunshine（系统服务）里的 \\.\DISPLAYn 编号和本进程看到的不一样（实测 DISPLAY1 vs DISPLAY5），
                        // 按屏幕左上角坐标对应到 Windows 的屏幕
                        System.Windows.Forms.Screen? sc = null;
                        if (hasInfo && info.TryGetProperty("origin_point", out var o) && o.ValueKind == JsonValueKind.Object &&
                            o.TryGetProperty("x", out var ox) && o.TryGetProperty("y", out var oy) && ox.TryGetInt32(out var x) && oy.TryGetInt32(out var y))
                            sc = screens.FirstOrDefault(s => s.Bounds.X == x && s.Bounds.Y == y);
                        sc ??= primary ? screens.FirstOrDefault(s => s.Primary) : null;
                        list.Add(new HostDisplay(id, fn == "" ? dn : fn, sc?.DeviceName ?? dn, primary || sc?.Primary == true,
                            sc?.Bounds.Width ?? 0, sc?.Bounds.Height ?? 0, -1, isVirtual));
                    }
                }
            }
        }
        catch (Exception e) { Log.Error("读取 Sunshine 显示器列表失败", e); }

        if (list.Count == 0)
            list = screens.Select(s => new HostDisplay(s.DeviceName, s.DeviceName.Replace(@"\\.\", ""), s.DeviceName, s.Primary, s.Bounds.Width, s.Bounds.Height)).ToList();
        // Sunshine 里的序号：它按 DXGI 的顺序（显卡 → 输出）列出接在桌面上的屏幕；DISPLAYn 的编号在服务进程里可能不同，但顺序相同。
        // 本进程跑 Sunshine 自带的 dxgi-info 得到同样的顺序，再按本进程的屏幕名对上
        var order = DxgiOrder();
        list = list.Select(d => d with { StreamIndex = order.FindIndex(n => n.Equals(d.DeviceName, StringComparison.OrdinalIgnoreCase)) }).ToList();
        // 虚拟显示器（息屏用）不是用户的屏幕：没在息屏时不列出来，切过去也没有意义
        if (!VirtualDisplay.IsActive) list = list.Where(d => !d.Virtual).ToList();
        else if (list.Any(d => d.Virtual)) list = list.Where(d => d.Virtual).ToList(); // 息屏中：实体屏都不在桌面上，只有虚拟屏可看
        return list.OrderByDescending(d => d.Primary).ThenBy(d => d.DeviceName).ToList();
    }

    static (List<string> Names, DateTime At) _dxgi = (new(), DateTime.MinValue);

    /// <summary>屏幕增减后（进入 / 退出息屏）重新取 Sunshine 的显示器顺序</summary>
    public static void InvalidateOrder() { _dxgi = (new(), DateTime.MinValue); _cache = default; }

    /// <summary>接在桌面上的屏幕，按 DXGI 枚举顺序（= Sunshine 的显示器序号）</summary>
    static List<string> DxgiOrder()
    {
        if ((DateTime.UtcNow - _dxgi.At).TotalSeconds < 15) return _dxgi.Names;
        var names = new List<string>();
        try
        {
            var exe = Path.Combine(Path.GetDirectoryName(Sunshine.Exe)!, "tools", "dxgi-info.exe");
            if (File.Exists(exe))
            {
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe)
                    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
                var o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                string? pending = null;
                foreach (var raw in o.Split('\n'))
                {
                    var l = raw.Trim();
                    if (l.StartsWith("Output Name")) pending = l[(l.IndexOf(':') + 1)..].Trim();
                    else if (l.StartsWith("AttachedToDesktop") && pending != null)
                    {
                        if (l.EndsWith("yes", StringComparison.OrdinalIgnoreCase)) names.Add(pending);
                        pending = null;
                    }
                }
            }
        }
        catch (Exception e) { Log.Error("读取显示器顺序失败", e); }
        _dxgi = (names, DateTime.UtcNow);
        return names;
    }

    /// <summary>
    /// 串流中已经用快捷键切到了这块屏（Sunshine 当场换了采集的屏幕）：只把它记进 output_name，下次连接还在这块屏，
    /// 不重启 Sunshine。光标、拖放落点也跟着换屏
    /// </summary>
    public static void Remember(string id)
    {
        if (VirtualDisplay.IsActive) return; // 息屏中切的是虚拟屏，退出息屏就没了，不记
        var d = List().FirstOrDefault(x => x.Id == id);
        if (d == null) return;
        try
        {
            var lines = File.Exists(Sunshine.ConfPath) ? File.ReadAllLines(Sunshine.ConfPath).ToList() : new List<string>();
            lines.RemoveAll(l => l.Split('=')[0].Trim().Equals("output_name", StringComparison.OrdinalIgnoreCase));
            if (!d.Primary) lines.Add($"output_name = {d.Id}");
            File.WriteAllLines(Sunshine.ConfPath, lines);
            _cache = default;
            Log.Info($"串流中切换到屏幕 {d.Name} ({d.DeviceName})，已记住");
        }
        catch (Exception e) { Log.Error("记住串流屏幕失败", e); }
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>sunshine.conf 里当前的 output_name（空 = 主屏）</summary>
    public static string CurrentId()
    {
        try
        {
            if (!File.Exists(Sunshine.ConfPath)) return "";
            foreach (var l in File.ReadAllLines(Sunshine.ConfPath))
            {
                var kv = l.Split('=', 2);
                if (kv.Length == 2 && kv[0].Trim().Equals("output_name", StringComparison.OrdinalIgnoreCase)) return kv[1].Trim();
            }
        }
        catch { }
        return "";
    }

    static (Rectangle Bounds, DateTime At) _cache;

    /// <summary>正在串流的那块屏幕的范围（物理像素）。光标位置、拖放落点、屏幕边缘接收条都按它算。</summary>
    public static Rectangle StreamedBounds()
    {
        if ((DateTime.UtcNow - _cache.At).TotalSeconds < 5 && !_cache.Bounds.IsEmpty) return _cache.Bounds;
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
        var b = primary;
        var id = CurrentId();
        if (id != "")
        {
            var d = List().FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || x.DeviceName.Equals(id, StringComparison.OrdinalIgnoreCase));
            var sc = d == null ? null : System.Windows.Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName.Equals(d.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (sc != null) b = sc.Bounds;
        }
        _cache = (b, DateTime.UtcNow);
        return b;
    }

    /// <summary>切换串流的屏幕：写 output_name 并重启 Sunshine（新设置要重启才生效）</summary>
    public static async Task<(bool Ok, string Message)> SetAsync(string id)
    {
        var d = List().FirstOrDefault(x => x.Id == id);
        if (d == null) return (false, "被控端没有这块显示器");
        try
        {
            var lines = File.Exists(Sunshine.ConfPath) ? File.ReadAllLines(Sunshine.ConfPath).ToList() : new List<string>();
            lines.RemoveAll(l => l.Split('=')[0].Trim().Equals("output_name", StringComparison.OrdinalIgnoreCase));
            // 主屏写空，让 Sunshine 用默认（主屏变化时也跟着走）
            if (!d.Primary) lines.Add($"output_name = {d.Id}");
            File.WriteAllLines(Sunshine.ConfPath, lines);
            _cache = default;
            Log.Info($"切换串流屏幕 → {d.Name} ({d.DeviceName})，重启 Sunshine");
            await Proc.RunAsync("net", "stop SunshineService", 30_000);
            await Proc.RunAsync("net", "start SunshineService", 30_000);
            for (int i = 0; i < 20 && !Sunshine.IsRunning; i++) await Task.Delay(500);
            await Task.Delay(2500); // 等 Sunshine 完成初始化、开始监听
            return (true, d.Name);
        }
        catch (Exception e)
        {
            Log.Error("切换串流屏幕失败", e);
            return (false, e.Message);
        }
    }
}
