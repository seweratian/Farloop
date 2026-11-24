using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Media;

namespace FreeRemote.Core;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public static class Palette
{
    static SolidColorBrush B(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    public static readonly SolidColorBrush Green = B(0x34, 0xC7, 0x59);
    public static readonly SolidColorBrush Orange = B(0xFF, 0x95, 0x00);
    public static readonly SolidColorBrush Gray = B(0xAE, 0xAE, 0xB2);
    public static readonly SolidColorBrush Blue = B(0x00, 0x7A, 0xFF);
    public static readonly SolidColorBrush Red = B(0xFF, 0x3B, 0x30);
}

public sealed class DeviceVM : Observable
{
    public DeviceVM(string ip) { Ip = ip; }

    public string Ip { get; }

    string _name = "";
    public string Name { get => _name; set => Set(ref _name, value); }

    bool _online;
    public bool Online { get => _online; set { if (Set(ref _online, value)) RaiseStatus(); } }

    bool _p2p;
    public bool IsP2P { get => _p2p; set { if (Set(ref _p2p, value)) RaiseStatus(); } }

    double? _latency;
    public double? Latency { get => _latency; set { if (Set(ref _latency, value)) { Raise(nameof(Detail)); Raise(nameof(BigMetric)); } } }

    string _nat = "";
    public string NatType { get => _nat; set => Set(ref _nat, value); }

    string _tunnel = "";
    public string Tunnel { get => _tunnel; set => Set(ref _tunnel, value); }

    string _loss = "";
    public string LossRate { get => _loss; set => Set(ref _loss, value); }

    /// <summary>被控端的显示器（对方告知；多于一块时控制中心里可以切换）</summary>
    public HostDisplay[] Displays { get; set; } = Array.Empty<HostDisplay>();
    /// <summary>对方 Sunshine 当前的 output_name（空 = 主屏）</summary>
    public string CurrentDisplay { get; set; } = "";

    bool _privacy;
    public bool PrivacyOn { get => _privacy; set => Set(ref _privacy, value); }

    bool _canWake;
    public bool CanWake { get => _canWake; set { if (Set(ref _canWake, value)) Raise(nameof(ShowWake)); } }
    /// <summary>FreeRemote 安卓端：只能控制别人——不显示连接、远程开机、发送文件</summary>
    bool _mobile;
    public bool IsMobile
    {
        get => _mobile;
        set { if (Set(ref _mobile, value)) { Raise(nameof(ShowWake)); Raise(nameof(ShowConnect)); Raise(nameof(ShowSendFiles)); Raise(nameof(IconGlyph)); Raise(nameof(StatusText)); } }
    }
    public string IconGlyph => _mobile ? "" : ""; // 手机 / 显示器
    /// <summary>离线时显示“远程开机”按钮</summary>
    public bool ShowWake => !Online && !IsConnected && !_mobile;
    /// <summary>离线的设备可以从列表里移除</summary>
    public bool ShowRemove => !Online && !IsConnected;
    public bool ShowConnect => Online && !IsConnected && !_mobile;
    public bool ShowSendFiles => Online && !_mobile;

    bool _connected;
    public bool IsConnected { get => _connected; set { if (Set(ref _connected, value)) { Raise(nameof(IsNotConnected)); Raise(nameof(ShowWake)); Raise(nameof(ShowConnect)); } } }
    public bool IsNotConnected => !_connected;

    string _busy = "";
    public string Busy { get => _busy; set { if (Set(ref _busy, value)) Raise(nameof(IsBusy)); } }
    public bool IsBusy => _busy != "";

    // ---- 卡片配色（参考图：每张卡片一种柔和的渐变 + 同色系图标徽章） ----
    static readonly (Color Tint, Color Badge, Color Icon)[] Hues =
    {
        (Color.FromRgb(0xFD, 0xE7, 0xC4), Color.FromRgb(0xFD, 0xEE, 0xCF), Color.FromRgb(0xE0, 0x9B, 0x2D)), // 琥珀
        (Color.FromRgb(0xF8, 0xD5, 0xEA), Color.FromRgb(0xFB, 0xE2, 0xF1), Color.FromRgb(0xD1, 0x4F, 0x9E)), // 粉
        (Color.FromRgb(0xCF, 0xF0, 0xDE), Color.FromRgb(0xDC, 0xF5, 0xE7), Color.FromRgb(0x2E, 0xA0, 0x68)), // 薄荷
        (Color.FromRgb(0xFB, 0xD3, 0xD6), Color.FromRgb(0xFC, 0xE1, 0xE3), Color.FromRgb(0xE0, 0x51, 0x66)), // 玫瑰
        (Color.FromRgb(0xE2, 0xDA, 0xFB), Color.FromRgb(0xEB, 0xE5, 0xFC), Color.FromRgb(0x84, 0x66, 0xEE)), // 薰衣草
        (Color.FromRgb(0xD3, 0xE5, 0xFB), Color.FromRgb(0xE0, 0xEC, 0xFC), Color.FromRgb(0x3D, 0x7B, 0xF7)), // 天蓝
    };

    int _hue = -1;
    /// <summary>卡片色号：按 IP 固定（同一台设备每次颜色一样）</summary>
    public int Hue
    {
        get
        {
            if (_hue >= 0) return _hue;
            int h = 0;
            foreach (var c in Ip) h = h * 31 + c; // string.GetHashCode 每次运行都不同，自己算一个稳定的
            return _hue = (int)((uint)h % (uint)Hues.Length);
        }
    }

    /// <summary>深色模式：卡片底色设成深灰，界面切换外观时调用</summary>
    public static bool DarkCards { get; set; }
    public void RefreshTheme() => Raise(nameof(CardBrush));

