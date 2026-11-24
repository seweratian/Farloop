using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace FreeRemote.Core;

// ===================== 设备详情 / 终端 / 快速启动（被控端提供，控制端和手机端使用） =====================

public record SysInfoMsg(
    string Name, string Os, string Cpu, int Cores, long RamTotal, long RamAvail, double CpuLoad,
    string[] Gpus, string Board, string[] Disks, string[] Displays, long UptimeSec,
    string LanIp, string VirtualIp, string Versions, string? PreviewJpg,
    // 0.13.5+：被控端网络（多条宽带检测）
    string[]? PublicIps = null, int[]? PublicIpCounts = null, bool PerFlow = false,
    string? IpRule = null, string? SuggestedRule = null, string? NetAdvice = null);
public record SetIpRuleMsg(string Rule);
public record TermOpenedMsg(string Id, string Cwd, bool Admin);
public record TermInputMsg(string Id, string Line);
public record TermOutputMsg(string Id, string Text, string? Prompt);
public record TermCloseMsg(string Id);
public record LaunchAppMsg(string Path);
public record AppEntry(string Name, string Path, string? IconPng);
public record AppListMsg(AppEntry[] Apps);

public static class RemoteTools
{
    // ---------------------------------------------------------------- 设备详情
    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX { public uint dwLength, dwMemoryLoad; public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    public static SysInfoMsg SysInfo(AppConfig cfg, double cpuLoad, string lanIp, bool withPreview)
    {
        string Reg(string key, string val) { try { return Registry.GetValue(key, val, "")?.ToString()?.Trim() ?? ""; } catch { return ""; } }
        const string nt = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        var product = Reg(nt, "ProductName");
        var build = Reg(nt, "CurrentBuildNumber");
        if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11"); // 注册表里 Win11 仍写 10
        var os = $"{product} {Reg(nt, "DisplayVersion")}（版本 {build}.{Reg(nt, "UBR")}）".Trim();
        var cpu = Reg(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString");
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref m);
        const string bios = @"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\BIOS";
        var board = $"{Reg(bios, "BaseBoardManufacturer")} {Reg(bios, "BaseBoardProduct")}".Trim();
        var disks = new List<string>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady && d.DriveType is DriveType.Fixed or DriveType.Removable) disks.Add($"{d.Name.TrimEnd('\\')} {Fmt(d.TotalSize - d.TotalFreeSpace)} / {Fmt(d.TotalSize)}（剩余 {Fmt(d.TotalFreeSpace)}）"); } catch { }
        }
        var displays = new List<string>();
        try { foreach (var x in HostDisplays.List()) displays.Add($"{x.Name}{(x.Primary ? "（主屏）" : "")} {x.Width}×{x.Height}"); } catch { }
        var versions = $"Farloop {PeerSession.Version}";
        try { var sv = FileVersionInfo.GetVersionInfo(Sunshine.Exe).ProductVersion; if (sv != null) versions += $" · Sunshine {sv}"; } catch { }
        return new SysInfoMsg(cfg.DeviceName, os, cpu, Environment.ProcessorCount, (long)m.ullTotalPhys, (long)m.ullAvailPhys, cpuLoad,
            Gpus(), board, disks.ToArray(), displays.ToArray(), Environment.TickCount64 / 1000, lanIp, cfg.VirtualIp, versions,
            withPreview ? Preview() : null);
    }

    static string[] Gpus()
    {
        var list = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (cls == null) return list.ToArray();
            foreach (var n in cls.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var k = cls.OpenSubKey(n);
                var desc = k?.GetValue("DriverDesc")?.ToString();
                if (string.IsNullOrEmpty(desc) || desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("Basic", StringComparison.OrdinalIgnoreCase)) continue;
                var mem = k!.GetValue("HardwareInformation.qwMemorySize");
                long bytes = mem is long l ? l : mem is byte[] bb && bb.Length >= 8 ? BitConverter.ToInt64(bb, 0) : 0;
                var s = bytes > 0 ? $"{desc}（{Fmt(bytes)}）" : desc;
                if (!list.Contains(s)) list.Add(s);
            }
        }
        catch { }
        return list.ToArray();
    }

    /// <summary>当前桌面的小缩略图（手机端设备页顶部的预览图）</summary>
    static string? Preview()
    {
        try
        {
            var b = HostDisplays.StreamedBounds();
            using var full = new Bitmap(b.Width, b.Height);
            using (var g = Graphics.FromImage(full)) g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size);
            int w = 640, h = Math.Max(1, b.Height * w / Math.Max(1, b.Width));
            using var small = new Bitmap(w, h);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(full, 0, 0, w, h);
            }
            using var ms = new MemoryStream();
            var enc = ImageCodecInfo.GetImageEncoders().First(e => e.MimeType == "image/jpeg");
            var ps = new EncoderParameters(1) { Param = { [0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L) } };
            small.Save(ms, enc, ps);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    public static string Fmt(long b) =>
        b >= 1L << 40 ? $"{b / (double)(1L << 40):0.0} TB" : b >= 1L << 30 ? $"{b / (double)(1L << 30):0.0} GB" : b >= 1L << 20 ? $"{b / (double)(1L << 20):0} MB" : $"{b / 1024} KB";

    // ---------------------------------------------------------------- 终端（PowerShell）
    // 用管道驱动 powershell：每条命令后追加一个结束标记，借此知道命令执行完了、当前目录在哪。
    // 不是完整的伪终端（不支持需要交互界面的程序，如 vim），但普通命令、脚本、ipconfig 等都可以。
    sealed class Term
    {
        public required Process P;
        public required PeerSession S;
        public readonly StringBuilder Buf = new();
        public readonly object Gate = new();
    }

    static readonly ConcurrentDictionary<string, Term> Terms = new();
    const string Marker = "\u0001FRDONE\u0001";

    public static TermOpenedMsg OpenTerminal(PeerSession s)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var psi = new ProcessStartInfo("powershell.exe", "-NoLogo -NoProfile -NoExit -ExecutionPolicy Bypass -Command -")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        var p = Process.Start(psi)!;
        var t = new Term { P = p, S = s };
        Terms[id] = t;
        p.StandardInput.AutoFlush = true;
        p.StandardInput.WriteLine("[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'");
        _ = Task.Run(() => Pump(id, t, p.StandardOutput));
        _ = Task.Run(() => Pump(id, t, p.StandardError));
        _ = Task.Run(async () => { await p.WaitForExitAsync(); Send(id, t, "\n[终端已结束]\n", null); Terms.TryRemove(id, out _); });
        // 打开时先报一次当前目录
        Input(id, "");
        Log.Info($"终端：{s.RemoteName} 打开 PowerShell（{id}）");
        return new TermOpenedMsg(id, psi.WorkingDirectory, true);
    }

    public static void Input(string id, string line)
    {
        if (!Terms.TryGetValue(id, out var t)) return;
        try
        {
            // 命令后输出结束标记 + 当前目录，控制端据此显示提示符
            t.P.StandardInput.WriteLine(line);
            t.P.StandardInput.WriteLine($"Write-Output (\"{Marker}\" + (Get-Location).Path)");
        }
        catch { }
    }

    /// <summary>Ctrl+C：结束当前正在跑的命令（关掉整个 PowerShell 再开一个新的，目录保留）</summary>
    public static TermOpenedMsg? Interrupt(PeerSession s, string id)
    {
        if (!Terms.TryRemove(id, out var t)) return null;
        try { t.P.Kill(true); } catch { }
        return OpenTerminal(s);
    }

    public static void Close(string id)
    {
        if (Terms.TryRemove(id, out var t)) { try { t.P.Kill(true); } catch { } }
    }

    public static void CloseAllFor(PeerSession s)
    {
        foreach (var kv in Terms.Where(kv => kv.Value.S == s).ToList()) Close(kv.Key);
    }

    static async Task Pump(string id, Term t, StreamReader r)
    {
        var buf = new char[4096];
        int n;
        try
        {
            while ((n = await r.ReadAsync(buf, 0, buf.Length)) > 0)
            {
                var text = new string(buf, 0, n);
                lock (t.Gate)
                {
                    t.Buf.Append(text);
                    var all = t.Buf.ToString();
                    int mi;
                    // 输出里出现结束标记 → 标记前的内容 + 新提示符
                    while ((mi = all.IndexOf(Marker, StringComparison.Ordinal)) >= 0)
                    {
                        int eol = all.IndexOf('\n', mi);
                        if (eol < 0) break;
                        var before = all[..mi];
                        var cwd = all[(mi + Marker.Length)..eol].Trim();
                        Send(id, t, before, cwd);
                        all = all[(eol + 1)..];
                    }
                    // 没有标记的部分（命令运行中的输出）及时转发，最后一行可能不完整，留着
                    int lastNl = all.LastIndexOf('\n');
                    if (lastNl >= 0 && !all.Contains('\u0001'))
                    {
                        Send(id, t, all[..(lastNl + 1)], null);
                        all = all[(lastNl + 1)..];
                    }
                    t.Buf.Clear().Append(all);
                }
            }
        }
        catch { }
    }

    static void Send(string id, Term t, string text, string? prompt)
    {
        if (text.Length == 0 && prompt == null) return;
        _ = t.S.SendJsonAsync(MsgType.TermOutput, new TermOutputMsg(id, text.Replace("\r\n", "\n"), prompt)).ContinueWith(_ => { });
    }

    // ---------------------------------------------------------------- 快速启动
    /// <summary>开始菜单（所有用户 + 当前用户）和桌面上的快捷方式，带 48px 图标</summary>
    public static AppListMsg ListApps()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<AppEntry>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)).ToList(); }
            catch { continue; }
            foreach (var f in files)
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Contains("卸载") || name.Contains("Uninstall", StringComparison.OrdinalIgnoreCase) || !seen.Add(name)) continue;
                list.Add(new AppEntry(name, f, Icon48(f)));
            }
        }
        return new AppListMsg(list.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToArray());
    }

    static string? Icon48(string file)
    {
        try
        {
            using var ico = Icon.ExtractAssociatedIcon(file);
            if (ico == null) return null;
            using var bmp = ico.ToBitmap();
            using var small = new Bitmap(48, 48);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(bmp, 0, 0, 48, 48);
            }
            using var ms = new MemoryStream();
            small.Save(ms, ImageFormat.Png);
            return Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    /// <summary>
    /// 在被控端打开应用。交给资源管理器去启动：FreeRemote 是管理员权限，直接启动的程序也会变成管理员，
    /// 和平时双击打开的不一样（拖放、权限都会出问题）；资源管理器启动的就是普通权限。
    /// 只允许开始菜单 / 桌面上的快捷方式和 .exe。
    /// </summary>
    public static (bool Ok, string Message) Launch(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is not (".lnk" or ".url" or ".exe") || !File.Exists(path)) return (false, "不能打开这个文件");
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
            Log.Info("快速启动：" + path);
            return (true, "已打开 " + Path.GetFileNameWithoutExtension(path));
        }
        catch (Exception e) { return (false, e.Message); }
    }
}
