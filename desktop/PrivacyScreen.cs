using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeRemote.Core;
using WinForms = System.Windows.Forms;

namespace FreeRemote;

/// <summary>
/// 被控端“关闭屏幕”（隐私屏）。
/// 不是真的关显示器（那样 Sunshine 也采集不到画面），而是在每块物理屏幕上盖一层黑色窗口，
/// 并设置 WDA_EXCLUDEFROMCAPTURE：显示器上看到的是黑屏，屏幕采集拿到的仍是下面的桌面。
/// 窗口鼠标穿透、不抢焦点，远程操作不受影响。实测本机：普通黑窗口被采集到 100%，排除后仅 2%。
/// </summary>
public static class PrivacyScreen
{
    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint a);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    static readonly List<Window> _windows = new();
    static DispatcherTimer? _keepTop;

    static bool _virtualWanted;
    public static bool IsOn => _windows.Count > 0 || _virtualWanted;

    public static void Set(bool on)
    {
        if (on == IsOn) return;
        if (on && Hub.I.Config.PrivacyVirtual)
        {
            // 虚拟屏：实体显示器真正熄灭。要几秒，后台做；失败就退回黑色遮罩
            _virtualWanted = true;
            Log.Info("隐私屏 开启（虚拟屏，熄灭显示器）");
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                var err = await VirtualDisplay.EnterAsync();
                if (err == null) { await System.Threading.Tasks.Task.Delay(1500); Hub.I.BroadcastSelfInfo(); return; }
                Log.Error("虚拟屏息屏失败，改用黑色遮罩：" + err);
                Application.Current.Dispatcher.Invoke(() => { if (_virtualWanted) { _virtualWanted = false; Show(); } });
            });
            return;
        }
        if (!on && _virtualWanted)
        {
            _virtualWanted = false;
            Log.Info("隐私屏 关闭（恢复实体显示器）");
            _ = System.Threading.Tasks.Task.Run(async () => { await VirtualDisplay.ExitAsync(); await System.Threading.Tasks.Task.Delay(1500); Hub.I.BroadcastSelfInfo(); });
            return;
        }
        if (on) Show(); else Hide();
        Log.Info("隐私屏 " + (on ? "开启" : "关闭"));
    }

    /// <summary>紧急恢复（Ctrl+Alt+Shift+F12、退出程序时）</summary>
    public static void ForceOff()
    {
        _virtualWanted = false;
        Hide();
        _ = System.Threading.Tasks.Task.Run(() => VirtualDisplay.ExitAsync("手动恢复"));
    }

    static void Show()
    {
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var b = screen.Bounds; // 物理像素
            var w = new Window
            {
                WindowStyle = WindowStyle.None,
                // 让 WPF 自己把窗口做成分层窗口（手动加 WS_EX_LAYERED 会被 WPF 显示时去掉，实测）；
                // 只有分层 + WS_EX_TRANSPARENT，远程的鼠标点击才能穿透黑屏落到下面的程序上
                AllowsTransparency = true,
                ResizeMode = ResizeMode.NoResize,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = Brushes.Black,
                Width = 100, Height = 100, Left = b.Left, Top = b.Top,
                Content = new TextBlock
                {
                    Text = "此电脑正在被远程控制",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x48, 0x48, 0x4A)),
                    FontSize = 22,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            w.SourceInitialized += (_, _) =>
            {
                var h = new WindowInteropHelper(w).Handle;
                SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                SetWindowPos(h, HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
                if (!SetWindowDisplayAffinity(h, WDA_EXCLUDEFROMCAPTURE))
                    Log.Error("SetWindowDisplayAffinity 失败（需要 Windows 10 2004 及以上）");
            };
            w.Show();
            _windows.Add(w);
        }

        // 别的置顶窗口可能盖上来，定时重新置顶
        _keepTop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _keepTop.Tick += (_, _) =>
        {
            foreach (var w in _windows)
                SetWindowPos(new WindowInteropHelper(w).Handle, HWND_TOPMOST, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE|NOMOVE|NOACTIVATE
        };
        _keepTop.Start();
    }

    static void Hide()
    {
        _keepTop?.Stop();
        _keepTop = null;
        foreach (var w in _windows) { try { w.Close(); } catch { } }
        _windows.Clear();
    }
}
