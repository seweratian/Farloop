using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace FreeRemote.Core;

/// <summary>
/// 剪贴板双向同步（文字、图片、文件）。只在有远程会话时工作，和 UU 一样。
/// 全部在 UI 线程上执行（Windows 剪贴板要求 STA 线程）。
/// </summary>
public sealed class ClipboardSync
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int MaxTextBytes = 16 * 1024 * 1024;
    public const long MaxClipboardFileBytes = 2L * 1024 * 1024 * 1024;

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool AddClipboardFormatListener(IntPtr hwnd);

    public interface IHost
    {
        bool ClipboardEnabled { get; }
        bool FileClipboardEnabled { get; }
        bool HasSessions { get; }
        void BroadcastClipboard(byte[] payload);
        void SendClipboardFiles(string[] paths);
    }

    readonly IHost _host;
    byte[] _lastHash = Array.Empty<byte>();
    DateTime _suppressUntil = DateTime.MinValue;

    public ClipboardSync(IHost host) { _host = host; }

    public void Attach(Window w)
    {
        var hwnd = new WindowInteropHelper(w).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        if (!AddClipboardFormatListener(hwnd)) Log.Error("注册剪贴板监听失败");

        // 剪贴板被别的程序长时间占着不放时提醒（实测：UU 远程的 GameViewer 会一直占着，导致两端都复制粘贴失灵）
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => CheckBlocked();
        t.Start();
    }

    [DllImport("user32.dll")] static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    public event Action<string>? Blocked;
    uint _blockPid;
    int _blockCount;
    readonly System.Collections.Generic.HashSet<uint> _warned = new();

    void CheckBlocked()
    {
        var h = GetOpenClipboardWindow();
        uint pid = 0;
        if (h != IntPtr.Zero) GetWindowThreadProcessId(h, out pid);
        if (pid == 0 || pid == (uint)Environment.ProcessId) { _blockPid = 0; _blockCount = 0; return; }
        _blockCount = pid == _blockPid ? _blockCount + 1 : 1;
        _blockPid = pid;
        if (_blockCount < 3 || !_warned.Add(pid)) return; // 连续约 6 秒都被同一个程序占着，每个程序只提醒一次
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { name = $"进程 {pid}"; }
        var hint = name.Contains("GameViewer", StringComparison.OrdinalIgnoreCase) ? "（这是 UU 远程，请退出它）" : "";
        Log.Error($"剪贴板被 {name}（pid {pid}）长时间占用");
        Blocked?.Invoke($"剪贴板被 {name}{hint} 一直占着，复制粘贴会失灵。退出该程序后即可恢复。");
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) _ = OnChangedAsync();
        return IntPtr.Zero;
    }

    static byte[] Hash(byte[] b) => SHA256.HashData(b);
    static byte[] HashFiles(string[] paths) => SHA256.HashData(Encoding.UTF8.GetBytes("F|" + string.Join("|", paths)));

    async Task OnChangedAsync()
    {
        if (!_host.ClipboardEnabled || !_host.HasSessions) return;
        if (DateTime.UtcNow < _suppressUntil) return;   // 是我们自己刚写进去的，不回传
        await Task.Delay(100);                          // 等源程序写完所有格式
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                if (!_host.FileClipboardEnabled) return;
                var paths = Retry(() => Clipboard.GetFileDropList()).Cast<string>().ToArray();
                var h = HashFiles(paths);
                if (h.SequenceEqual(_lastHash)) return;
                _lastHash = h;
                _host.SendClipboardFiles(paths);
                return;
            }

            byte[] payload;
            if (Clipboard.ContainsText())
            {
                var text = Retry(() => Clipboard.GetText());
                var bytes = Encoding.UTF8.GetBytes(text);
                if (bytes.Length > MaxTextBytes) return;
                payload = new byte[bytes.Length + 1];
                payload[0] = 1;
                bytes.CopyTo(payload, 1);
            }
            else if (Clipboard.ContainsImage())
            {
                var img = Retry(() => Clipboard.GetImage());
                if (img == null) return;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(img));
                using var ms = new MemoryStream();
                ms.WriteByte(2);
                enc.Save(ms);
                payload = ms.ToArray();
            }
            else return;

            var hash = Hash(payload);
            if (hash.SequenceEqual(_lastHash)) return;
            _lastHash = hash;
            _host.BroadcastClipboard(payload);
        }
        catch (Exception e) { Log.Error("读取剪贴板失败", e); }
    }

    /// <summary>收到对方的文字/图片</summary>
    public void ApplyRemote(byte[] payload)
    {
        if (!_host.ClipboardEnabled || payload.Length < 1) return;
        try
        {
            _lastHash = Hash(payload);
            _suppressUntil = DateTime.UtcNow.AddMilliseconds(800);
            if (payload[0] == 1)
            {
                var text = Encoding.UTF8.GetString(payload, 1, payload.Length - 1);
                Retry(() => { Clipboard.SetDataObject(text, true); return true; });
            }
            else if (payload[0] == 2)
            {
                using var ms = new MemoryStream(payload, 1, payload.Length - 1);
                var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                Retry(() => { Clipboard.SetImage(frame); return true; });
            }
        }
        catch (Exception e) { Log.Error("写入剪贴板失败", e); }
    }

    /// <summary>收到对方复制的文件后，放进本机剪贴板，Ctrl+V 即可粘贴</summary>
    public void ApplyRemoteFiles(string[] paths)
    {
        try
        {
            _lastHash = HashFiles(paths);
            _suppressUntil = DateTime.UtcNow.AddMilliseconds(800);
            var sc = new StringCollection();
            sc.AddRange(paths);
            Retry(() => { Clipboard.SetFileDropList(sc); return true; });
        }
        catch (Exception e) { Log.Error("写入剪贴板文件失败", e); }
    }

    // 剪贴板经常被别的程序短暂占用，重试几次
    static T Retry<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (COMException) when (i < 6) { Thread.Sleep(40); }
            catch (ExternalException) when (i < 6) { Thread.Sleep(40); }
        }
    }
}