    public Brush CardBrush
    {
        get
        {
            var t = Hues[Hue].Tint;
            var b = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 1) };
            if (DarkCards)
            {
                var ic = Hues[Hue].Icon;
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x2C, 0x2C, 0x30), 0));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, 0x2C, 0x2C, 0x30), 0.45));
                b.GradientStops.Add(new GradientStop(Color.FromArgb(0xF0, (byte)(0x2C + ic.R / 6), (byte)(0x2C + ic.G / 6), (byte)(0x30 + ic.B / 6)), 1));
                b.Freeze();
                return b;
            }
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF), 0.45));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(0xE6, t.R, t.G, t.B), 1));
            b.Freeze();
            return b;
        }
    }
    public Brush BadgeBrush { get { var b = new SolidColorBrush(Hues[Hue].Badge); b.Freeze(); return b; } }
    public Brush IconBrush { get { var b = new SolidColorBrush(Hues[Hue].Icon); b.Freeze(); return b; } }
    public string BigMetric => !Online ? "—" : Latency is double l ? $"{l:0} ms" : "—";

    public string StatusText => (_mobile ? "手机 · " : "") + (!Online ? "离线" : IsP2P ? "P2P 直连" : "中转");
    public Brush StatusBrush => !Online ? Palette.Gray : IsP2P ? Palette.Green : Palette.Orange;
    public string Detail => Latency is double l && Online ? $"{Ip}  ·  {l:0} ms" : Ip;

    void RaiseStatus() { Raise(nameof(StatusText)); Raise(nameof(StatusBrush)); Raise(nameof(Detail)); Raise(nameof(ShowWake)); Raise(nameof(ShowRemove)); Raise(nameof(ShowConnect)); Raise(nameof(BigMetric)); }
}

public sealed class TransferVM : Observable
{
    readonly Stopwatch _sw = Stopwatch.StartNew();
    long _done, _lastDone;
    double _lastTime;

    public TransferVM(string title, string subtitle, long total, bool outgoing, string peerIp = "", bool clipboard = false)
    {
        _title = title;
        Subtitle = subtitle;
        Total = total;
        Outgoing = outgoing;
        PeerIp = peerIp;
        IsClipboard = clipboard;
    }

    public string PeerIp { get; }
    public bool IsClipboard { get; }

    // ---- 浮动进度卡片用 ----
    string _doneText = "", _speedText = "", _etaText = "准备中…";
    public string DoneText { get => _doneText; private set => Set(ref _doneText, value); }
    public string SpeedText { get => _speedText; private set => Set(ref _speedText, value); }
    public string EtaText { get => _etaText; private set => Set(ref _etaText, value); }
    public string Direction => Outgoing ? "发送到对方" : "从对方复制";
    double _smoothSpeed;

    string _title;
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get; }
    public long Total { get; }
    public bool Outgoing { get; }
    public string Glyph => Outgoing ? "" : "";

    public void Add(long n) => Interlocked.Add(ref _done, n);

    double _progress;
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    string _state = "等待中";
    public string State { get => _state; private set => Set(ref _state, value); }

    bool _finished;
    public bool Finished { get => _finished; private set => Set(ref _finished, value); }

    bool _failed;
    public bool Failed { get => _failed; private set => Set(ref _failed, value); }

    public string? OpenPath { get; set; }

    /// <summary>界面定时调用，刷新进度和速度</summary>
    public void Refresh()
    {
        if (Finished) return;
        var done = Interlocked.Read(ref _done);
        var now = _sw.Elapsed.TotalSeconds;
        Progress = Total > 0 ? Math.Min(100, done * 100.0 / Total) : 0;
        DoneText = $"{Fmt(done)} / {Fmt(Total)}"; // 和进度条同步刷新
        var dt = now - _lastTime;
        if (dt >= 0.5)
        {
            var speed = (done - _lastDone) / dt;
            _lastDone = done;
            _lastTime = now;
            State = $"{Fmt(done)} / {Fmt(Total)}  ·  {Fmt((long)speed)}/s";
            // 剩余时间用平滑后的速度，避免数字乱跳
            _smoothSpeed = _smoothSpeed <= 0 ? speed : _smoothSpeed * 0.7 + speed * 0.3;
            SpeedText = $"{Fmt((long)_smoothSpeed)}/s";
            var left = Total - done;
            EtaText = done == 0 ? "准备中…" : _smoothSpeed < 1 ? $"剩余 {Fmt(left)}" : $"剩余 {Fmt(left)} · 约 {Eta(left / _smoothSpeed)}";
        }
    }

    static string Eta(double s) =>
        s < 1 ? "不到 1 秒" : s < 60 ? $"{s:0} 秒" : s < 3600 ? $"{(int)(s / 60)} 分 {(int)(s % 60)} 秒" : $"{(int)(s / 3600)} 小时 {(int)(s % 3600 / 60)} 分";

    public void Complete()
    {
        Progress = 100;
        var secs = Math.Max(_sw.Elapsed.TotalSeconds, 0.001);
        State = $"已完成  ·  {Fmt(Total)}  ·  平均 {Fmt((long)(Total / secs))}/s";
        DoneText = $"{Fmt(Total)} / {Fmt(Total)}";
        SpeedText = $"平均 {Fmt((long)(Total / secs))}/s";
        EtaText = "复制完成";
        Finished = true;
    }

    public void Fail(string reason)
    {
        State = "失败：" + reason;
        EtaText = "失败：" + reason;
        SpeedText = "";
        Failed = true;
        Finished = true;
    }

    public static string Fmt(long b) =>
        b >= 1L << 30 ? $"{b / (double)(1L << 30):0.00} GB" :
        b >= 1L << 20 ? $"{b / (double)(1L << 20):0.0} MB" :
        b >= 1L << 10 ? $"{b / 1024.0:0} KB" : $"{b} B";
}
