using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

public sealed class SetupStep : Observable
{
    public SetupStep(string title) { Title = title; }
    public string Title { get; }

    string _detail = "";
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    string _glyph = "";
    public string Glyph { get => _glyph; private set => Set(ref _glyph, value); }

    Brush _brush = Palette.Gray;
    public Brush GlyphBrush { get => _brush; private set => Set(ref _brush, value); }

    public void Running() { Glyph = ""; GlyphBrush = Palette.Blue; }
    public void Ok(string d = "") { Glyph = ""; GlyphBrush = Palette.Green; if (d != "") Detail = d; }
    public void Warn(string d) { Glyph = ""; GlyphBrush = Palette.Orange; Detail = d; }
    public void Fail(string d) { Glyph = ""; GlyphBrush = Palette.Red; Detail = d; }
}

public partial class MainWindow : Window
{
    readonly Hub _hub = Hub.I;
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    string? _toastPath;
    bool _hintShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _hub;

        _toastTimer.Tick += (_, _) => { ToastBar.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _hub.Toast += ShowToast;
        _hub.PropertyChanged += Hub_PropertyChanged;
        _hub.Devices.CollectionChanged += (_, _) => UpdateEmpty();
        _hub.Transfers.CollectionChanged += (_, _) => UpdateEmpty();
        UpdateEmpty();

        SourceInitialized += (_, _) =>
        {
            // Windows 11 22H2+：系统 Acrylic 毛玻璃；更老的系统退回纯色背景
            if (WindowEffects.ApplyBackdrop(this, WindowEffects.Acrylic, ThemeManager.IsDark)) Background = Brushes.Transparent;
            ThemeManager.Changed += () =>
            {
                WindowEffects.ApplyBackdrop(this, WindowEffects.Acrylic, ThemeManager.IsDark);
                foreach (var d in _hub.Devices) d.RefreshTheme();
            };
            WindowEffects.HideSystemCaptionButtons(this); // 用自己画的简洁按钮，系统按钮在毛玻璃上会隐约露出来
        };
        // 最大化时窗口会超出屏幕一圈边框，内容收进来
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(8) : new Thickness(0);
            MaxBtn.Content = WindowState == WindowState.Maximized ? "" : ""; // 还原 / 最大化
        };
        SwAllowControl.IsChecked = _hub.Config.AllowControl;

