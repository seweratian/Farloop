using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeRemote.Core;
using WinForms = System.Windows.Forms;

namespace FreeRemote;

static class Win
{
    public const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20;
    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int i, int v);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ChangeWindowMessageFilterEx(IntPtr h, uint msg, uint action, IntPtr info);
    [DllImport("shell32.dll")] public static extern void DragAcceptFiles(IntPtr h, bool accept);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint DragQueryFile(IntPtr hDrop, uint i, StringBuilder? sb, uint len);
    [DllImport("shell32.dll")] public static extern bool DragQueryPoint(IntPtr hDrop, out POINT p);
    [DllImport("shell32.dll")] public static extern void DragFinish(IntPtr hDrop);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

    public static readonly IntPtr TOPMOST = new(-1);
    public const uint NOACTIVATE = 0x0010, SHOWWINDOW = 0x0040, NOSIZE = 0x1, NOMOVE = 0x2;
    const uint WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049, MSGFLT_ALLOW = 1;

    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    /// <summary>注入一次左键松开（考虑左右键互换）</summary>
    public static void LeftUp() => mouse_event(WinForms.SystemInformation.MouseButtonsSwapped ? 0x0010u : 0x0004u, 0, 0, 0, IntPtr.Zero);

    public static bool LeftDown =>(GetAsyncKeyState(WinForms.SystemInformation.MouseButtonsSwapped ? 0x02 : 0x01) & 0x8000) != 0;

    public static void SetPassThrough(Window w, bool pass)
    {
        var h = new WindowInteropHelper(w).Handle;
        if (h == IntPtr.Zero) return;
        int ex = GetWindowLong(h, GWL_EXSTYLE);
        int want = pass ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        if (want != ex) SetWindowLong(h, GWL_EXSTYLE, want);
    }

    /// <summary>
    /// 让窗口接收资源管理器拖进来的文件 —— 即使本程序是管理员权限。
    /// FreeRemote 以管理员运行，而资源管理器是普通权限；Windows 的权限隔离（UIPI）会拦截 OLE 拖放，光标显示“禁止”。
    /// 经典的 WM_DROPFILES 方式可以通过 ChangeWindowMessageFilterEx 放行，所以这里不用 WPF 的 AllowDrop（那是 OLE）。
    /// 回调参数：文件列表、落点（窗口客户区物理像素）。
    /// </summary>
    public static void EnableFileDrop(Window w, Action<string[], POINT> onDrop)
    {
        var h = new WindowInteropHelper(w).Handle;
        foreach (var m in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            ChangeWindowMessageFilterEx(h, m, MSGFLT_ALLOW, IntPtr.Zero);
        DragAcceptFiles(h, true);
        HwndSource.FromHwnd(h)!.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg != WM_DROPFILES) return IntPtr.Zero;
            handled = true;
            try
            {
                uint n = DragQueryFile(wp, 0xFFFFFFFF, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder((int)DragQueryFile(wp, i, null, 0) + 1);
                    DragQueryFile(wp, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
                DragQueryPoint(wp, out var pt);
                if (files.Count > 0) onDrop(files.ToArray(), pt);
            }
            catch (Exception e) { Log.Error("处理拖入的文件失败", e); }
            finally { DragFinish(wp); }
            return IntPtr.Zero;
        });
    }

    /// <summary>光标下真正可见的顶层窗口是不是串流窗口（被资源管理器等窗口挡住时返回 false）</summary>
    public static bool StreamUnderCursor(IntPtr win, POINT c) => win != IntPtr.Zero && GetAncestor(WindowFromPoint(c), 2) == win;

    /// <summary>串流窗口客户区（屏幕物理像素）</summary>
    public static bool ClientRectOnScreen(IntPtr win, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (win == IntPtr.Zero || !GetClientRect(win, out var cr)) return false;
        var o = new POINT();
        ClientToScreen(win, ref o);
        x = o.X; y = o.Y; w = cr.R - cr.L; h = cr.B - cr.T;
        return w > 0 && h > 0;
    }

    /// <summary>客户区里的点 → 画面（去掉黑边）里的 0..1 坐标，算法与 Moonlight 一致</summary>
    public static bool ToVideo(int cw, int ch, string resolution, int px, int py, out float nx, out float ny)
    {
        nx = ny = -1;
        var p = resolution.Split('x');
        if (p.Length != 2 || !int.TryParse(p[0], out var sw) || !int.TryParse(p[1], out var sh) || cw <= 0 || ch <= 0) return false;
        double scale = Math.Min((double)cw / sw, (double)ch / sh);
        double vw = sw * scale, vh = sh * scale, vx = (cw - vw) / 2, vy = (ch - vh) / 2;
        nx = (float)Math.Clamp((px - vx) / vw, 0, 1);
        ny = (float)Math.Clamp((py - vy) / vh, 0, 1);
        return true;
    }
}

