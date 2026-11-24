using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 远程时贴在 Moonlight 画面左上角的半透明信息卡片（类似 UU 远程）。
/// 位置由 StreamOverlay 统一控制；这里只负责每秒刷新数据。鼠标穿透、不抢焦点。
/// </summary>
public partial class StreamInfoCard : Window
{
    readonly DeviceVM _dev;
    readonly string _myIp;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Ping _ping = new();
    NetworkInterface? _nic;
    long _lastRx = -1;
    DateTime _lastAt;
    bool _busy;

    public StreamInfoCard(DeviceVM dev, StreamSettings s, string myIp)
    {
        InitializeComponent();
        _dev = dev;
        _myIp = myIp;
        UpdateVideo(s);
        Opacity = 0;
        SourceInitialized += (_, _) => Overlay.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: true);
        _timer.Tick += async (_, _) => await TickAsync();
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => { _timer.Stop(); _ping.Dispose(); };
    }

    public void UpdateVideo(StreamSettings s)
    {
        VideoText.Text = $"{s.ResolutionLabel} · {s.Fps} FPS{(s.Hdr ? " · HDR" : "")}";
        CodecText.Text = s.CodecLabel + (s.Yuv444 ? " · 4:4:4" : "");
    }

    public void Place(double left, double top, bool visible)
    {
        if (!visible) { Opacity = 0; return; }
        Left = left;
        Top = top;
        Opacity = 1;
    }

    async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            ModeText.Text = !_dev.Online ? "连接中断" : _dev.IsP2P ? $"P2P 直连{Proto()}" : $"中转{Proto()}";
            Dot.Fill = !_dev.Online ? Palette.Red : _dev.IsP2P ? Palette.Green : Palette.Orange;
            LossText.Text = string.IsNullOrEmpty(_dev.LossRate) || _dev.LossRate == "-" ? "0%" : _dev.LossRate;

            // 延迟：经虚拟网络 ping 被控端，即实际往返时间
            try
            {
                var r = await _ping.SendPingAsync(_dev.Ip, 1000);
                LatText.Text = r.Status == IPStatus.Success ? $"{r.RoundtripTime} ms" : "超时";
            }
            catch { LatText.Text = "—"; }

            // 码率：虚拟网卡的实时接收速率（画面 + 声音）
            _nic ??= NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == _myIp));
            if (_nic != null)
            {
                long rx;
                try { rx = _nic.GetIPStatistics().BytesReceived; }
                catch { _nic = null; _lastRx = -1; return; } // 组网重启后网卡重建了，下次重新找
                var now = DateTime.UtcNow;
                if (_lastRx >= 0)
                {
                    var secs = (now - _lastAt).TotalSeconds;
                    if (secs > 0.2) RateText.Text = $"{(rx - _lastRx) * 8 / secs / 1e6:0.0} Mbps";
                }
                _lastRx = rx;
                _lastAt = now;
            }
        }
        catch (Exception e) { Log.Error("信息卡片刷新失败", e); }
        finally { _busy = false; }
    }

    string Proto() => string.IsNullOrEmpty(_dev.Tunnel) || _dev.Tunnel == "-" ? "" : " · " + _dev.Tunnel.ToUpperInvariant();
}
