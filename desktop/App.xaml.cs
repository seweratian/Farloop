using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FreeRemote.Core;
using WinForms = System.Windows.Forms;

namespace FreeRemote;

public partial class App : Application
{
    Mutex? _mutex;
    EventWaitHandle? _showSignal;
    WinForms.NotifyIcon? _tray;
    MainWindow? _main;
    public static bool Exiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ex) => { Log.Error("界面异常", ex.Exception); ex.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Error("后台异常", ex.Exception); ex.SetObserved(); };

        // 拖拽助手：普通权限运行，只负责拖拽相关的窗口（见 DragHelper.cs）
        if (e.Args.Contains("--drag-helper"))
        {
            DragHelper.RunHelper(this);
            return;
        }

        // 主程序需要管理员权限：普通权限启动时自己重新以管理员身份启动（和以前一样弹一次 UAC；开机自启走计划任务不弹）
        if (!new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" };
                foreach (var a in e.Args) psi.ArgumentList.Add(a);
                System.Diagnostics.Process.Start(psi);
            }
            catch (Exception ex) { Log.Error("提权失败（用户取消了 UAC？）", ex); }
            Shutdown();
            return;
        }

        // 卸载（来自“设置 → 应用”或设置页里的卸载按钮）
        if (e.Args.Contains("--uninstall"))
        {
            RunUninstall(quiet: e.Args.Contains("--quiet"));
            return;
        }

        // 自动更新：新版本等旧版本退出后再启动（否则单实例检查会把自己当成重复打开）
        SelfUpdate.WaitForOldVersion(e.Args);

        // 单实例：再次打开时唤醒已运行的窗口
        _mutex = new Mutex(true, @"Global\FreeRemote.App", out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\FreeRemote.Show");
        if (!first)
        {
            _showSignal.Set();
            Shutdown();
            return;
        }
        new Thread(() =>
        {
            while (_showSignal.WaitOne()) Dispatcher.BeginInvoke(ShowMain);
        }) { IsBackground = true }.Start();

        // 0.17 起改名：把 C:\ProgramData\FreeRemote、旧计划任务等迁移成 Farloop（要在读配置之前）
        Migration.Run();

        // 保险：上次如果异常退出，系统光标可能还是被替换的样子，先恢复
        CursorClient.RestoreSystemCursors();

        // 外观（跟随系统 / 浅色 / 深色）要在建窗口之前套上
        ThemeManager.Apply(Hub.I.Config.Theme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, ev) =>
        {
            if (ev.Category == Microsoft.Win32.UserPreferenceCategory.General && Hub.I.Config.Theme == ThemeManager.System_)
                Dispatcher.BeginInvoke(() => ThemeManager.Apply(ThemeManager.System_));
        };
        // 上次虚拟屏息屏没还原就退出了（崩溃 / 被结束）：先把实体显示器恢复
        _ = Task.Run(() => VirtualDisplay.RecoverAsync());
        // 驱动提前装好，第一次“关闭对方屏幕”时就不用等
        if (Hub.I.Config.PrivacyVirtual) _ = Task.Run(async () => { await Task.Delay(20_000); if (!VirtualDisplay.Installed) await VirtualDisplay.InstallAsync(); });
        RegisterRestoreHotkey();
        _main = new MainWindow();
        Hub.I.Init(_main);
        CreateTray();

        bool tray = e.Args.Contains("--tray");
        if (Hub.I.Config.IsConfigured)
        {
            // 运行的是新下载的 exe 时，顺便覆盖安装目录里的旧版本（开机自启用的是那份）
            try { Installer.InstallSelf(); } catch (Exception ex) { Log.Error("自动升级失败", ex); }
            Hub.I.Start();
            // 先换上内置的打洞加速版 EasyTier（会顺带重新注册），再检查启动参数是否最新
            _ = Task.Run(async () =>
            {
                if (Migration.Migrated)
                {
                    // 刚从 FreeRemote 改名过来：组网、开机自启按新名字重新注册
                    try { await Installer.RegisterEasyTierAsync(Hub.I.Config); } catch (Exception ex) { Log.Error("重新注册组网失败", ex); }
                    if (Migration.AutostartWasOn) { try { await Installer.RegisterAutostartAsync(); } catch (Exception ex) { Log.Error("重新注册开机自启失败", ex); } }
                    await Installer.ConfigureFirewallAsync();
                }
                if (!await Installer.EnsureBundledEasyTierAsync(Hub.I.Config))
                    await Installer.EnsureEasyTierCurrentAsync(Hub.I.Config);
            });
        }
        if (!tray || !Hub.I.Config.IsConfigured) ShowMain();
    }

    void RunUninstall(bool quiet)
    {
        bool deleteData = false;
        if (!quiet)
        {
            var r = MessageBox.Show(
                "确定要卸载 Farloop 吗？\n\n" +
                "将删除：程序、开机任务、防火墙规则、快捷方式，并退出组网。\n" +
                "Sunshine 和 Moonlight 会保留（可在“已安装的应用”里单独卸载）。\n\n" +
                "是否同时删除配置和日志（网络密钥、配对记录等）？\n" +
                "  是 = 一起删除    否 = 保留（以后重新安装可直接使用）",
                "卸载 Farloop", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) { Shutdown(); return; }
            deleteData = r == MessageBoxResult.Yes;
        }
        try
        {
            try { Task.Run(() => VirtualDisplay.UninstallAsync()).Wait(30000); } catch { }
            try { Task.Run(SunshineFarloop.RestoreAsync).Wait(60000); } catch { }
            if (!Sync.Run(() => Installer.UninstallAsync(deleteData), 180_000))
                throw new TimeoutException("卸载步骤 3 分钟没有完成");
            if (!quiet) MessageBox.Show("Farloop 已卸载。", "卸载 Farloop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            Log.Error("卸载失败", ex);
            if (!quiet) MessageBox.Show("卸载时出错：" + ex.Message, "卸载 Farloop", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        Shutdown();
    }

    public void ShowMain()
    {
        if (_main == null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    /// <summary>紧急恢复热键 Ctrl+Alt+Shift+F12：在电脑前看不到画面时，按它立刻恢复实体显示器</summary>
    void RegisterRestoreHotkey()
    {
        var p = new System.Windows.Interop.HwndSourceParameters("FarloopHotkey") { Width = 0, Height = 0, WindowStyle = 0 };
        var src = new System.Windows.Interop.HwndSource(p);
        src.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == 0x0312 /*WM_HOTKEY*/ && wp.ToInt32() == 0xF12)
            {
                Log.Info("按下紧急恢复热键，恢复实体显示器");
                Dispatcher.BeginInvoke(() => FreeRemote.PrivacyScreen.ForceOff());
                handled = true;
            }
            return IntPtr.Zero;
        });
        // MOD_ALT 1 | MOD_CONTROL 2 | MOD_SHIFT 4 | MOD_NOREPEAT 0x4000，VK_F12 = 0x7B
        if (!RegisterHotKey(src.Handle, 0xF12, 0x4007, 0x7B)) Log.Info("紧急恢复热键 Ctrl+Alt+Shift+F12 注册失败（可能被别的程序占用）");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);

    /// <summary>自动更新：退出旧版本，但组网继续运行（新版本马上接上，远程连接只断一下）</summary>
    public void ExitForUpdate()
    {
        Exiting = true;
        try { if (VirtualDisplay.IsActive) Task.Run(() => VirtualDisplay.ExitAsync("更新 Farloop")).Wait(15000); } catch { }
        CursorClient.RestoreSystemCursors();
        Hub.I.Stop();
        Assist.StopAll();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    public void ExitApp()
    {
        Exiting = true;
        try { if (VirtualDisplay.IsActive) Task.Run(() => VirtualDisplay.ExitAsync("退出 Farloop")).Wait(15000); } catch { }
        CursorClient.RestoreSystemCursors();
        // “退出”= 本机下线：先告诉对方，再停掉组网（否则组网是后台计划任务，手机那边会一直看到这台电脑在线）。
        // 下次打开 Farloop 或开机时组网会自动启动
        Hub.I.SayGoodbye();
        Hub.I.Stop();
        Assist.StopAll();
        try { Task.Run(Installer.StopEasyTierAsync).Wait(8000); } catch { }
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        Shutdown();
    }

    void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("打开 Farloop", null, (_, _) => ShowMain());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _tray = new WinForms.NotifyIcon
        {
            Icon = AppIcon(),
            Text = "Farloop",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, ev) => { if (ev.Button == WinForms.MouseButtons.Left) ShowMain(); };
        Hub.I.Toast += (text, _) =>
        {
            if (_main is { IsVisible: false }) _tray.ShowBalloonTip(4000, "Farloop", text, WinForms.ToolTipIcon.None);
        };
    }

    /// <summary>运行时画一个蓝色圆角图标，免得带额外资源文件</summary>
    /// <summary>托盘图标：用嵌在 exe 里的 app.ico（16/20/24/32 多尺寸，系统按 DPI 选）</summary>
    static Icon AppIcon()
    {
        try
        {
            var s = GetResourceStream(new Uri("pack://application:,,,/app.ico"))?.Stream;
            if (s != null) return new Icon(s, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception e) { Log.Error("加载图标失败", e); }
        return MakeIcon();
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(64, 64);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = new GraphicsPath();
            int r = 18;
            path.AddArc(0, 0, r, r, 180, 90);
            path.AddArc(64 - r, 0, r, r, 270, 90);
            path.AddArc(64 - r, 64 - r, r, r, 0, 90);
            path.AddArc(0, 64 - r, r, r, 90, 90);
            path.CloseFigure();
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, 64, 64), Color.FromArgb(0x5A, 0xC8, 0xFA), Color.FromArgb(0x00, 0x7A, 0xFF), 45f);
            g.FillPath(brush, path);
            using var pen = new Pen(Color.White, 5) { LineJoin = LineJoin.Round };
            g.DrawRectangle(pen, 14, 17, 36, 24);
            g.DrawLine(pen, 24, 49, 40, 49);
            g.DrawLine(pen, 32, 41, 32, 49);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