/// <summary>
/// 控制端：盖在 Moonlight 画面上的透明“接收层”，负责两个方向的拖拽判断。
///  · 拖进：从窗口外按住鼠标拖到画面上 → 接收层变成可放置，松开就把文件发到被控端的落点。
///  · 拖出：在画面里按住、拖到窗口外 → 跟随光标的提示；在本地松开时记下落点文件夹，
///          被控端那边同时收到“拖到屏幕边缘的文件”后，就把文件传到这个文件夹。
/// </summary>
public sealed class DropCatcher : Window
{
    readonly Func<IntPtr> _streamWindow;
    readonly Func<string> _resolution;
    readonly Action<string[], float, float> _onDrop;
    readonly Action<string?>? _onDragOutRelease; // null = 不负责“拖出”（由管理员主程序检测，见 Overlay）
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Border _hint;
    readonly string _peer;
    bool _wasDown, _startedInside, _armed;
    DateTime _upAt;
    int _cx, _cy, _cw, _ch;
    DragGhost? _ghost;

    public DropCatcher(string peerName, Func<IntPtr> streamWindow, Func<string> resolution,
        Action<string[], float, float> onDrop, Action<string?>? onDragOutRelease)
    {
        _peer = peerName;
        _streamWindow = streamWindow;
        _resolution = resolution;
        _onDrop = onDrop;
        _onDragOutRelease = onDragOutRelease;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)); // alpha=1：看不见，但能被拖放命中
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        Width = Height = 1;

        _hint = new Border
        {
            Visibility = Visibility.Collapsed,
            Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x5F, 0xB8)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x60, 0xCD, 0xFF)),
            BorderThickness = new Thickness(3),
            Child = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x20, 0x20)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(18, 12, 18, 12),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = $"松开即可发送到 {peerName}（放进对方当前的文件夹或桌面）", Foreground = Brushes.White, FontSize = 15 },
            },
        };
        Content = _hint;

        SourceInitialized += (_, _) =>
        {
            Overlay.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: true);
            Win.EnableFileDrop(this, OnFileDrop);
            _timer.Start();
        };
        Closed += (_, _) => { _timer.Stop(); _ghost?.Close(); };
        _timer.Tick += (_, _) => Tick();

        // 在普通权限的拖拽助手里，标准 OLE 拖放可用：资源管理器、微信、浏览器等任何程序拖来的文件都能收
        AllowDrop = true;
        DragEnter += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            var pos = e.GetPosition(this);
            var dpi = VisualTreeHelper.GetDpi(this);
            OnFileDrop(paths, new Win.POINT { X = (int)(pos.X * dpi.DpiScaleX), Y = (int)(pos.Y * dpi.DpiScaleY) });
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        };
    }

    void OnFileDrop(string[] paths, Win.POINT pt)
    {
        // 接收层铺满串流窗口客户区，落点就是客户区坐标
        Log.Info($"拖进远程画面：收到 {paths.Length} 项，落点 ({pt.X},{pt.Y})");
        if (Win.ToVideo(_cw, _ch, _resolution(), pt.X, pt.Y, out var nx, out var ny)) _onDrop(paths, nx, ny);
        Disarm();
    }

    void Tick()
    {
        bool down = Win.LeftDown;
        var win = _streamWindow();
        Win.GetCursorPos(out var c);
        bool hasRect = win != IntPtr.Zero && !Win.IsIconic(win) && Win.ClientRectOnScreen(win, out _cx, out _cy, out _cw, out _ch);
        bool inside = hasRect && c.X >= _cx && c.Y >= _cy && c.X < _cx + _cw && c.Y < _cy + _ch;

        // 按下时光标下必须真的是 Moonlight：资源管理器窗口叠在画面上方时，从它里面拖文件不算“画面里按下”
        if (down && !_wasDown) _startedInside = inside && Win.StreamUnderCursor(win, c);
        if (!down && _wasDown) _upAt = DateTime.UtcNow;
        _wasDown = down;

        // ---- 拖进：别处按下、拖到画面上 ----
        if (!_armed && down && !_startedInside && inside && Win.StreamUnderCursor(win, c)) Arm();
        // 由主程序展开的接收层也由主程序收起（本进程在 Moonlight 前台时读不到按键状态，不能自己判断）
        else if (_armed && !_external && !down && (DateTime.UtcNow - _upAt).TotalMilliseconds > 300) Disarm();

        // ---- 拖出：画面里按下、拖到窗口外 ----
        if (_onDragOutRelease != null && _ghost == null && down && _startedInside && hasRect && !inside)
        {
            _ghost = new DragGhost(_peer, _streamWindow, folder =>
            {
                _ghost = null;
                _onDragOutRelease(folder);
            });
            _ghost.Show();
        }
    }

    bool _external;

    /// <summary>主程序（管理员）通知：有文件正从外部拖进画面 / 拖拽已结束</summary>
    public void ExternalArm(bool on)
    {
        if (!on) { _external = false; Disarm(); return; }
        var win = _streamWindow();
        if (win == IntPtr.Zero || Win.IsIconic(win) || !Win.ClientRectOnScreen(win, out _cx, out _cy, out _cw, out _ch))
        {
            Log.Error("拖进远程画面：找不到画面窗口，无法展开接收层");
            return;
        }
        _external = true;
        if (!_armed) Arm();
    }

    void Arm()
    {
        Log.Info($"拖进远程画面：接收层已展开 ({_cx},{_cy} {_cw}x{_ch})");
        var h = new WindowInteropHelper(this).Handle;
        Win.SetWindowPos(h, Win.TOPMOST, _cx, _cy, _cw, _ch, Win.NOACTIVATE | Win.SHOWWINDOW);
        Win.SetPassThrough(this, false);
        _hint.Visibility = Visibility.Visible;
        _armed = true;
    }

    void Disarm()
    {
        _external = false;
        if (!_armed) return;
        Win.SetPassThrough(this, true);
        Win.SetWindowPos(new WindowInteropHelper(this).Handle, Win.TOPMOST, _cx, _cy, 1, 1, Win.NOACTIVATE);
        _hint.Visibility = Visibility.Collapsed;
        _armed = false;
    }
}

