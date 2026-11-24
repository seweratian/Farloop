using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Threading;

namespace FreeRemote.Core;

// 光标同步协议（在 FreeRemote 自己的加密会话上，和 Moonlight 无关）：
//   LocalCursor   控制端→被控端 [1 开 / 0 关]
//   CursorMode    被控端→控制端 [需要切换 Sunshine 光标显示 1/0]
//   CursorShape   被控端→控制端 [8 哈希][2 热点X][2 热点Y][PNG]
//   CursorRef     被控端→控制端 [8 哈希]   之前发过的形状，只发编号
//   CursorHidden  被控端→控制端 []        被控端光标被隐藏（打字、游戏）
//   CursorPos     被控端→控制端 [4 x][4 y] 相对主屏的 0..1 坐标

/// <summary>被控端：高频读取光标，形状变化时才发送</summary>
public sealed class CursorHost
{
    PeerSession? _target;
    Thread? _thread;
    volatile bool _run;

    public PeerSession? Target => _target;

    // ---------- Sunshine 的 Ctrl+Alt+Shift+N 开关状态 ----------
    // 状态在 Sunshine 进程里一直保持；FreeRemote 重启不能丢，所以写到磁盘，按“进程号 + 启动时间”识别是不是同一个 Sunshine
    static string StateFile => System.IO.Path.Combine(AppConfig.DataDir, "sunshine-cursor.txt");

    static string SunshineKey()
    {
        var p = Process.GetProcessesByName("sunshine").FirstOrDefault();
        if (p == null) return "";
        try { return $"{p.Id}|{p.StartTime.Ticks}"; } catch { return p.Id.ToString(); }
    }

    /// <summary>返回控制端是否需要按一次 Ctrl+Alt+Shift+N</summary>
    public bool NeedToggleFor(bool wantHidden)
    {
        var key = SunshineKey();
        bool hidden = false;
        try
        {
            var parts = System.IO.File.Exists(StateFile) ? System.IO.File.ReadAllText(StateFile).Split('\n') : Array.Empty<string>();
            if (parts.Length == 2 && parts[0] == key) hidden = parts[1].Trim() == "1"; // 不是同一个 Sunshine（重启过）→ 默认显示
        }
        catch { }
        bool need = wantHidden != hidden;
        if (need) // 控制端收到后会立即按
        {
            try { System.IO.File.WriteAllText(StateFile, key + "\n" + (wantHidden ? "1" : "0")); } catch { }
        }
        return need;
    }

    // ---------- 被控端系统光标换成透明 ----------
    // 拖动窗口时 Windows 会把光标直接合成进屏幕画面，Sunshine 的“隐藏光标”管不到（已实测）。
    // 远程期间把系统光标全部换成透明的，画面里就永远不会出现光标；形状用事先记下的原样同步给控制端。
    // 实测：SetSystemCursor 替换后，GetCursorInfo 返回的句柄不变，所以可以按句柄查原来的形状。
    [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, int id);
    [DllImport("user32.dll")] static extern bool SetSystemCursor(IntPtr h, uint id);
    static readonly int[] SystemCursorIds =
        { 32512, 32513, 32514, 32515, 32516, 32642, 32643, 32644, 32645, 32646, 32648, 32649, 32650, 32651, 32671, 32672 };

    Dictionary<IntPtr, CursorNative.Shape?> _systemShapes = new();
    bool _blanked;

    void BlankSystemCursors()
    {
        CursorClient.RestoreSystemCursors(); // 先恢复，确保记下的是原样（防止上次异常退出残留）
        var shapes = new Dictionary<IntPtr, CursorNative.Shape?>();
        foreach (var id in SystemCursorIds)
        {
            var h = LoadCursor(IntPtr.Zero, id);
            if (h != IntPtr.Zero && !shapes.ContainsKey(h)) shapes[h] = CursorNative.Capture(h);
        }
        _systemShapes = shapes;
        foreach (var id in SystemCursorIds) SetSystemCursor(CursorNative.CreateBlankCursor(), (uint)id);
        _blanked = true;
        Log.Info("远程中：被控端系统光标已隐藏");
    }

    void RestoreCursors()
    {
        if (!_blanked) return;
        CursorClient.RestoreSystemCursors();
        _blanked = false;
        Log.Info("被控端系统光标已恢复");
    }

    public void Start(PeerSession s)
    {
        Stop();
        try { BlankSystemCursors(); } catch (Exception e) { Log.Error("隐藏被控端光标失败", e); }
        _target = s;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CursorHost", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _target = null;
        RestoreCursors();
    }

