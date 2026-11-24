using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>首次安装和系统集成：下载组件、计划任务、防火墙、自启动。</summary>
public static class Installer
{
    public const string EasyTierTask = @"Farloop\EasyTier";
    public const string AutostartTask = @"Farloop\App";
    public const string DragHelperTask = @"Farloop\DragHelper";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Farloop");
    public static string InstalledExe => Path.Combine(InstallDir, "Farloop.exe");

    // ---------------- 程序本体 ----------------
    public static void InstallSelf()
    {
        var cur = Environment.ProcessPath!;
        if (!string.Equals(Path.GetFullPath(cur), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(InstallDir);
            foreach (var p in Process.GetProcessesByName("Farloop").Concat(Process.GetProcessesByName("FreeRemote")))
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    if (string.Equals(Path.GetFullPath(p.MainModule?.FileName ?? ""), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
            }
            File.Copy(cur, InstalledExe, true);
        }
        // 0.13 起改名 Farloop：删掉旧名字的快捷方式（程序目录、计划任务等内部名字保持不变，升级无缝）
        foreach (var old in OldShortcuts) { try { File.Delete(old); } catch { } }
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Farloop.lnk"));
        CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Farloop.lnk"));
        RegisterUninstall();
        RefreshShellIcons();
    }

    // ---------------- 系统“已安装的应用”登记 / 卸载 ----------------
    const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Farloop";

    static IEnumerable<string> OldShortcuts => new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "FreeRemote.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "FreeRemote.lnk"),
    };

    static IEnumerable<string> Shortcuts => OldShortcuts.Concat(new[]
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Farloop.lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Farloop.lnk"),
    });