/// <summary>
/// 控制端（管理员主程序里）：检测“在远程画面里按住、拖到窗口外”，弹出跟随提示，松手时得到本地落点。
/// 必须在管理员进程里做：Moonlight 窗口是管理员权限，普通权限进程读不到它在前台时的鼠标按键状态（防键盘记录）。
/// </summary>
public sealed class DragOutWatcher
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly Func<IntPtr> _streamWindow;
    readonly string _peer;
    readonly Action<string?> _onRelease;
    readonly Action<bool>? _onDragIn;
    bool _wasDown, _startedInside, _dragInArmed;
    DateTime _upAt;
    DragGhost? _ghost;

    public DragOutWatcher(string peer, Func<IntPtr> streamWindow, Action<string?> onRelease, Action<bool>? onDragIn = null)
    {
        _peer = peer;
        _streamWindow = streamWindow;
        _onRelease = onRelease;
        _onDragIn = onDragIn;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    void Tick()
    {
        bool down = Win.LeftDown;
        var win = _streamWindow();
        Win.GetCursorPos(out var c);
        int x = 0, y = 0, w = 0, h = 0;
        bool hasRect = win != IntPtr.Zero && !Win.IsIconic(win) && Win.ClientRectOnScreen(win, out x, out y, out w, out h);
        bool inside = hasRect && c.X >= x && c.Y >= y && c.X < x + w && c.Y < y + h;
        if (down && !_wasDown) _startedInside = inside && Win.StreamUnderCursor(win, c);
        if (!down && _wasDown) _upAt = DateTime.UtcNow;
        _wasDown = down;

        // ---- 拖进：别处按下（资源管理器、桌面……）拖到画面上 → 让助手展开接收层 ----
        if (_onDragIn != null)
        {
            if (!_dragInArmed && down && !_startedInside && inside && Win.StreamUnderCursor(win, c))
            {
                _dragInArmed = true;
                Log.Info("拖进远程画面：检测到从外部拖入，展开接收层");
                _onDragIn(true);
            }
            else if (_dragInArmed && !down && (DateTime.UtcNow - _upAt).TotalMilliseconds > 600)
            {
                _dragInArmed = false;
                _onDragIn(false);
            }
        }

        if (_ghost == null && down && _startedInside && hasRect && !inside)
        {
            _ghost = new DragGhost(_peer, _streamWindow, folder => { _ghost = null; _onRelease(folder); });
            _ghost.Show();
        }
    }

    public void Stop() { _timer.Stop(); _ghost?.Close(); }
}

/// <summary>
/// 控制端：从远程画面里往外拖时跟着光标的提示；松开时判断落点（资源管理器文件夹 / 桌面 / 拖回画面=取消）。
/// </summary>
public sealed class DragGhost : Window
{
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    readonly TextBlock _text;
    readonly Func<IntPtr> _streamWindow;
    readonly Action<string?> _onRelease;   // 落点文件夹；null = 取消
    readonly DateTime _started = DateTime.UtcNow;

