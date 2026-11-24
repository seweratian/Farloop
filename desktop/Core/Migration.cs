using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// 0.17 起所有看得见的名字都从 FreeRemote 改成 Farloop：
///   C:\ProgramData\FreeRemote → C:\ProgramData\Farloop（配置、日志、组网组件…）
///   C:\Program Files\FreeRemote\FreeRemote.exe → C:\Program Files\Farloop\Farloop.exe
///   计划任务文件夹 FreeRemote\ → Farloop\，防火墙规则 FreeRemote-* → Farloop-*，“已安装的应用”登记
/// 启动时（读配置之前）把旧的搬过来、删掉；组网等计划任务随后按新名字重新注册。只做一次，失败的部分下次启动再试。
/// </summary>
public static class Migration
{
    static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public static string OldDataDir => Path.Combine(ProgramData, "FreeRemote");
    public static string OldInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FreeRemote");
    public static readonly string[] OldTasks = { @"FreeRemote\EasyTier", @"FreeRemote\App", @"FreeRemote\DragHelper" };
    public static readonly string[] OldFirewallRules = { "FreeRemote-VLAN", "FreeRemote-EasyTier", "FreeRemote-Assist" };
    public const string OldUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\FreeRemote";

    /// <summary>这次启动刚从旧名字迁过来：组网、开机自启的计划任务要按新名字重新注册</summary>
    public static bool Migrated { get; private set; }
    /// <summary>迁移前开着“开机自启”</summary>
    public static bool AutostartWasOn { get; private set; }

    static readonly List<string> _notes = new();

    public static void Run()
    {
        try { Task.Run(RunAsync).Wait(TimeSpan.FromMinutes(2)); }
        catch (Exception e) { _notes.Add("改名迁移出错：" + e.Message); }
        // 迁移完才写日志（写日志会建新目录，迁移前写会让“整个搬过去”变成“逐个复制”）
        foreach (var n in _notes) Log.Info(n);
    }

    static async Task RunAsync()
    {
        bool oldData = Directory.Exists(OldDataDir);
        bool oldInstall = Directory.Exists(OldInstallDir);
        var oldTasks = new List<string>();
        foreach (var t in OldTasks) if ((await Proc.RunAsync("schtasks", $"/Query /TN \"{t}\"", 10_000)).Item1 == 0) oldTasks.Add(t);
        if (!oldData && !oldInstall && oldTasks.Count == 0) return;
        _notes.Add($"改名：把 FreeRemote 迁移为 Farloop（数据目录 {oldData}，程序目录 {oldInstall}，旧计划任务 {oldTasks.Count} 个）");

        // 1. 停掉从旧目录运行的组网、旧名字的程序，删掉旧计划任务
        AutostartWasOn = oldTasks.Contains(@"FreeRemote\App");
        if (oldTasks.Count > 0)
        {
            await Proc.RunAsync("schtasks", "/End /TN \"FreeRemote\\EasyTier\"", 15_000);
            foreach (var p in Process.GetProcessesByName("easytier-core")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
            foreach (var t in oldTasks) await Proc.RunAsync("schtasks", $"/Delete /TN \"{t}\" /F", 15_000);
            await Proc.RunAsync("powershell", "-NoProfile -Command \"try { $s = New-Object -ComObject Schedule.Service; $s.Connect(); $s.GetFolder('\\').DeleteFolder('FreeRemote', 0) } catch { }\"", 20_000);
            Migrated = true;
        }
        foreach (var p in Process.GetProcessesByName("FreeRemote"))
            if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(3000); } catch { } }

        // 2. 数据目录
        if (oldData)
        {
            var neu = AppConfig.DataDir;
            try
            {
                if (!Directory.Exists(neu)) Directory.Move(OldDataDir, neu);
                else CopyMissing(OldDataDir, neu);
            }
            catch (Exception e)
            {
                _notes.Add("整体搬移数据目录失败，改为逐个复制：" + e.Message);
                try { CopyMissing(OldDataDir, neu); } catch (Exception e2) { _notes.Add("复制数据目录失败：" + e2.Message); }
            }
            if (Directory.Exists(OldDataDir))
            {
                try { Directory.Delete(OldDataDir, true); }
                catch (Exception e) { _notes.Add("旧数据目录暂时删不掉，下次启动再删：" + e.Message); }
            }
            Migrated = true;
        }

        // 3. 防火墙规则、“已安装的应用”登记
        foreach (var n in OldFirewallRules)
            await Proc.RunAsync("netsh", $"advfirewall firewall delete rule name=\"{n}\"", 15_000);
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(OldUninstallKey, false); } catch { }

        // 4. 旧程序目录：正在从里面运行时删不掉，下次启动再删
        if (oldInstall)
        {
            var self = Path.GetFullPath(Environment.ProcessPath ?? "");
            if (!self.StartsWith(Path.GetFullPath(OldInstallDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(OldInstallDir, true); }
                catch (Exception e) { _notes.Add("旧程序目录暂时删不掉，下次启动再删：" + e.Message); }
            }
            Migrated = true;
        }
    }

    /// <summary>把 from 里新目录还没有的文件复制过去（新目录里已有的以新目录为准）</summary>
    static void CopyMissing(string from, string to)
    {
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            if (File.Exists(dst)) continue;
            try { File.Copy(f, dst); } catch (Exception e) { _notes.Add($"复制 {f} 失败：{e.Message}"); }
        }
    }
}
