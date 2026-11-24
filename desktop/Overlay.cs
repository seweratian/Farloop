using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 管理贴在 Moonlight 窗口上的浮层：右上角的控制中心按钮（标题栏 最小化/最大化/关闭 左边）+ 左上角信息卡片。
/// 用系统窗口事件（位置变化、前台切换、最小化）实时跟随，无轮询延迟。
/// </summary>
public sealed class Overlay
{
    // ---------------- Win32 ----------------
    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    const int DWMWA_CAPTION_BUTTON_BOUNDS = 5;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObj, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }

    /// <summary>浮层窗口：不进任务栏、不抢焦点；clickThrough 时鼠标直接穿过</summary>
    public static void MakeOverlay(IntPtr h, bool clickThrough)
    {
        int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        if (clickThrough) ex |= WS_EX_TRANSPARENT;
        SetWindowLong(h, GWL_EXSTYLE, ex);
    }

    /// <summary>给 Moonlight 发它自己的快捷键（Ctrl+Alt+Shift+键）。浮层不抢焦点，所以 Moonlight 仍在前台。</summary>
    public static async Task SendMoonlightHotkeyAsync(char key)
    {
        const byte CTRL = 0x11, ALT = 0x12, SHIFT = 0x10;
        const uint UP = 0x2;
        byte k = (byte)char.ToUpperInvariant(key);
        foreach (var m in new[] { CTRL, ALT, SHIFT }) keybd_event(m, 0, 0, UIntPtr.Zero);
        keybd_event(k, 0, 0, UIntPtr.Zero);
        await Task.Delay(40);
        keybd_event(k, 0, UP, UIntPtr.Zero);
        foreach (var m in new[] { SHIFT, ALT, CTRL }) keybd_event(m, 0, UP, UIntPtr.Zero);
    }

    // ---------------- 实例 ----------------
    readonly DeviceVM _dev;
    Process _proc;
    readonly ControlCenter _cc;
    StreamInfoCard? _card;
    readonly WinEventProc _cb;   // 必须持有引用，防止被 GC
    IntPtr _hookFg, _hookLoc;
    readonly DispatcherTimer _fallback = new() { Interval = TimeSpan.FromMilliseconds(400) };
    bool _pending, _closed;

    public Overlay(DeviceVM dev, Process proc, StreamSettings active, bool showCard)
    {
        _dev = dev;
        _proc = proc;
        _active = active;
        _cb = (_, _, _, _, _, _, _) => Schedule();
        _cc = new ControlCenter(this, dev, active);
        _cc.Show();
        // 拖进：接收层放在普通权限的拖拽助手里（管理员窗口收不到资源管理器的拖放）
        Hub.I.Drag?.Catcher(dev.Ip, proc.Id, active.Resolution, dev.Name);
        // 拖出：在本（管理员）进程里检测
        _dragOut = new DragOutWatcher(dev.Name, FindStreamWindow, folder => Hub.I.SetDragOutTarget(dev, folder),
            arm => Hub.I.Drag?.Arm(dev.Ip, arm)); // 拖进：按键状态只有管理员进程读得到，由这里通知助手展开接收层
        SetCardVisible(showCard, active);
        Hook();
        _fallback.Tick += (_, _) => Update();
        _fallback.Start();
    }

    public DeviceVM Device => _dev;
    StreamSettings _active;
    readonly DragOutWatcher _dragOut;

    public IntPtr StreamWindow => FindStreamWindow();

    /// <summary>重新连接后换成新的 Moonlight 进程</summary>
    public void Attach(Process proc, StreamSettings active)
    {
        _proc = proc;
        _active = active;
        Hub.I.Drag?.Catcher(_dev.Ip, proc.Id, active.Resolution, _dev.Name);
        _cc.SetActive(active);
        _card?.UpdateVideo(active);
        Unhook();
        Hook();
        Schedule();
    }

    public void SetCardVisible(bool on, StreamSettings s)
    {
        if (on && _card == null)
        {
            _card = new StreamInfoCard(_dev, s, Hub.I.Config.VirtualIp);
            _card.Show();
        }
        else if (!on && _card != null)
        {
            _card.Close();
            _card = null;
        }
        Schedule();
    }

    void Hook()
    {
        _hookFg = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _cb, 0, 0, 0);
        _hookLoc = SetWinEventHook(EVENT_SYSTEM_MINIMIZESTART, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _cb, (uint)_proc.Id, 0, 0);
    }

    void Unhook()
    {
        if (_hookFg != IntPtr.Zero) UnhookWinEvent(_hookFg);
        if (_hookLoc != IntPtr.Zero) UnhookWinEvent(_hookLoc);
        _hookFg = _hookLoc = IntPtr.Zero;
    }

    void Schedule()
    {
        if (_pending || _closed) return;
        _pending = true;
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Render, () => { _pending = false; Update(); });
    }

    IntPtr FindStreamWindow()
    {
        try { return FindStreamWindow((uint)_proc.Id); } catch { return IntPtr.Zero; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder sb, int max);

    /// <summary>串流画面是 SDL 窗口（类名 SDL_app）；找不到时退回该进程面积最大的可见窗口</summary>
    public static IntPtr FindStreamWindow(uint pid)
    {
        IntPtr best = IntPtr.Zero, sdl = IntPtr.Zero;
        long bestArea = 0;
        var sb = new System.Text.StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out var p);
            if (p == pid && IsWindowVisible(h) && GetWindowRect(h, out var r))
            {
                sb.Clear();
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "SDL_app") { sdl = h; return false; }
                long area = (long)(r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
            }
            return true;
        }, IntPtr.Zero);
        return sdl != IntPtr.Zero ? sdl : best;
    }

    // ================= 无缝切换：冻结画面 + 还原窗口位置 =================
    const int GWL_STYLE = -16;
    const int WS_CAPTION = 0x00C00000;
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length, flags, showCmd;
        public POINT minPos, maxPos;
        public RECT normal;
    }

    public sealed class Snapshot
    {
        internal WINDOWPLACEMENT Placement;
        public int X, Y, W, H;                 // 可见区域（物理像素），冻结画面盖在这里
        public bool Windowed;                  // 有标题栏
        public System.Windows.Media.Imaging.BitmapSource? Image;
    }

    /// <summary>记下 Moonlight 窗口当前的位置、大小、最大化状态，并截一帧画面</summary>
    public Snapshot? TakeSnapshot()
    {
        var h = FindStreamWindow();
        if (h == IntPtr.Zero || IsIconic(h)) return null;
        var s = new Snapshot();
        s.Placement.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        GetWindowPlacement(h, ref s.Placement);
        s.Windowed = (GetWindowLong(h, GWL_STYLE) & WS_CAPTION) == WS_CAPTION;
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var vis, Marshal.SizeOf<RECT>()) != 0) GetWindowRect(h, out vis);
        s.X = vis.L; s.Y = vis.T; s.W = vis.R - vis.L; s.H = vis.B - vis.T;

        // 截图前先藏起自己的浮层，免得被截进去
        _cc.Place(0, 0, false);
        _card?.Place(0, 0, false);
        try
        {
            using var bmp = new System.Drawing.Bitmap(s.W, s.H);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(s.X, s.Y, 0, 0, new System.Drawing.Size(s.W, s.H));
            var hb = bmp.GetHbitmap();
            try
            {
                s.Image = Imaging.CreateBitmapSourceFromHBitmap(hb, IntPtr.Zero, Int32Rect.Empty,
                    System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                s.Image.Freeze();
            }
            finally { DeleteObject(hb); }
        }
        catch (Exception e) { Log.Error("截取画面失败", e); }
        return s;
    }

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    /// <summary>等新 Moonlight 进程的画面窗口出现</summary>
    public static async Task<IntPtr> WaitForStreamWindowAsync(Process p, int timeoutMs)
    {
        var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < until)
        {
            if (p.HasExited) return IntPtr.Zero;
            var sb = new System.Text.StringBuilder(64);
            var h = FindStreamWindow((uint)p.Id);
            if (h != IntPtr.Zero)
            {
                GetClassName(h, sb, sb.Capacity);
                if (sb.ToString() == "SDL_app") return h;
            }
            await Task.Delay(40);
        }
        return IntPtr.Zero;
    }

    public static void Focus(IntPtr h) => SetForegroundWindow(h);

    /// <summary>新窗口放回旧窗口的位置和大小（含最大化状态）</summary>
    public static void RestorePlacement(IntPtr h, Snapshot s)
    {
        if (!s.Windowed) return; // 全屏/无边框由 Moonlight 自己铺满屏幕
        var wp = s.Placement;
        wp.length = Marshal.SizeOf<WINDOWPLACEMENT>();
        SetWindowPlacement(h, ref wp);
        SetForegroundWindow(h);
    }

    void Update()
    {
        if (_closed) return;
        var h = FindStreamWindow();
        bool visible = false;
        if (h != IntPtr.Zero && !IsIconic(h))
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var fg);
            try { visible = fg == (uint)_proc.Id; } catch { }
        }
        if (!visible)
        {
            _cc.Place(0, 0, false);
            _card?.Place(0, 0, false);
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(_cc);
        double sx = dpi.DpiScaleX, sy = dpi.DpiScaleY;
        GetWindowRect(h, out var wr);
        var co = new POINT();
        ClientToScreen(h, ref co);
        GetClientRect(h, out var cr);

        // 可见边框（最大化时 GetWindowRect 会超出屏幕约 8px 的隐藏缩放边框，不能用它算位置）
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out var vis, Marshal.SizeOf<RECT>()) != 0) vis = wr;

        // 有标题栏（窗口模式）：放在 最小化/最大化/关闭 左边，在“可见顶边 ~ 画面顶边”之间垂直居中
        if (co.Y - vis.T > 12 &&
            DwmGetWindowAttribute(h, DWMWA_CAPTION_BUTTON_BOUNDS, out var cb, Marshal.SizeOf<RECT>()) == 0 && cb.R - cb.L > 0)
        {
            double right = (vis.R - (cb.R - cb.L)) / sx - 6;   // 系统按钮总是贴着可见右边缘
            double centerY = (vis.T + co.Y) / 2.0 / sy;
            _cc.Place(right, centerY - ControlCenter.PillHeight / 2, true);
        }
        else
        {
            // 无边框 / 全屏：放在画面右上角
            _cc.Place((co.X + cr.R) / sx - 12, co.Y / sy + 10, true);
        }
        // 信息卡片：放在远程画面右下角、对方任务栏（时钟）上方，避开左上角 Moonlight 的“详细性能统计”
        double vx = co.X, vy = co.Y, vw = cr.R, vh = cr.B;
        var res = _active.Resolution.Split('x');
        if (res.Length == 2 && int.TryParse(res[0], out var rw) && int.TryParse(res[1], out var rh) && cr.R > 0 && cr.B > 0)
        {
            double k = Math.Min((double)cr.R / rw, (double)cr.B / rh); // 黑边算法与 Moonlight 一致
            vw = rw * k; vh = rh * k;
            vx = co.X + (cr.R - vw) / 2; vy = co.Y + (cr.B - vh) / 2;
        }
        double brRight = (vx + vw) / sx - 4;
        double brBottom = (vy + vh - vh * TaskbarFraction) / sy - 2;
        if (_card != null)
        {
            _card.Place(brRight - _card.ActualWidth, brBottom - _card.ActualHeight, true);
            brBottom -= _card.ActualHeight - 8; // 卡片自带 8 的外边距，叠放时扣掉一份
        }
        BottomRightAnchor = (brRight, brBottom, _proc.Id);
    }

    /// <summary>对方任务栏大约占画面高度的比例（100% 缩放 1080p 是 48/1080，4K 150% 是 72/2160）</summary>
    const double TaskbarFraction = 0.042;

    /// <summary>远程画面右下角可用的位置（WPF 单位，已让出任务栏和信息卡片）：复制进度卡片贴在这里</summary>
    public static (double Right, double Bottom, int Pid)? BottomRightAnchor { get; private set; }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _fallback.Stop();
        Unhook();
        try { _cc.Close(); } catch { }
        try { _card?.Close(); } catch { }
        Hub.I.Drag?.StopCatcher(_dev.Ip);
        _dragOut.Stop();
    }
}