    void Loop()
    {
        var s = _target;
        if (s == null) return;
        var sent = new HashSet<ulong>();
        // 系统光标已被换成透明，用事先记下的原样；应用自己的光标照常读取
        var byHandle = new Dictionary<IntPtr, CursorNative.Shape?>(_systemShapes);
        IntPtr lastHandle = new(-1);
        bool lastVisible = false;
        int lastX = int.MinValue, lastY = int.MinValue;
        var posTimer = Stopwatch.StartNew();
        bool secure = false;
        var arrow = LoadCursor(IntPtr.Zero, 32512);

        void SendShape(CursorNative.Shape shape)
        {
            if (sent.Add(shape.Hash))
            {
                var p = new byte[12 + shape.Png.Length];
                BinaryPrimitives.WriteUInt64LittleEndian(p, shape.Hash);
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(8), (short)shape.HotX);
                BinaryPrimitives.WriteInt16LittleEndian(p.AsSpan(10), (short)shape.HotY);
                shape.Png.CopyTo(p, 12);
                Send(s, MsgType.CursorShape, p);
            }
            else
            {
                var p = new byte[8];
                BinaryPrimitives.WriteUInt64LittleEndian(p, shape.Hash);
                Send(s, MsgType.CursorRef, p);
            }
        }

        try
        {
            while (_run && s.IsOpen && _target == s)
            {
                var st = CursorNative.Query();

                // UAC 确认框等“安全桌面”：读不到光标。不能当成“隐藏”或“在 (0,0)”——
                // 以前会让控制端光标消失、并被位置矫正拉到画面左上角，导致点不到确认框。
                // 这时控制端显示普通箭头、停止位置矫正，输入照常经 Sunshine（系统服务）送达。
                if (!st.Ok)
                {
                    if (!secure)
                    {
                        secure = true;
                        Log.Info($"光标同步：进入安全桌面（UAC 等），控制端改用普通箭头（安全桌面={CursorNative.OnSecureDesktop()}）");
                        if (byHandle.TryGetValue(arrow, out var a) && a != null) SendShape(a);
                    }
                    Thread.Sleep(30);
                    continue;
                }
                if (secure)
                {
                    secure = false;
                    lastHandle = new(-1); // 回到普通桌面，重新同步形状
                    Log.Info("光标同步：回到普通桌面");
                }

                if (st.Visible != lastVisible || st.Handle != lastHandle)
                {
                    lastVisible = st.Visible;
                    lastHandle = st.Handle;
                    if (!st.Visible) Send(s, MsgType.CursorHidden, Array.Empty<byte>());
                    else
                    {
                        if (!byHandle.TryGetValue(st.Handle, out var shape)) byHandle[st.Handle] = shape = CursorNative.Capture(st.Handle);
                        if (shape != null) SendShape(shape);
                    }
                }

                // 位置：最多 60 次/秒，只在变化时发（按正在串流的那块屏幕换算）
                if ((st.X != lastX || st.Y != lastY) && posTimer.ElapsedMilliseconds >= 16)
                {
                    lastX = st.X; lastY = st.Y;
                    posTimer.Restart();
                    var b = HostDisplays.StreamedBounds();
                    var p = new byte[8];
                    BinaryPrimitives.WriteSingleLittleEndian(p, (float)(st.X - b.Left) / b.Width);
                    BinaryPrimitives.WriteSingleLittleEndian(p.AsSpan(4), (float)(st.Y - b.Top) / b.Height);
                    Send(s, MsgType.CursorPos, p);
                }
                Thread.Sleep(6);
            }
        }
        catch (Exception e) { Log.Error("光标采集停止", e); }
    }

    static void Send(PeerSession s, MsgType t, byte[] p)
    {
        try { s.SendAsync(t, p).GetAwaiter().GetResult(); } catch { }
    }
}

/// <summary>
/// 控制端：本地显示光标（零延迟），形状用被控端发来的；
/// 只在 Moonlight 画面窗口在前台时替换系统箭头，离开就恢复。
/// </summary>
public sealed class CursorClient : IDisposable
{
    const uint OCR_NORMAL = 32512;
    const uint SPI_SETCURSORS = 0x0057;

