using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 远程窗口标题栏上的“控制中心”。即时生效：关闭被控端屏幕、信息卡片、详细统计、全屏切换；
/// 分辨率/帧率/码率/编码/垂直同步等需要快速重连，改完出现“立即应用”。
/// </summary>
public partial class ControlCenter : Window
{
    public const double PillHeight = 24;
    const double RightMargin = 6; // 与 XAML 里 StackPanel 的右边距一致

    readonly Overlay _overlay;
    readonly DeviceVM _dev;
    StreamSettings _active;
    double _anchorRight, _top;
    bool _visible;
    readonly DispatcherTimer _autoCollapse = new() { Interval = TimeSpan.FromMilliseconds(1500) };

    StreamSettings Cfg => Hub.I.Config.Stream;

    public ControlCenter(Overlay overlay, DeviceVM dev, StreamSettings active)
    {
        InitializeComponent();
        _overlay = overlay;
        _dev = dev;
        _active = active;
        Opacity = 0;
        TitleText.Text = dev.Name;
        SourceInitialized += (_, _) => Overlay.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: false);
        SizeChanged += (_, _) => Reposition();
        _autoCollapse.Tick += (_, _) => { _autoCollapse.Stop(); Collapse(); };
        dev.PropertyChanged += Dev_PropertyChanged;
        Closed += (_, _) => dev.PropertyChanged -= Dev_PropertyChanged;
        UpdateStatus();
        LoadControls();
    }

    void Dev_PropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DeviceVM.IsP2P) or nameof(DeviceVM.Online) or nameof(DeviceVM.Latency) or nameof(DeviceVM.PrivacyOn))
            Dispatcher.BeginInvoke(UpdateStatus);
    }

    void UpdateStatus()
    {
        var brush = !_dev.Online ? Palette.Red : _dev.IsP2P ? Palette.Green : Palette.Orange;
        PillDot.Fill = brush;
        StatusText.Foreground = brush;
        StatusText.Text = (!_dev.Online ? "连接中断" : _dev.IsP2P ? "P2P 直连" : "中转") +
                          (_dev.Latency is double l && _dev.Online ? $" · {l:0} ms" : "");
        SwPrivacy.IsChecked = _dev.PrivacyOn;
    }

    // ---------------- 位置（由 Overlay 调用） ----------------
    public void Place(double anchorRight, double top, bool visible)
    {
        _visible = visible;
        if (!visible) { Opacity = 0; Collapse(); return; }
        _anchorRight = anchorRight;
        _top = top;
        Reposition();
        Opacity = 1;
    }

    void Reposition()
    {
        if (!_visible) return;
        Left = _anchorRight - ActualWidth + RightMargin;
        Top = _top;
    }

    // ---------------- 展开 / 收起 ----------------
    void Pill_Click(object s, MouseButtonEventArgs e)
    {
        if (Panel.Visibility == Visibility.Visible) Collapse();
        else { LoadControls(); Panel.Visibility = Visibility.Visible; }
    }

    void Collapse() => Panel.Visibility = Visibility.Collapsed;
    void Panel_MouseEnter(object s, MouseEventArgs e) => _autoCollapse.Stop();
    void Panel_MouseLeave(object s, MouseEventArgs e) => _autoCollapse.Start();

    public void SetActive(StreamSettings active)
    {
        _active = active;
        UpdatePending();
    }

    // ---------------- 控件 ----------------
    void LoadControls()
    {
        var st = Cfg;
        Seg(SegRes, new[] { ("720p", "1280x720"), ("1080p", "1920x1080"), ("2K", "2560x1440"), ("4K", "3840x2160") }, st.Resolution, v => st.Resolution = v);
        Seg(SegFps, new[] { "30", "60", "90", "120", "144" }.Select(f => (f, f)).ToArray(), st.Fps.ToString(), v => st.Fps = int.Parse(v));
        Seg(SegBitrate, new[] { ("自动", "0"), ("10M", "10"), ("20M", "20"), ("40M", "40"), ("80M", "80") }, st.BitrateMbps.ToString(), v => st.BitrateMbps = int.Parse(v));
        Seg(SegCodec, new[] { ("自动", "auto"), ("H.264", "H.264"), ("HEVC", "HEVC"), ("AV1", "AV1") }, st.Codec, v => st.Codec = v);

        // 被控端屏幕（多于一块时）：点了立即切换
        var ds = _dev.Displays;
        DisplayRow.Visibility = ds.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (ds.Length > 1)
        {
            var cur = ds.FirstOrDefault(x => _dev.CurrentDisplay != "" && (x.Id == _dev.CurrentDisplay || x.DeviceName == _dev.CurrentDisplay))
                      ?? ds.FirstOrDefault(x => x.Primary) ?? ds[0];
            var opts = ds.Select((x, i) => ($"{i + 1} {(x.Primary ? "主屏" : "副屏")}", x.Id)).ToArray();
            Seg(SegDisplay, opts, cur.Id, id =>
            {
                var target = ds.First(x => x.Id == id);
                if (target == cur) return;
                Collapse();
                _ = Hub.I.SwitchDisplayAsync(_dev, target);
            }, save: false);
            for (int i = 0; i < ds.Length; i++)
                ((FrameworkElement)SegDisplay.Children[i]).ToolTip = $"{ds[i].Name}  {ds[i].Width}×{ds[i].Height}";
        }

        // 连接方式：仅中转 = 重启本机组网（约 5–10 秒），画面自动重连
        Seg(SegRoute, new[] { ("自动（P2P 优先）", "p2p"), ("仅中转", "relay") }, Hub.I.Config.ForceRelay ? "relay" : "p2p", v =>
        {
            Collapse();
            _ = Hub.I.SetForceRelayAsync(_dev, v == "relay");
        }, save: false);
        SwCard.IsChecked = st.InfoCard;
        SwLocalCursor.IsChecked = st.LocalCursor;
        SwLocalCursor.IsEnabled = _active.DesktopMouse; // 游戏模式（相对鼠标）下没有本地光标
        SwPerf.IsChecked = st.PerfOverlay;
        SwVsync.IsChecked = st.VSync;
        SwPacing.IsChecked = st.FramePacing;
        SwHdr.IsChecked = st.Hdr;
        SwPrivacy.IsChecked = _dev.PrivacyOn;
        UpdatePending();
    }

    void Seg(Panel host, (string Label, string Value)[] options, string current, Action<string> apply, bool save = true)
    {
        host.Children.Clear();
        var style = (Style)FindResource("SegDark");
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = "cc_" + host.Name, Style = style, IsChecked = value == current };
            rb.Checked += (_, _) => { apply(value); if (save) { Hub.I.Config.Save(); UpdatePending(); } };
            host.Children.Add(rb);
        }
    }

    void UpdatePending() =>
        ApplyBar.Visibility = Cfg.NeedsReconnect(_active) ? Visibility.Visible : Visibility.Collapsed;

    void Toggles_Click(object s, RoutedEventArgs e)
    {
        Cfg.VSync = SwVsync.IsChecked == true;
        Cfg.FramePacing = SwPacing.IsChecked == true;
        Cfg.Hdr = SwHdr.IsChecked == true;
        Hub.I.Config.Save();
        UpdatePending();
    }

    // 即时生效的几项
    async void Privacy_Click(object s, RoutedEventArgs e) => await Hub.I.SetPrivacyAsync(_dev, SwPrivacy.IsChecked == true);

    async void LocalCursor_Click(object s, RoutedEventArgs e) =>
        await Hub.I.SetLocalCursorAsync(_dev, SwLocalCursor.IsChecked == true);

    void Card_Click(object s, RoutedEventArgs e)
    {
        Cfg.InfoCard = SwCard.IsChecked == true;
        Hub.I.Config.Save();
        _overlay.SetCardVisible(Cfg.InfoCard, Cfg);
    }

    async void Perf_Click(object s, RoutedEventArgs e)
    {
        Cfg.PerfOverlay = SwPerf.IsChecked == true;
        Hub.I.Config.Save();
        await Overlay.SendMoonlightHotkeyAsync('S'); // Moonlight：Ctrl+Alt+Shift+S 切换统计
    }

    async void Fullscreen_Click(object s, RoutedEventArgs e)
    {
        Collapse();
        await Overlay.SendMoonlightHotkeyAsync('X'); // Moonlight：Ctrl+Alt+Shift+X 全屏/窗口
    }

    async void Apply_Click(object s, RoutedEventArgs e)
    {
        Collapse();
        await Hub.I.RestartStreamAsync(_dev);
    }

    void Disconnect_Click(object s, RoutedEventArgs e) => Hub.I.Disconnect(_dev);
}