        if (!_hub.Config.IsConfigured) ShowSetup();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.Exiting)
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    void HideToTray()
    {
        Hide();
        if (!_hintShown && _hub.Config.IsConfigured)
        {
            _hintShown = true;
            _hub.GetType(); // 保持后台运行，托盘图标可重新打开
        }
    }

    void Close_Click(object s, RoutedEventArgs e) => HideToTray();
    void Min_Click(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object s, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Hub_PropertyChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Hub.Probe)) BuildIpRuleButtons();
        if (e.PropertyName == nameof(Hub.NetOk))
            NetDot.Fill = _hub.NetOk ? Palette.Green : Palette.Gray;
    }

    void UpdateEmpty()
    {
        EmptyHint.Visibility = _hub.Devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool noTx = _hub.Transfers.Count == 0;
        TransfersEmpty.Visibility = noTx ? Visibility.Visible : Visibility.Collapsed;
        TransfersGroup.Visibility = noTx ? Visibility.Collapsed : Visibility.Visible;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDevices == null) return; // 初始化阶段
        PageDevices.Visibility = NavDevices.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTransfers.Visibility = NavTransfers.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAssist.Visibility = NavAssist.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavAssist.IsChecked == true) LoadAssist();
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavSettings.IsChecked == true) LoadSettings();
    }

    // ---------------- 远程协助 ----------------
    void LoadAssist()
    {
        SwAssist.IsChecked = _hub.Config.AssistEnabled;
        AssistPrepareBtn.Visibility = _hub.Config.AllowControl && Sunshine.Installed ? Visibility.Collapsed : Visibility.Visible;
        if (_hub.Config.IsConfigured && _hub.Config.AssistEnabled && !_hub.AssistHostActive) _hub.StartAssistHost();
    }

    void AssistEnabled_Click(object s, RoutedEventArgs e) => _hub.SetAssistEnabled(SwAssist.IsChecked == true);

    void AssistRefresh_Click(object s, RoutedEventArgs e)
    {
        _hub.RefreshAssistPassword();
        ShowToast("已换新密码，旧密码立即失效", null);
    }

    void AssistCopy_Click(object s, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"Farloop 远程协助\n设备码：{_hub.AssistIdText}\n临时密码：{_hub.AssistPassword}");
            ShowToast("已复制，发给要协助你的人", null);
        }
        catch { }
    }

    async void AssistPrepare_Click(object s, RoutedEventArgs e)
    {
        AssistPrepareBtn.IsEnabled = false;
        try
        {
            var msg = await _hub.SetAllowControlAsync(true, m => Dispatcher.BeginInvoke(() => ShowToast(m, null)));
            if (msg != null) ShowToast(msg, null);
            SwAllowControl.IsChecked = _hub.Config.AllowControl;
            _hub.StartAssistHost();
            LoadAssist();
        }
        finally { AssistPrepareBtn.IsEnabled = true; }
    }

    void AssistDisconnect_Click(object s, RoutedEventArgs e)
    {
        _hub.DisconnectAssist();
        ShowToast("已断开，并换了新密码", null);
    }

    async void AssistConnect_Click(object s, RoutedEventArgs e)
    {
        AssistConnectBtn.IsEnabled = false;
        try { await _hub.AssistConnectAsync(AssistIdBox.Text, AssistPwBox.Text); }
        finally { AssistConnectBtn.IsEnabled = true; }
    }

    void AssistPw_KeyDown(object s, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AssistConnect_Click(s, e);
    }

    void AssistEnd_Click(object s, RoutedEventArgs e) => _hub.EndAssistHelp("已结束协助");

    // ---------------- 提示条 ----------------
    void ShowToast(string text, string? path)
    {
        ToastText.Text = text;
        _toastPath = path;
        ToastOpen.Visibility = path != null ? Visibility.Visible : Visibility.Collapsed;
        ToastBar.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void ToastOpen_Click(object s, RoutedEventArgs e) { if (_toastPath != null) OpenPath(_toastPath); }

    static void OpenPath(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); } catch { }
    }

    // ---------------- 设备卡片 ----------------
    static DeviceVM? Dev(object sender) => (sender as FrameworkElement)?.DataContext as DeviceVM;

    async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is { } d) await _hub.ConnectAsync(d);
    }

    void RemoveDevice_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is not { } d) return;
        if (MessageBox.Show(this, $"从列表里移除“{d.Name}”（{d.Ip}）？\n\n它下次上线时会自动重新出现；移除后不能再对它“远程开机”，直到它重新上线一次。",
                "移除设备", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        _hub.RemoveDevice(d);
    }

    async void Wake_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is { } d) await _hub.WakeAsync(d);
    }

    void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is { } d) _hub.Disconnect(d);
    }

    /// <summary>卡片上的“详情”：设备参数、快速启动（里面还有“终端”）</summary>
    void More_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is not { } d) return;
        foreach (Window w in OwnedWindows)
            if (w is DeviceDetailsWindow dw && dw.Device == d) { dw.Activate(); return; }
        new DeviceDetailsWindow(d) { Owner = this }.Show();
    }

    async void SendFiles_Click(object sender, RoutedEventArgs e)
    {
        if (Dev(sender) is not { } d) return;
        var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"发送到 {d.Name}" };
        if (dlg.ShowDialog(this) != true) return;
        NavTransfers.IsChecked = true;
        await _hub.SendFilesAsync(d, dlg.FileNames);
    }

    // 卡片背景是绑定的渐变，拖拽高亮只改边框，不能覆盖背景
    void Device_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && sender is Border b)
        {
            b.BorderBrush = (Brush)FindResource("Accent");
            b.BorderThickness = new Thickness(2);
            e.Effects = DragDropEffects.Copy;
        }
    }

    void Device_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Border b)
        {
            b.BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
            b.BorderThickness = new Thickness(1);
        }
    }

    // ---------------- 侧边栏 ----------------
    async void SidebarSend_Click(object s, RoutedEventArgs e)
    {
        var online = _hub.Devices.Where(d => d.Online).ToList();
        if (online.Count == 1)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = $"发送到 {online[0].Name}" };
            if (dlg.ShowDialog(this) != true) return;
            NavTransfers.IsChecked = true;
            await _hub.SendFilesAsync(online[0], dlg.FileNames);
            return;
        }
        NavDevices.IsChecked = true;
        ShowToast(online.Count == 0 ? "没有在线的设备" : "有多台设备在线：点卡片上的“发送文件”，或直接把文件拖到卡片上", null);
    }

    async void AllowControl_Click(object s, RoutedEventArgs e)
    {
        bool on = SwAllowControl.IsChecked == true;
        SwAllowControl.IsEnabled = false;
        string? msg;
        try { msg = await _hub.SetAllowControlAsync(on, m => Dispatcher.BeginInvoke(() => ShowToast(m, null))); }
        finally { SwAllowControl.IsEnabled = true; }
        SwAllowControl.IsChecked = _hub.Config.AllowControl;
        if (msg != null) ShowToast(msg, null);
    }

    void Help_Click(object s, RoutedEventArgs e) =>
        ShowToast("远程中快捷键：Ctrl+Alt+Shift+X 全屏/窗口 · Z 释放鼠标 · S 详细统计 · Q 结束远程。遇到问题可在 设置 → 网络 → 打开日志", null);

    void Exit_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "退出 Farloop？退出后本机不能被远程（组网和串流服务仍在后台运行，开机会自动启动）。",
                "Farloop", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            ((App)Application.Current).ExitApp();
    }

    async void Device_Drop(object sender, DragEventArgs e)
    {
        Device_DragLeave(sender, e);
        if (Dev(sender) is not { } d || e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        await _hub.SendFilesAsync(d, paths);
    }

    void CopyInvite_Click(object s, RoutedEventArgs e)
    {
        if (_hub.InviteCode == "") return;
        try { Clipboard.SetText(_hub.InviteCode); } catch { }
        ShowToast("邀请码已复制。它相当于网络密码，只发给自己的电脑。", null);
    }

    // ---------------- 传输页 ----------------
    void OpenReceive_Click(object s, RoutedEventArgs e)
    {
        Directory.CreateDirectory(_hub.Config.ReceiveFolder);
        OpenPath(_hub.Config.ReceiveFolder);
    }

    void ClearTransfers_Click(object s, RoutedEventArgs e)
    {
        foreach (var t in _hub.Transfers.Where(t => t.Finished).ToList()) _hub.Transfers.Remove(t);
    }

    void ShowTransfer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is TransferVM t)
            OpenPath(t.OpenPath ?? _hub.Config.ReceiveFolder);
    }

    // ---------------- 设置页 ----------------
    void LoadSettings()
    {
        var c = _hub.Config;
        NameBox.Text = c.DeviceName;
        ClipSwitch.IsChecked = c.ClipboardSync;
        FileClipSwitch.IsChecked = c.FileClipboardSync;
        FolderText.Text = c.ReceiveFolder;
        SunshineState.Text = !c.AllowControl ? "未启用" : Sunshine.Installed ? "已安装" : "未安装";
        MoonlightState.Text = !c.CanControl ? "未启用" : Moonlight.Installed ? "已安装" : "未安装";
        PeersBox.Text = string.Join(Environment.NewLine, c.Peers);
        SwAutostart.IsChecked = c.AutoStart;
        SwP2POnly.IsChecked = c.P2POnly;
        SwPrivacyVirtual.IsChecked = c.PrivacyVirtual;
        BuildSeg(SegTheme, new[] { ("跟随系统", "0"), ("浅色", "1"), ("深色", "2") }, c.Theme.ToString(), v =>
        {
            c.Theme = int.Parse(v);
            ThemeManager.Apply(c.Theme);
        });
        IpRuleBox.Text = c.PublicIpRule;
        _ = LoadWakeReadinessAsync();

        var st = c.Stream;
        BuildSeg(SegMode, new[] { ("窗口", "windowed"), ("无边框全屏", "borderless"), ("全屏", "fullscreen") }, st.DisplayMode, v => st.DisplayMode = v);
        BuildSeg(SegRes, new[] { ("720p", "1280x720"), ("1080p", "1920x1080"), ("2K", "2560x1440"), ("4K", "3840x2160") }, st.Resolution, v => st.Resolution = v);
        BuildSeg(SegFps, new[] { "30", "60", "90", "120", "144", "165" }.Select(f => (f, f)).ToArray(), st.Fps.ToString(), v => st.Fps = int.Parse(v));
        BuildSeg(SegBitrate, new[] { ("自动", "0"), ("10M", "10"), ("20M", "20"), ("40M", "40"), ("80M", "80"), ("150M", "150") }, st.BitrateMbps.ToString(), v => st.BitrateMbps = int.Parse(v));
        BuildSeg(SegCodec, new[] { ("自动", "auto"), ("H.264", "H.264"), ("HEVC", "HEVC"), ("AV1", "AV1") }, st.Codec, v => st.Codec = v);
        SwHdr.IsChecked = st.Hdr;
        SwYuv.IsChecked = st.Yuv444;
        SwVsync.IsChecked = st.VSync;
        SwPacing.IsChecked = st.FramePacing;
        SwMouse.IsChecked = st.DesktopMouse;
        SwAudioHost.IsChecked = st.AudioOnHost;
        SwCard.IsChecked = st.InfoCard;
        SwPerf.IsChecked = st.PerfOverlay;
    }

    /// <summary>用分段控件呈现一组选项，选中即保存</summary>
    void BuildSeg(Panel host, (string Label, string Value)[] options, string current, Action<string> apply)
    {
        host.Children.Clear();
        var style = (Style)FindResource("Segment");
        foreach (var (label, value) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = host.Name, Style = style, IsChecked = value == current };
            rb.Checked += (_, _) => { apply(value); _hub.Config.Save(); };
            host.Children.Add(rb);
        }
    }

    void StreamToggle_Click(object s, RoutedEventArgs e)
    {
        var st = _hub.Config.Stream;
        st.Hdr = SwHdr.IsChecked == true;
        st.Yuv444 = SwYuv.IsChecked == true;
        st.VSync = SwVsync.IsChecked == true;
        st.FramePacing = SwPacing.IsChecked == true;
        st.DesktopMouse = SwMouse.IsChecked == true;
        st.AudioOnHost = SwAudioHost.IsChecked == true;
        st.InfoCard = SwCard.IsChecked == true;
        st.PerfOverlay = SwPerf.IsChecked == true;
        _hub.Config.Save();
    }

    async Task LoadWakeReadinessAsync()
    {
        try
        {
            var r = await WakeOnLan.CheckLocalAsync();
            WakeNic.Text = r.Adapter == null ? "没找到正在使用的网卡" : $"{r.Adapter}（{(r.Wired ? "有线" : "无线，唤醒可能不支持")}）  {r.Mac}";
            WakeMagic.Text = r.MagicPacket switch { true => "已开启 ✓", false => "未开启", _ => "网卡不支持或无法检测" };
            WakeMagic.Foreground = r.MagicPacket == true ? Palette.Green : Palette.Orange;
            WakeFast.Text = r.FastStartup ? "开启中（需关闭）" : "已关闭 ✓";
            WakeFast.Foreground = r.FastStartup ? Palette.Orange : Palette.Green;
            bool ready = r.MagicPacket == true && !r.FastStartup;
            WakeEnableBtn.Content = ready ? "已就绪" : "一键开启";
            WakeEnableBtn.IsEnabled = !ready && r.Adapter != null;
        }
        catch (Exception ex) { Log.Error("检测远程开机状态失败", ex); }
    }

    async void WakeEnable_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将修改本机设置：\n· 允许网卡被“魔术包”唤醒\n· 关闭 Windows 快速启动（开机会慢几秒）\n\n继续吗？",
                "开启远程开机", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        WakeEnableBtn.IsEnabled = false;
        WakeEnableBtn.Content = "设置中…";
        try { await WakeOnLan.EnableLocalAsync(); ShowToast("已开启。别忘了在 BIOS 里打开网络唤醒（只需一次）", null); }
        catch (Exception ex) { ShowToast("开启失败：" + ex.Message, null); }
        await LoadWakeReadinessAsync();
    }

    void Uninstall_Click(object s, RoutedEventArgs e)
    {
        var exe = File.Exists(Installer.InstalledExe) ? Installer.InstalledExe : Environment.ProcessPath!;
        // 卸载程序会先结束本进程，所以这里只负责启动它
        Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = true });
    }

    async void CheckUpdates_Click(object s, RoutedEventArgs e) => await _hub.CheckUpdatesAsync();

    async void UpdateComponent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ComponentVM c) return;
        if (_hub.UpdateBlocker(c) is { } why) { ShowToast(why, null); return; }
        if (_hub.UpdateWarning(c) is { } warn &&
            MessageBox.Show(this, warn, "Farloop", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await _hub.UpdateAsync(c);
    }

    async void PrivacyVirtual_Click(object s, RoutedEventArgs e)
    {
        bool on = SwPrivacyVirtual.IsChecked == true;
        _hub.Config.PrivacyVirtual = on;
        _hub.Config.Save();
        if (on && !VirtualDisplay.Installed)
        {
            ShowToastLocal("正在安装虚拟显示器驱动…");
            var err = await VirtualDisplay.InstallAsync();
            ShowToastLocal(err == null ? "虚拟显示器驱动已安装" : "安装失败：" + err + "（“关闭对方屏幕”会改用黑色遮罩）");
        }
    }

    void ShowToastLocal(string m) => _hub.ShowToast(m);

    async void P2POnly_Click(object s, RoutedEventArgs e)
    {
        bool on = SwP2POnly.IsChecked == true;
        if (on && MessageBox.Show(this, "仅 P2P：数据只走直连，不经过中转节点。\n和某台设备打洞不成功时，就连不上那台设备。\n\n确定打开？",
                "Farloop", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) { SwP2POnly.IsChecked = false; return; }
        _hub.Config.P2POnly = on;
        await _hub.ApplyNetSettingsAsync();
    }

    /// <summary>多条宽带时：每个出口一个“只用它”按钮（推荐的标出来），外加“自动”</summary>
    void BuildIpRuleButtons()
    {
        IpRuleButtons.Children.Clear();
        var p = _hub.Probe;
        if (p == null || !p.Multi) return;
        var cur = EasyTier.NormalizeIpRule(_hub.Config.PublicIpRule);
        void Add(string label, string rule, bool primary)
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 8, 6), Style = (Style)FindResource(primary ? "CardAction" : "CardActionGlass") };
            if (rule == cur) b.Content = "✓ " + label;
            b.Click += async (_, _) => { IpRuleBox.Text = rule; await _hub.SetIpRuleAsync(rule); };
            IpRuleButtons.Children.Add(b);
        }
        foreach (var (ip, _) in p.Ips)
        {
            var rule = _hub.RuleFor(ip);
            bool rec = ip == p.Main;
            Add($"只用 {rule}.*" + (rec ? "（推荐）" : ""), rule, rec);
        }
        Add("自动", "", false);
    }

    async void UseSuggestedRule_Click(object s, RoutedEventArgs e) => await _hub.UseSuggestedRuleAsync();
    void DismissNetTip_Click(object s, RoutedEventArgs e) => _hub.DismissNetTip();

    async void SaveIpRule_Click(object s, RoutedEventArgs e)
    {
        var raw = IpRuleBox.Text.Trim();
        var rule = EasyTier.NormalizeIpRule(raw);
        if (raw.Length > 0 && rule.Length == 0) { MessageBox.Show(this, "格式不对。填公网 IP 的前几段，例如 203.0；多个用逗号分隔。", "Farloop"); return; }
        IpRuleBox.Text = rule;
        if (rule == _hub.Config.PublicIpRule) return;
        _hub.Config.PublicIpRule = rule;
        await _hub.ApplyNetSettingsAsync();
    }

    async void Autostart_Click(object s, RoutedEventArgs e)
    {
        bool on = SwAutostart.IsChecked == true;
        try
        {
            await Installer.SetAutostartAsync(on);
            _hub.Config.AutoStart = on;
            _hub.Config.Save();
            ShowToast(on ? "已开启开机自动启动" : "已关闭开机自动启动（组网和串流服务仍会随开机运行）", null);
        }
        catch (Exception ex)
        {
            SwAutostart.IsChecked = !on;
            ShowToast("设置失败：" + ex.Message, null);
        }
    }

    void ResetPeers_Click(object s, RoutedEventArgs e) =>
        PeersBox.Text = string.Join(Environment.NewLine, AppConfig.DefaultPeers);

    async void SavePeers_Click(object s, RoutedEventArgs e)
    {
        var lines = PeersBox.Text.Split('\n').Select(l => l.Trim()).Where(l => l != "").Distinct().ToList();
        var bad = lines.FirstOrDefault(l => !EasyTier.IsValidPeer(l));
        if (bad != null) { ShowToast($"格式不对：{bad}（例如 tcp://example.com:11010）", null); return; }
        if (lines.Count == 0) { ShowToast("至少要有一个节点", null); return; }
        _hub.Config.Peers = lines;
        _hub.Config.Save();
        ShowToast("正在重启组网…", null);
        await Installer.RegisterEasyTierAsync(_hub.Config);
        ShowToast($"已保存 {lines.Count} 个节点，组网已重启", null);
    }

    void Toggles_Click(object s, RoutedEventArgs e)
    {
        _hub.Config.ClipboardSync = ClipSwitch.IsChecked == true;
        _hub.Config.FileClipboardSync = FileClipSwitch.IsChecked == true;
        _hub.Config.Save();
    }

    async void SaveName_Click(object s, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name == "" || name == _hub.Config.DeviceName) return;
        _hub.Config.DeviceName = name;
        _hub.Config.Save();
        _hub.ReloadConfig(_hub.Config);
        await Installer.RegisterEasyTierAsync(_hub.Config); // 主机名变了，重启组网
        ShowToast("设备名称已保存", null);
    }

    void ChangeFolder_Click(object s, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择接收文件夹", InitialDirectory = _hub.Config.ReceiveFolder };
        if (dlg.ShowDialog(this) != true) return;
        _hub.Config.ReceiveFolder = dlg.FolderName;
        _hub.Config.Save();
        FolderText.Text = dlg.FolderName;
    }

    async void ResetSunshine_Click(object s, RoutedEventArgs e)
    {
        if (!Sunshine.Installed) { ShowToast("本机没有安装 Sunshine", null); return; }
        var pass = AppConfig.RandomToken(20);
        var (ok, msg) = await Sunshine.SetCredentialsAsync("fr", pass);
        if (ok)
        {
            _hub.Config.SunshineUser = "fr";
            _hub.Config.SunshinePassword = pass;
            _hub.Config.Save();
        }
        ShowToast(ok ? "串流账号已重新设置" : msg, null);
    }

    void ForgetPair_Click(object s, RoutedEventArgs e)
    {
        _hub.ForgetPairings();
        ShowToast("已清除。下次连接会自动重新配对。", null);
    }

    async void RestartNet_Click(object s, RoutedEventArgs e)
    {
        ShowToast("正在重启组网服务…", null);
        await Installer.RestartEasyTierAsync();
        ShowToast("组网服务已重启", null);
    }

    void OpenLog_Click(object s, RoutedEventArgs e)
    {
        if (File.Exists(Log.FilePath)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Log.FilePath}\""));
    }

    async void Reset_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将退出当前网络并删除本机配置（已安装的 Sunshine / Moonlight 保留）。确定吗？",
                "Farloop", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        _hub.Stop();
        await Installer.UninstallTasksAsync();
        var fresh = new AppConfig();
        fresh.Save();
        _hub.ReloadConfig(fresh);
        _hub.Devices.Clear();
        ShowSetup();
    }

    // ================= 首次设置向导 =================
    readonly ObservableCollection<SetupStep> _steps = new();

    void ShowSetup()
    {
        SetupOverlay.Visibility = Visibility.Visible;
        SetupForm.Visibility = Visibility.Visible;
        SetupProgress.Visibility = Visibility.Collapsed;
        SetupName.Text = _hub.Config.DeviceName;
        SetupError.Visibility = Visibility.Collapsed;
    }

    void SetupOpt_Checked(object s, RoutedEventArgs e)
    {
        if (InviteBox != null) InviteBox.Visibility = OptJoin.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void SetupFail(string msg)
    {
        SetupError.Text = msg;
        SetupError.Visibility = Visibility.Visible;
    }

    async void SetupStart_Click(object sender, RoutedEventArgs e)
    {
        var name = SetupName.Text.Trim();
        if (name == "") { SetupFail("请填写设备名称"); return; }

        var cfg = new AppConfig
        {
            DeviceName = name,
            AllowControl = SetupAllow.IsChecked == true,
            CanControl = SetupControl.IsChecked == true,
        };
        bool create = OptCreate.IsChecked == true;
        if (create)
        {
            cfg.NetworkName = "fr-" + AppConfig.RandomToken(12);
            cfg.NetworkSecret = AppConfig.RandomToken(32);
            cfg.VirtualIp = "10.144.144.1";
        }
        else
        {
            if (!AppConfig.TryParseInvite(InviteBox.Text, out var n, out var sec)) { SetupFail("邀请码格式不对，请完整复制（以 FR1- 开头）"); return; }
            cfg.NetworkName = n;
            cfg.NetworkSecret = sec;
            cfg.VirtualIp = AppConfig.RandomIp();
        }

        SetupForm.Visibility = Visibility.Collapsed;
        SetupProgress.Visibility = Visibility.Visible;
        SetupDone.IsEnabled = false;
        InviteResult.Visibility = Visibility.Collapsed;
        SetupTitle.Text = "正在设置…";
        _steps.Clear();
        StepList.ItemsSource = _steps;

        bool fatal = false;
        async Task Step(string title, Func<SetupStep, Task> work, bool critical = false)
        {
            var st = new SetupStep(title);
            _steps.Add(st);
            if (fatal) { st.Warn("已跳过"); return; }
            st.Running();
            try { await work(st); if (st.GlyphBrush == Palette.Blue) st.Ok(); }
            catch (Exception ex)
            {
                Log.Error("安装步骤失败：" + title, ex);
                st.Fail(ex.Message);
                if (critical) fatal = true;
            }
        }

        await Step("安装 Farloop", st => Task.Run(Installer.InstallSelf));

        await Step("下载组网组件 EasyTier", async st =>
        {
            var p = new Progress<string>(m => st.Detail = m);
            try { await Installer.DownloadEasyTierAsync(p); }
            catch (Exception ex)
            {
                throw new Exception($"下载失败（{ex.Message}）。可以手动从 github.com/EasyTier/EasyTier/releases 下载 windows-x86_64 压缩包，" +
                                    $"把里面的文件解压到 {EasyTier.Dir}，然后重新运行设置。");
            }
        }, critical: true);

        if (cfg.AllowControl)
        {
            await Step("安装 Sunshine（被远程）", async st =>
            {
                if (!Sunshine.Installed)
                {
                    st.Detail = "通过 winget 安装…";
                    await Installer.WingetInstallAsync("LizardByte.Sunshine", SunshineFarloop.BaseVersion);
                    if (!Sunshine.Installed) await Installer.WingetInstallAsync("LizardByte.Sunshine");
                }
                if (!Sunshine.Installed) { st.Warn("安装失败，请手动安装 Sunshine 后在设置里“重新设置串流账号”"); return; }
                st.Detail = "设置串流账号…";
                var pass = AppConfig.RandomToken(20);
                var (ok, msg) = await Sunshine.SetCredentialsAsync("fr", pass);
                if (ok) { cfg.SunshineUser = "fr"; cfg.SunshinePassword = pass; }
                await Installer.DisableSleepOnAcAsync();
                if (ok) st.Ok("已安装，插电时不再自动睡眠"); else st.Warn(msg);
            });
        }

        if (cfg.CanControl)
        {
            await Step("安装 Moonlight（控制别人）", async st =>
            {
                if (!Moonlight.Installed)
                {
                    st.Detail = "通过 winget 安装…";
                    await Installer.WingetInstallAsync("MoonlightGameStreamingProject.Moonlight");
                }
                if (Moonlight.Installed) st.Ok("已安装"); else st.Warn("安装失败，请手动安装 Moonlight");
            });
        }

        await Step("配置网络", async st =>
        {
            cfg.Save();
            await Installer.RegisterEasyTierAsync(cfg);
            await Installer.ConfigureFirewallAsync();
            st.Ok($"虚拟 IP {cfg.VirtualIp}");
        }, critical: true);

        await Step("开机自动运行", st => Installer.RegisterAutostartAsync());

        if (fatal)
        {
            SetupTitle.Text = "设置未完成";
            SetupDone.Content = "返回";
            SetupDone.IsEnabled = true;
            SetupDone.Tag = "back";
            return;
        }

        _hub.ReloadConfig(cfg);
        SetupTitle.Text = "设置完成";
        if (create)
        {
            InviteOut.Text = cfg.InviteCode;
            InviteResult.Visibility = Visibility.Visible;
        }
        SetupDone.Content = "完成";
        SetupDone.Tag = null;
        SetupDone.IsEnabled = true;
    }

    void SetupDone_Click(object sender, RoutedEventArgs e)
    {
        if (SetupDone.Tag as string == "back") { ShowSetup(); return; }
        SetupOverlay.Visibility = Visibility.Collapsed;
        _hub.Start();
    }
}