    [DllImport("user32.dll")] static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr pv, uint winIni);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorNative.POINT p);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref CursorNative.POINT p);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    /// <summary>恢复用户自己的光标方案（启动时、退出时、离开远程窗口时都会调）</summary>
    public static void RestoreSystemCursors() => SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);

    readonly Dictionary<ulong, IntPtr> _cache = new();
    readonly IntPtr _blank = CursorNative.CreateBlankCursor();
    readonly DispatcherTimer _timer;
    Process _proc;
    StreamSettings _stream;
    IntPtr _current = IntPtr.Zero;
    bool _dirty, _applied, _lastFg;

    float _hostX = -1, _hostY = -1;
    DateTime _hostPosAt;
    CursorNative.POINT _lastLocal;
    DateTime _lastLocalMove = DateTime.UtcNow;
    bool _selfMoved;

    public CursorClient(Process moonlight, StreamSettings stream, Dispatcher ui)
    {
        _proc = moonlight;
        _stream = stream;
        _timer = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(15) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public void Attach(Process moonlight, StreamSettings stream)
    {
        _proc = moonlight;
        _stream = stream;
        _dirty = true;
    }

    /// <summary>强制下一次重新替换系统箭头（拖放等操作之后）</summary>
    public void Reapply()
    {
        _dirty = true;
        _applied = false;
        Log.Info($"本地光标：重新套用（当前形状 {(_current == _blank ? "隐藏" : _current == IntPtr.Zero ? "无" : "正常")}）");
    }

    // ---------- 收到被控端消息（UI 线程） ----------
    public void OnShape(byte[] p)
    {
        if (p.Length < 13) return;
        var hash = BinaryPrimitives.ReadUInt64LittleEndian(p);
        int hx = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(8)), hy = BinaryPrimitives.ReadInt16LittleEndian(p.AsSpan(10));
        if (!_cache.ContainsKey(hash))
        {
            try { _cache[hash] = CursorNative.CreateCursor(p[12..], hx, hy); }
            catch (Exception e) { Log.Error("生成光标失败", e); return; }
        }
        SetCurrent(_cache[hash]);
    }

    public void OnRef(byte[] p)
    {
        if (p.Length >= 8 && _cache.TryGetValue(BinaryPrimitives.ReadUInt64LittleEndian(p), out var h)) SetCurrent(h);
    }

    public void OnHidden()
    {
        if (_current != _blank) Log.Info("本地光标：被控端光标隐藏");
        SetCurrent(_blank);
    }

    public void OnPos(byte[] p)
    {
        if (p.Length < 8) return;
        _hostX = BinaryPrimitives.ReadSingleLittleEndian(p);
        _hostY = BinaryPrimitives.ReadSingleLittleEndian(p.AsSpan(4));
        _hostPosAt = DateTime.UtcNow;
    }

    void SetCurrent(IntPtr h)
    {
        if (h == _current) return;
        _current = h;
        _dirty = true;
    }

    // ---------- 每 15ms ----------
    void Tick()
    {
        bool fg = false;
        IntPtr win = IntPtr.Zero;
        try
        {
            if (!_proc.HasExited)
            {
                GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
                if (pid == (uint)_proc.Id)
                {
                    win = FreeRemote.Overlay.FindStreamWindow(pid);
                    fg = win != IntPtr.Zero;
                }
            }
        }
        catch { }

        if (fg != _lastFg) { _lastFg = fg; Log.Info($"本地光标：远程画面{(fg ? "在前台，接管箭头" : "不在前台，恢复系统光标")}"); }
        if (!fg)
        {
            if (_applied) { RestoreSystemCursors(); _applied = false; }
            return;
        }

        // 替换系统箭头（SetSystemCursor 会销毁传入的句柄，所以每次传一份拷贝）
        if (_current != IntPtr.Zero && (_dirty || !_applied))
        {
            var copy = CursorNative.Copy(_current);
            bool ok = copy != IntPtr.Zero && SetSystemCursor(copy, OCR_NORMAL);
            if (ok) _applied = true;
            else Log.Error("本地光标：替换系统箭头失败");
            _dirty = false;
        }

        Correct(win);
    }

    /// <summary>
    /// 反查矫正：本地鼠标停下 200ms 后，如果被控端光标位置和本地对不上（被控端程序自己移动了光标），
    /// 把本地光标拉到对应位置。移动过程中不干预，避免和本地操作“打架”。
    /// </summary>
    void Correct(IntPtr win)
    {
        GetCursorPos(out var local);
        var now = DateTime.UtcNow;
        if (local.X != _lastLocal.X || local.Y != _lastLocal.Y)
        {
            if (!_selfMoved) _lastLocalMove = now;
            _selfMoved = false;
            _lastLocal = local;
        }
        if (_hostX < 0 || (now - _hostPosAt).TotalSeconds > 1 || (now - _lastLocalMove).TotalMilliseconds < 200) return;
        if (!VideoRect(win, out var vx, out var vy, out var vw, out var vh)) return;
        if (local.X < vx || local.Y < vy || local.X >= vx + vw || local.Y >= vy + vh) return; // 光标不在画面里
        int tx = vx + (int)Math.Round(Math.Clamp(_hostX, 0, 1) * vw);
        int ty = vy + (int)Math.Round(Math.Clamp(_hostY, 0, 1) * vh);
        if (Math.Abs(tx - local.X) + Math.Abs(ty - local.Y) <= 6) return;
        SetCursorPos(tx, ty);
        _selfMoved = true;
    }

    /// <summary>画面在窗口里的实际区域（按串流宽高比居中，和 Moonlight 的黑边算法一致）</summary>
    bool VideoRect(IntPtr win, out int x, out int y, out int w, out int h)
    {
        x = y = w = h = 0;
        if (!GetClientRect(win, out var cr)) return false;
        var o = new CursorNative.POINT();
        ClientToScreen(win, ref o);
        int cw = cr.R - cr.L, ch = cr.B - cr.T;
        var parts = _stream.Resolution.Split('x');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var sw) || !int.TryParse(parts[1], out var sh) || cw <= 0 || ch <= 0) return false;
        double scale = Math.Min((double)cw / sw, (double)ch / sh);
        w = (int)(sw * scale); h = (int)(sh * scale);
        x = o.X + (cw - w) / 2; y = o.Y + (ch - h) / 2;
        return true;
    }

    public void Dispose()
    {
        _timer.Stop();
        if (_applied) RestoreSystemCursors();
        _applied = false;
        foreach (var h in _cache.Values) CursorNative.Destroy(h);
        _cache.Clear();
        CursorNative.Destroy(_blank);
    }
}