    /// <summary>登记到“设置 → 应用 → 已安装的应用”和“控制面板 → 程序和功能”</summary>
    public static void RegisterUninstall()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(UninstallKey);
            k.SetValue("DisplayName", "Farloop");
            k.SetValue("DisplayVersion", PeerSession.Version);
            k.SetValue("Publisher", "Farloop");
            k.SetValue("DisplayIcon", File.Exists(Path.Combine(InstallDir, "Farloop.ico")) ? Path.Combine(InstallDir, "Farloop.ico") : InstalledExe);
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
            k.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
            if (File.Exists(InstalledExe))
                k.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), Microsoft.Win32.RegistryValueKind.DWord);
            k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        catch (Exception e) { Log.Error("登记卸载信息失败", e); }
    }

    /// <summary>
    /// 完整卸载：停组网、删开机任务、删防火墙规则、恢复光标、删快捷方式和登记，最后删程序目录。
    /// Sunshine / Moonlight 是独立安装的软件，保留（可在“已安装的应用”里单独卸载）。
    /// </summary>
    public static async Task UninstallAsync(bool deleteData)
    {
        foreach (var p in Process.GetProcessesByName("FreeRemote").Concat(Process.GetProcessesByName("Farloop")))
            if (p.Id != Environment.ProcessId) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        CursorClient.RestoreSystemCursors();
        await UninstallTasksAsync();
        foreach (var t in Migration.OldTasks.Append(DragHelperTask))
            await Proc.RunAsync("schtasks", $"/Delete /TN \"{t}\" /F", 15_000);
        await Proc.RunAsync("powershell", "-NoProfile -Command \"try { $s = New-Object -ComObject Schedule.Service; $s.Connect(); foreach ($n in 'Farloop','FreeRemote') { try { $s.GetFolder('\\').DeleteFolder($n, 0) } catch { } } } catch { }\"", 20_000);
        foreach (var n in new[] { "Farloop-VLAN", "Farloop-EasyTier", "Farloop-Assist" }.Concat(Migration.OldFirewallRules))
            await Proc.RunAsync("netsh", $"advfirewall firewall delete rule name=\"{n}\"", 15_000);
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(Migration.OldUninstallKey, false); } catch { }
        try { if (Directory.Exists(Migration.OldInstallDir)) Directory.Delete(Migration.OldInstallDir, true); } catch { }
        if (deleteData) { try { if (Directory.Exists(Migration.OldDataDir)) Directory.Delete(Migration.OldDataDir, true); } catch { } }
        foreach (var s in Shortcuts) { try { File.Delete(s); } catch { } }
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, false); } catch { }
        if (deleteData) { try { Directory.Delete(AppConfig.DataDir, true); } catch (Exception e) { Log.Error("删除数据目录失败", e); } }

        // 正在运行的 exe 删不掉：交给一个延时的 cmd 在我们退出后删除
        var cmd = $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{InstallDir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }

    /// <summary>把 exe 里的 app.ico 写到安装目录（Farloop.ico），给快捷方式用</summary>
    static string? WriteIcon()
    {
        try
        {
            var path = Path.Combine(InstallDir, "Farloop.ico");
            var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (s == null) return null;
            Directory.CreateDirectory(InstallDir);
            using (s) using (var f = File.Create(path)) s.CopyTo(f);
            return path;
        }
        catch (Exception e) { Log.Error("写出图标失败", e); return null; }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    /// <summary>通知资源管理器刷新图标</summary>
    public static void RefreshShellIcons() { try { SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); } catch { } }

    static void CreateShortcut(string lnkPath)
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell")!;
            dynamic sh = Activator.CreateInstance(t)!;
            dynamic lnk = sh.CreateShortcut(lnkPath);
            lnk.TargetPath = InstalledExe;
            lnk.WorkingDirectory = InstallDir;
            // 图标单独放一个新文件：exe 路径没变，系统图标缓存会一直显示旧图标
            var ico = WriteIcon();
            if (ico != null) lnk.IconLocation = ico + ",0";
            lnk.Description = "Farloop";
            lnk.Save();
        }
        catch (Exception e) { Log.Error("创建快捷方式失败", e); }
    }

    // ---------------- EasyTier ----------------
    public static async Task DownloadEasyTierAsync(IProgress<string> progress)
    {
        if (EasyTier.Installed) { progress.Report("已存在，跳过"); return; }
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Farloop/1.0");

        progress.Report("查询最新版本…");
        var json = await http.GetStringAsync("https://api.github.com/repos/EasyTier/EasyTier/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        var asset = doc.RootElement.GetProperty("assets").EnumerateArray()
            .FirstOrDefault(a =>
            {
                var n = a.GetProperty("name").GetString() ?? "";
                return n.Contains("windows-x86_64", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
            });
        if (asset.ValueKind == JsonValueKind.Undefined) throw new InvalidOperationException("没找到 Windows 版 EasyTier");

        var url = asset.GetProperty("browser_download_url").GetString()!;
        progress.Report($"下载 {tag}…");
        var tmp = Path.Combine(Path.GetTempPath(), "fr-et-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var zip = Path.Combine(tmp, "et.zip");
            await using (var fs = File.Create(zip))
            await using (var s = await http.GetStreamAsync(url))
                await s.CopyToAsync(fs);

            progress.Report("解压…");
            var outDir = Path.Combine(tmp, "x");
            ZipFile.ExtractToDirectory(zip, outDir);
            var core = Directory.EnumerateFiles(outDir, "easytier-core.exe", SearchOption.AllDirectories).FirstOrDefault()
                       ?? throw new InvalidOperationException("压缩包里没有 easytier-core.exe");
            Directory.CreateDirectory(EasyTier.Dir);
            foreach (var f in Directory.EnumerateFiles(Path.GetDirectoryName(core)!))
                File.Copy(f, Path.Combine(EasyTier.Dir, Path.GetFileName(f)), true);
            // 官方包只用它带的驱动等文件；程序本体换成 FreeRemote 内置的打洞加速版
            WriteBundledEasyTier();
            progress.Report($"已安装 {tag}（打洞加速版）");
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    // ---------------- 内置的 EasyTier（打洞加速版） ----------------
    // 官方 v2.6.4 源码只改了打洞失败后的重试间隔：原版最终每 64 秒才再试一次（UDP 打洞）、60 秒（直连），
    // 改成最多 8 秒 / 10 秒，NAT4（手机流量、公司网络）对 NAT3 更快、更稳地打通 P2P。手机端用的是同一份源码。
    static readonly (string Resource, string File)[] Bundled = { ("et-core", "easytier-core.exe"), ("et-cli", "easytier-cli.exe") };

    static byte[]? ReadBundled(string res)
    {
        using var s = typeof(Installer).Assembly.GetManifestResourceStream(res);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>已安装的 EasyTier 是不是内置版（逐字节比较）</summary>
    public static bool BundledEasyTierInstalled()
    {
        foreach (var (res, file) in Bundled)
        {
            var b = ReadBundled(res);
            if (b == null) return true; // 没内置（开发版构建）就不替换
            var p = Path.Combine(EasyTier.Dir, file);
            if (!File.Exists(p) || !System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)).AsSpan()
                    .SequenceEqual(System.Security.Cryptography.SHA256.HashData(b))) return false;
        }
        return true;
    }

    static void WriteBundledEasyTier()
    {
        foreach (var (res, file) in Bundled)
        {
            var b = ReadBundled(res);
            if (b != null) File.WriteAllBytes(Path.Combine(EasyTier.Dir, file), b);
        }
    }

    /// <summary>启动时：装的不是内置加速版就换上（停组网 → 替换 → 重新注册并启动，几秒钟）</summary>
    public static async Task<bool> EnsureBundledEasyTierAsync(AppConfig cfg)
    {
        if (!cfg.IsConfigured || !EasyTier.Installed || BundledEasyTierInstalled()) return false;
        try
        {
            Log.Info("换上内置的 EasyTier（打洞加速版）");
            await StopEasyTierAsync();
            for (int i = 0; i < 20 && EasyTier.IsRunning; i++) await Task.Delay(250);
            WriteBundledEasyTier();
            await RegisterEasyTierAsync(cfg);
            return true;
        }
        catch (Exception e)
        {
            Log.Error("替换 EasyTier 失败", e);
            try { await RestartEasyTierAsync(); } catch { }
            return false;
        }
    }

    /// <summary>注册为开机自启的 SYSTEM 计划任务（登录前就联网，无人值守必需），并立即启动。</summary>
    static string CmdPath => Path.Combine(AppConfig.DataDir, "run-easytier.cmd");

    // 用 cmd 循环包一层：进程意外退出 3 秒后自动拉起
    static string BuildCmd(AppConfig cfg) =>
        "@echo off\r\n" +
        $"cd /d \"{EasyTier.Dir}\"\r\n" +
        // 打洞用的公网 IP 规则（打过补丁的 EasyTier 读取这个环境变量）
        (cfg.EffectiveIpRule is { Length: > 0 } rule ? $"set \"FR_PUBLIC_IP_PREFIX={rule}\"\r\n" : "") +
        ":loop\r\n" +
        $"\"{EasyTier.CoreExe}\" {EasyTier.BuildArgs(cfg)}\r\n" +
        "timeout /t 3 /nobreak >nul\r\n" +
        "goto loop\r\n";

    /// <summary>启动时检查：开机脚本和当前配置不一致（比如升级后换了节点）就重新生成并重启组网</summary>
    public static async Task<bool> EnsureEasyTierCurrentAsync(AppConfig cfg)
    {
        if (!cfg.IsConfigured || !EasyTier.Installed) return false;
        try
        {
            if (File.Exists(CmdPath) && File.ReadAllText(CmdPath) == BuildCmd(cfg)) return false;
            Log.Info("组网配置已变化，重新注册");
            await RegisterEasyTierAsync(cfg);
            return true;
        }
        catch (Exception e) { Log.Error("更新组网配置失败", e); return false; }
    }

    public static async Task RegisterEasyTierAsync(AppConfig cfg)
    {
        await StopEasyTierAsync();
        var cmd = CmdPath;
        File.WriteAllText(cmd, BuildCmd(cfg), new UTF8Encoding(false));

        var xml = TaskXml(
            trigger: "<BootTrigger><Enabled>true</Enabled></BootTrigger>",
            principal: "<UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel>",
            command: "cmd.exe",
            args: $"/c \"{cmd}\"");
        await CreateTaskAsync(EasyTierTask, xml);
        await Proc.RunAsync("schtasks", $"/Run /TN \"{EasyTierTask}\"", 15_000);
    }

    public static async Task StopEasyTierAsync()
    {
        await Proc.RunAsync("schtasks", $"/End /TN \"{EasyTierTask}\"", 15_000);
        var assist = Assist.Pids; // 远程协助的进程不动
        foreach (var p in Process.GetProcessesByName("easytier-core").Where(x => !assist.Contains(x.Id)))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    /// <summary>组网没在运行就启动（托盘“退出”会停掉组网，重新打开 Farloop 时要拉起来）</summary>
    public static async Task EnsureEasyTierRunningAsync()
    {
        if (EasyTier.IsRunning || !File.Exists(CmdPath)) return;
        Log.Info("组网未运行，启动");
        await Proc.RunAsync("schtasks", $"/Run /TN \"{EasyTierTask}\"", 15_000);
    }

    public static async Task RestartEasyTierAsync()
    {
        await StopEasyTierAsync();
        await Proc.RunAsync("schtasks", $"/Run /TN \"{EasyTierTask}\"", 15_000);
    }

    /// <summary>当前用户登录时以最高权限自动启动（托盘运行，不弹 UAC）。</summary>
    public static async Task RegisterAutostartAsync()
    {
        var user = WindowsIdentity.GetCurrent().Name;
        var xml = TaskXml(
            trigger: $"<LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger>",
            principal: $"<UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel>",
            command: InstalledExe,
            args: "--tray");
        await CreateTaskAsync(AutostartTask, xml);
    }

    /// <summary>开机自启开关：开 = 重新注册（顺便修正 exe 路径），关 = 删除任务。组网任务不受影响。</summary>
    public static async Task SetAutostartAsync(bool on)
    {
        if (on) await RegisterAutostartAsync();
        else await Proc.RunAsync("schtasks", $"/Delete /TN \"{AutostartTask}\" /F", 15_000);
    }

    public static async Task UninstallTasksAsync()
    {
        await StopEasyTierAsync();
        await Proc.RunAsync("schtasks", $"/Delete /TN \"{EasyTierTask}\" /F", 15_000);
        await Proc.RunAsync("schtasks", $"/Delete /TN \"{AutostartTask}\" /F", 15_000);
    }

    static string TaskXml(string trigger, string principal, string command, string args) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Farloop</Description></RegistrationInfo>
          <Triggers>{trigger}</Triggers>
          <Principals><Principal id="Author">{principal}</Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RestartOnFailure><Interval>PT1M</Interval><Count>999</Count></RestartOnFailure>
            <Priority>4</Priority>
            <Enabled>true</Enabled>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{SecurityElement.Escape(command)}</Command><Arguments>{SecurityElement.Escape(args)}</Arguments></Exec>
          </Actions>
        </Task>
        """;

    static async Task CreateTaskAsync(string name, string xml)
    {
        var f = Path.Combine(Path.GetTempPath(), "fr-task-" + Guid.NewGuid().ToString("N") + ".xml");
        File.WriteAllText(f, xml, Encoding.Unicode);
        try
        {
            var (code, output) = await Proc.RunAsync("schtasks", $"/Create /TN \"{name}\" /XML \"{f}\" /F", 20_000);
            if (code != 0) throw new InvalidOperationException($"创建计划任务失败：{output.Trim()}");
        }
        finally { File.Delete(f); }
    }

    // ---------------- 防火墙 / 电源 / winget ----------------
    public static async Task ConfigureFirewallAsync()
    {
        foreach (var n in new[] { "Farloop-VLAN", "Farloop-EasyTier" })
            await Proc.RunAsync("netsh", $"advfirewall firewall delete rule name=\"{n}\"", 15_000);
        // 只放行来自虚拟局域网的入站；外网不受影响
        await Proc.RunAsync("netsh", $"advfirewall firewall add rule name=\"Farloop-VLAN\" dir=in action=allow remoteip={EasyTier.Subnet} profile=any", 15_000);
        // EasyTier 本身要能接收打洞包
        await Proc.RunAsync("netsh", $"advfirewall firewall add rule name=\"Farloop-EasyTier\" dir=in action=allow program=\"{EasyTier.CoreExe}\" profile=any", 15_000);
    }

    /// <summary>远程协助：被协助端只放行协助者（10.145.0.2）的入站</summary>
    public static async Task ConfigureAssistFirewallAsync()
    {
        await Proc.RunAsync("netsh", "advfirewall firewall delete rule name=\"Farloop-Assist\"", 15_000);
        await Proc.RunAsync("netsh", $"advfirewall firewall add rule name=\"Farloop-Assist\" dir=in action=allow remoteip={Assist.HelperIp} profile=any", 15_000);
    }

    public static async Task DisableSleepOnAcAsync()
    {
        await Proc.RunAsync("powercfg", "/change standby-timeout-ac 0", 10_000);
        await Proc.RunAsync("powercfg", "/change hibernate-timeout-ac 0", 10_000);
    }

    public static async Task<(bool Ok, string Message)> WingetInstallAsync(string id, string? version = null, bool force = false)
    {
        var (code, output) = await Proc.RunAsync("winget",
            $"install --id {id} -e{(version != null ? " --version " + version : "")}{(force ? " --force" : "")} --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 20 * 60_000);
        Log.Info($"winget {id} 退出码 {code}");
        // 已安装时 winget 也返回非 0，交给调用方用文件是否存在来判断
        return (code == 0, output.Length > 300 ? output[^300..] : output);
    }
}