    public DragGhost(string peer, Func<IntPtr> streamWindow, Action<string?> onRelease)
    {
        _streamWindow = streamWindow;
        _onRelease = onRelease;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        _text = new TextBlock { Foreground = Brushes.White, FontSize = 13 };
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0x60, 0xCD, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 8, 14, 8),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    new TextBlock { Text = "", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0xCD, 0xFF)), FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Width = 8 },
                    _text,
                },
            },
        };
        SourceInitialized += (_, _) => Overlay.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: true);
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) => _timer.Start();
        Tick();
    }

    void Tick()
    {
        Win.GetCursorPos(out var c);
        var win = _streamWindow();
        bool overStream = Win.ClientRectOnScreen(win, out var x, out var y, out var w, out var h) && c.X >= x && c.Y >= y && c.X < x + w && c.Y < y + h;
        var target = overStream ? (ShellTarget.Kind.None, null) : ShellTarget.At(c.X, c.Y);
        _text.Text = overStream ? "拖回远程画面 · 松开取消" :
            target.Item1 == ShellTarget.Kind.Desktop ? "远程文件  →  松开复制到桌面" :
            target.Item2 != null ? $"远程文件  →  松开复制到 {System.IO.Path.GetFileName(target.Item2.TrimEnd('\\'))}" :
            "远程文件  →  松开复制到接收文件夹";

        var dpi = VisualTreeHelper.GetDpi(this);
        Left = c.X / dpi.DpiScaleX + 18;
        Top = c.Y / dpi.DpiScaleY + 20;

        bool timeout = (DateTime.UtcNow - _started).TotalSeconds > 60;
        if (!Win.LeftDown || timeout)
        {
            _timer.Stop();
            Close();
            _onRelease(overStream || timeout ? null : target.Item2 ?? Hub.I.Config.ReceiveFolder);
        }
    }
}

/// <summary>
/// 被控端：屏幕四边各一条 2 像素的透明“接收条”。
/// 控制端把文件拖出画面时，Moonlight 会把被控端光标停在屏幕边缘；松手时被控端的拖拽就“放”在接收条上，
/// 我们拿到文件列表，通知控制端来取。平时鼠标穿透，不影响点任务栏、开始按钮。
/// 同样用 WM_DROPFILES，避免管理员权限下收不到资源管理器的拖放。
/// </summary>
public sealed class EdgeCatcher
{
    readonly List<Window> _strips = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    readonly Action<string[]> _onFilesAtEdge;
    bool _active;
    DateTime _upAt;

    readonly System.Drawing.Rectangle? _area;

    /// <param name="area">放接收条的屏幕范围（物理像素）；空 = 主屏</param>
    public EdgeCatcher(Action<string[]> onFilesAtEdge, System.Drawing.Rectangle? area = null)
    {
        _onFilesAtEdge = onFilesAtEdge;
        _area = area;
        _timer.Tick += (_, _) => Tick();
    }

    public bool Running => _strips.Count > 0;

    public void Start()
    {
        if (Running) return;
        var b = _area ?? WinForms.Screen.PrimaryScreen!.Bounds;
        const int t = 2;
        foreach (var (x, y, w, h) in new[] { (b.Left, b.Top, b.Width, t), (b.Left, b.Bottom - t, b.Width, t), (b.Left, b.Top, t, b.Height), (b.Right - t, b.Top, t, b.Height) })
        {
            var win = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)),
                Topmost = true, ShowInTaskbar = false, ShowActivated = false, ResizeMode = ResizeMode.NoResize, Width = 1, Height = 1,
            };
            int px = x, py = y, pw = w, ph = h;
            win.SourceInitialized += (_, _) =>
            {
                var hw = new WindowInteropHelper(win).Handle;
                Overlay.MakeOverlay(hw, clickThrough: true);
                Win.SetWindowPos(hw, Win.TOPMOST, px, py, pw, ph, Win.NOACTIVATE | Win.SHOWWINDOW);
                Win.EnableFileDrop(win, (files, _) => _onFilesAtEdge(files));
            };
            win.AllowDrop = true; // 普通权限的助手里用标准 OLE 拖放
            win.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
            win.Drop += (_, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) _onFilesAtEdge(files);
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            };
            win.Show();
            _strips.Add(win);
        }
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        foreach (var w in _strips) { try { w.Close(); } catch { } }
        _strips.Clear();
    }

    void Tick()
    {
        bool down = Win.LeftDown;
        if (down)
        {
            _upAt = default;
            if (!_active) { foreach (var w in _strips) Win.SetPassThrough(w, false); _active = true; }
        }
        else if (_active)
        {
            if (_upAt == default) _upAt = DateTime.UtcNow;
            if ((DateTime.UtcNow - _upAt).TotalMilliseconds > 400)
            {
                foreach (var w in _strips) Win.SetPassThrough(w, true);
                _active = false;
                _upAt = default;
            }
        }
        foreach (var w in _strips) Win.SetWindowPos(new WindowInteropHelper(w).Handle, Win.TOPMOST, 0, 0, 0, 0, Win.NOACTIVATE | Win.NOSIZE | Win.NOMOVE);
    }
}
