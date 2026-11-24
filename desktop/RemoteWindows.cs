using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>远程终端：在对方电脑上开一个 PowerShell，一行一行执行（和手机端同一套协议）。</summary>
public sealed class TerminalWindow : Window
{
    readonly DeviceVM _d;
    readonly TextBox _out = new();
    readonly TextBox _in = new();
    readonly TextBlock _prompt = new();
    readonly StringBuilder _buf = new();
    readonly List<string> _hist = new();
    int _histPos;
    string? _id;
    string _cwd = "";
    readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(8) };

    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x0E));
    static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x22));

    public TerminalWindow(DeviceVM d)
    {
        _d = d;
        Title = $"终端 · {d.Name}";
        Width = 900; Height = 580; MinWidth = 520; MinHeight = 320;
        Background = Bg;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Application.Current.MainWindow?.Icon;

        var root = new DockPanel { Margin = new Thickness(0) };
        var head = new StackPanel { Margin = new Thickness(16, 12, 16, 8) };
        head.Children.Add(new TextBlock { Text = $"终端 · {d.Name}", Foreground = Brushes.White, FontSize = 16, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = "Windows PowerShell（对方以管理员权限运行，请谨慎执行删除类命令）", Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x40)), FontSize = 12, Margin = new Thickness(0, 3, 0, 0) });
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var bar = new DockPanel { Margin = new Thickness(12, 6, 12, 12) };
        _prompt.Text = "PS>";
        _prompt.Foreground = new SolidColorBrush(Color.FromRgb(0x5E, 0xB3, 0xFF));
        _prompt.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _prompt.FontWeight = FontWeights.Bold;
        _prompt.VerticalAlignment = VerticalAlignment.Center;
        _prompt.MaxWidth = 320;
        _prompt.TextTrimming = TextTrimming.CharacterEllipsis;
        DockPanel.SetDock(_prompt, Dock.Left);
        bar.Children.Add(_prompt);
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(btns, Dock.Right);
        btns.Children.Add(Btn("Ctrl+C", Interrupt));
        btns.Children.Add(Btn("清屏", () => { _buf.Clear(); _out.Clear(); }));
        bar.Children.Add(btns);
        _in.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _in.FontSize = 13.5;
        _in.Foreground = Brushes.White;
        _in.CaretBrush = Brushes.White;
        _in.Background = Panel;
        _in.BorderThickness = new Thickness(0);
        _in.Padding = new Thickness(10, 7, 10, 7);
        _in.Margin = new Thickness(8, 0, 4, 0);
        _in.PreviewKeyDown += In_KeyDown;
        bar.Children.Add(_in);
        DockPanel.SetDock(bar, Dock.Bottom);
        root.Children.Add(bar);

        _out.IsReadOnly = true;
        _out.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _out.FontSize = 13;
        _out.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));
        _out.Background = Bg;
        _out.BorderThickness = new Thickness(0);
        _out.Padding = new Thickness(14, 4, 14, 4);
        _out.TextWrapping = TextWrapping.NoWrap;
        _out.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _out.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        root.Children.Add(_out);
        Content = root;

        Hub.I.RemoteMessage += OnMessage;
        _timeout.Tick += (_, _) =>
        {
            _timeout.Stop();
            if (_id == null) Append("对方没有响应。对方电脑需要 Farloop 0.12.1 以上，并开启“允许被远程”。\n");
        };
        Loaded += async (_, _) =>
        {
            _in.Focus();
            Append($"正在连接 {d.Name} 的 PowerShell…\n");
            _timeout.Start();
            if (!await Hub.I.SendToAsync(d, MsgType.TermOpen, Array.Empty<byte>())) Append("连接失败。\n");
        };
        Closed += (_, _) =>
        {
            Hub.I.RemoteMessage -= OnMessage;
            if (_id != null) _ = Hub.I.SendJsonToAsync(_d, MsgType.TermClose, new TermCloseMsg(_id));
        };
    }

    static Button Btn(string text, Action a)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(12, 6, 12, 6),
            Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), Background = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2B)),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
        };
        b.Click += (_, _) => a();
        return b;
    }

    void In_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Run(); e.Handled = true; }
        else if (e.Key == Key.Up) { Hist(-1); e.Handled = true; }
        else if (e.Key == Key.Down) { Hist(1); e.Handled = true; }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _in.SelectionLength == 0) { Interrupt(); e.Handled = true; }
    }

    void Hist(int d)
    {
        if (_hist.Count == 0) return;
        _histPos = Math.Clamp(_histPos + d, 0, _hist.Count);
        _in.Text = _histPos < _hist.Count ? _hist[_histPos] : "";
        _in.CaretIndex = _in.Text.Length;
    }

    void Run()
    {
        if (_id == null) return;
        var line = _in.Text;
        _in.Clear();
        var t = line.Trim();
        if (t.Equals("cls", StringComparison.OrdinalIgnoreCase) || t.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _buf.Clear(); _out.Clear();
            _ = Hub.I.SendJsonToAsync(_d, MsgType.TermInput, new TermInputMsg(_id, ""));
            return;
        }
        if (t.Length > 0) { _hist.Add(line); _histPos = _hist.Count; }
        Append($"PS {_cwd}> {line}\n");
        _ = Hub.I.SendJsonToAsync(_d, MsgType.TermInput, new TermInputMsg(_id, line));
    }

    void Interrupt()
    {
        if (_id == null) return;
        Append("^C\n[正在结束当前命令…]\n");
        var id = _id;
        _id = null;
        _ = Hub.I.SendJsonToAsync(_d, MsgType.TermInterrupt, new TermCloseMsg(id));
    }

    void OnMessage(string ip, MsgType t, byte[] b)
    {
        if (ip != _d.Ip) return;
        try
        {
            if (t == MsgType.TermOpened)
            {
                var m = Frames.FromJson<TermOpenedMsg>(b);
                bool first = _cwd.Length == 0;
                _id = m.Id; _cwd = m.Cwd;
                _prompt.Text = $"PS {_cwd}>";
                if (first) Append("已连接。\n");
            }
            else if (t == MsgType.TermOutput)
            {
                var m = Frames.FromJson<TermOutputMsg>(b);
                if (_id != null && m.Id != _id) return;
                Append(m.Text);
                if (m.Prompt != null) { _cwd = m.Prompt; _prompt.Text = $"PS {_cwd}>"; }
            }
        }
        catch (Exception e) { Log.Error("终端消息", e); }
    }

    void Append(string s)
    {
        _buf.Append(s);
        if (_buf.Length > 400_000) _buf.Remove(0, _buf.Length - 400_000);
        _out.Text = _buf.ToString();
        _out.ScrollToEnd();
    }
}

/// <summary>设备详情 + 快速启动：看对方配置，点一下在对方电脑上打开应用并进入桌面。</summary>
public sealed class DeviceDetailsWindow : Window
{
    readonly DeviceVM _d;
    public DeviceVM Device => _d;
    readonly Image _preview = new() { Stretch = Stretch.Uniform, MaxHeight = 230 };
    readonly StackPanel _rows = new();
    readonly WrapPanel _quick = new();
    readonly TextBlock _state = new() { Foreground = Brushes.Gray, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(5) };
    AppEntry[]? _apps;
    Window? _picker;

    static string QuickFile => Path.Combine(AppConfig.DataDir, "quick-launch.json");

    public DeviceDetailsWindow(DeviceVM d)
    {
        _d = d;
        Title = $"{d.Name} · 设备详情";
        Width = 760; Height = 720; MinWidth = 560; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Color.FromRgb(0xF3, 0xF4, 0xF7));
        Foreground = ThemeManager.IsDark ? Brushes.White : Brushes.Black;
        Icon = Application.Current.MainWindow?.Icon;

        var sp = new StackPanel { Margin = new Thickness(24, 20, 24, 24) };
        // 预览 + 进入桌面
        var top = Card();
        var tg = new Grid();
        tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        tg.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var prevBox = new Border { Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1A, 0x20)), CornerRadius = new CornerRadius(12), MinHeight = 150, Child = _preview, ClipToBounds = true };
        tg.Children.Add(prevBox);
        var side = new StackPanel { Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Width = 190 };
        side.Children.Add(new TextBlock { Text = d.Name, FontSize = 22, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis });
        side.Children.Add(new TextBlock { Text = d.Ip, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 14) });
        side.Children.Add(Action("进入桌面", true, () => Connect()));
        side.Children.Add(Action("终端", false, () => new TerminalWindow(_d) { Owner = Owner }.Show()));
        side.Children.Add(_state);
        Grid.SetColumn(side, 1);
        tg.Children.Add(side);
        top.Child = tg;
        sp.Children.Add(top);

        // 快速启动
        sp.Children.Add(Header("快速启动", "添加", ShowPicker));
        var qc = Card();
        qc.Child = _quick;
        sp.Children.Add(qc);

        // 配置
        sp.Children.Add(Header("设备信息", null, null));
        var ic = Card();
        ic.Child = _rows;
        sp.Children.Add(ic);
        _rows.Children.Add(new TextBlock { Text = "正在读取…", Foreground = Brushes.Gray });

        Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        Hub.I.RemoteMessage += OnMessage;
        _refresh.Tick += (_, _) => _ = Hub.I.SendToAsync(_d, MsgType.SysInfoReq, new byte[] { 0 });
        Loaded += async (_, _) =>
        {
            RenderQuick();
            if (await Hub.I.SendToAsync(_d, MsgType.SysInfoReq, new byte[] { 1 })) _refresh.Start();
            _ = Task.Delay(8000).ContinueWith(_ => Dispatcher.Invoke(() =>
            {
                if (_rows.Children.Count == 1 && _rows.Children[0] is TextBlock)
                    _rows.Children[0] = new TextBlock { Text = "对方没有响应。对方电脑需要 Farloop 0.12.1 以上，并开启“允许被远程”。", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
            }));
        };
        Closed += (_, _) => { _refresh.Stop(); Hub.I.RemoteMessage -= OnMessage; _picker?.Close(); };
    }

    static Border Card() => new()
    {
        Background = ThemeManager.IsDark ? new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x30)) : Brushes.White,
        CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14),
        BorderBrush = new SolidColorBrush(ThemeManager.IsDark ? Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)), BorderThickness = new Thickness(1),
    };

    static FrameworkElement Header(string text, string? action, Action? click)
    {
        var g = new DockPanel { Margin = new Thickness(6, 8, 6, 8) };
        if (action != null)
        {
            var b = new Button { Content = action, Style = (Style)Application.Current.TryFindResource("CardAction") };
            b.Click += (_, _) => click!();
            DockPanel.SetDock(b, Dock.Right);
            g.Children.Add(b);
        }
        g.Children.Add(new TextBlock { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return g;
    }

    static Button Action(string text, bool primary, Action a)
    {
        var b = new Button
        {
            Content = text, Margin = new Thickness(0, 0, 0, 8), HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Application.Current.TryFindResource(primary ? "CardAction" : "CardActionGlass"),
        };
        if (!primary) { b.Background = new SolidColorBrush(ThemeManager.IsDark ? Color.FromRgb(0x3A, 0x3A, 0x3E) : Color.FromRgb(0xEC, 0xEE, 0xF2)); b.Foreground = ThemeManager.IsDark ? Brushes.White : Brushes.Black; }
        b.Click += (_, _) => a();
        return b;
    }

    void Connect()
    {
        if (_d.IsConnected) { _state.Text = "已经连着这台电脑"; return; }
        _ = Hub.I.ConnectAsync(_d);
    }

    // ---------------- 快速启动（按对方 IP 存在本机） ----------------
    Dictionary<string, List<AppEntry>> LoadAll()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, List<AppEntry>>>(File.ReadAllText(QuickFile)) ?? new(); }
        catch { return new(); }
    }
    List<AppEntry> Quick() => LoadAll().TryGetValue(_d.Ip, out var l) ? l : new();
    void SaveQuick(List<AppEntry> l)
    {
        var all = LoadAll();
        all[_d.Ip] = l;
        try { Directory.CreateDirectory(AppConfig.DataDir); File.WriteAllText(QuickFile, JsonSerializer.Serialize(all)); } catch (Exception e) { Log.Error("保存快速启动", e); }
    }

    void RenderQuick()
    {
        _quick.Children.Clear();
        var l = Quick();
        if (l.Count == 0)
        {
            _quick.Children.Add(new TextBlock { Text = "还没有添加应用。点右上角“添加”，从对方电脑的开始菜单里挑；之后点一下就会在对方电脑上打开它并进入桌面。", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 });
            return;
        }
        foreach (var a in l)
        {
            var tile = AppTile(a);
            tile.MouseLeftButtonUp += (_, _) => Launch(a);
            var menu = new ContextMenu();
            var rm = new MenuItem { Header = "从快速启动移除" };
            rm.Click += (_, _) => { var q = Quick(); q.RemoveAll(x => x.Path == a.Path); SaveQuick(q); RenderQuick(); };
            menu.Items.Add(rm);
            tile.ContextMenu = menu;
            tile.ToolTip = a.Name + "（右键可移除）";
            _quick.Children.Add(tile);
        }
    }

    static FrameworkElement AppTile(AppEntry a)
    {
        var sp = new StackPanel { Width = 92, Margin = new Thickness(4, 4, 4, 8), Cursor = Cursors.Hand, Background = Brushes.Transparent };
        var img = new Image { Width = 44, Height = 44, Margin = new Thickness(0, 4, 0, 6) };
        if (a.IconPng != null)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.StreamSource = new MemoryStream(Convert.FromBase64String(a.IconPng)); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                img.Source = bi;
            }
            catch { }
        }
        sp.Children.Add(img);
        sp.Children.Add(new TextBlock { Text = a.Name, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 34, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis });
        return sp;
    }

    async void Launch(AppEntry a)
    {
        _state.Text = $"正在打开 {a.Name}…";
        if (await Hub.I.SendJsonToAsync(_d, MsgType.LaunchApp, new LaunchAppMsg(a.Path))) Connect();
    }

    void ShowPicker()
    {
        if (_picker != null) { _picker.Activate(); return; }
        var list = new WrapPanel { Margin = new Thickness(12) };
        var w = new Window
        {
            Title = $"添加到快速启动 · {_d.Name}", Width = 620, Height = 560, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = list }, Icon = Icon, Background = Brushes.White,
        };
        _picker = w;
        w.Closed += (_, _) => _picker = null;
        void Fill()
        {
            list.Children.Clear();
            var have = Quick().Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var a in _apps!)
            {
                var t = AppTile(a);
                if (have.Contains(a.Path)) t.Opacity = 0.35;
                t.ToolTip = a.Path;
                t.MouseLeftButtonUp += (_, _) =>
                {
                    var q = Quick();
                    if (q.Any(x => string.Equals(x.Path, a.Path, StringComparison.OrdinalIgnoreCase))) return;
                    q.Add(a); SaveQuick(q); RenderQuick();
                    t.Opacity = 0.35;
                };
                list.Children.Add(t);
            }
        }
        w.Tag = (Action)Fill;
        if (_apps != null) Fill();
        else list.Children.Add(new TextBlock { Text = "正在读取对方电脑的应用列表…", Foreground = Brushes.Gray });
        w.Show();
        _ = Hub.I.SendToAsync(_d, MsgType.AppListReq, Array.Empty<byte>());
    }

    // ---------------- 消息 ----------------
    void OnMessage(string ip, MsgType t, byte[] b)
    {
        if (ip != _d.Ip) return;
        try
        {
            switch (t)
            {
                case MsgType.SysInfo: ShowInfo(Frames.FromJson<SysInfoMsg>(b)); break;
                case MsgType.AppList:
                    _apps = Frames.FromJson<AppListMsg>(b).Apps;
                    if (_picker?.Tag is Action fill) fill();
                    break;
                case MsgType.LaunchResult:
                {
                    var r = Frames.FromJson<FsOpResultMsg>(b);
                    _state.Text = r.Ok ? "已在对方电脑上打开" : "打开失败：" + r.Message;
                    break;
                }
            }
        }
        catch (Exception e) { Log.Error("设备详情消息", e); }
    }

    static string Gb(long bytes) => $"{bytes / 1073741824.0:0.#} GB";

    void ShowInfo(SysInfoMsg m)
    {
        if (m.PreviewJpg != null)
        {
            try
            {
                var bi = new BitmapImage();
                bi.BeginInit(); bi.StreamSource = new MemoryStream(Convert.FromBase64String(m.PreviewJpg)); bi.CacheOption = BitmapCacheOption.OnLoad; bi.EndInit(); bi.Freeze();
                _preview.Source = bi;
            }
            catch { }
        }
        _rows.Children.Clear();
        var used = m.RamTotal - m.RamAvail;
        Meter("CPU", m.CpuLoad, $"{m.CpuLoad * 100:0}%");
        Meter("内存", m.RamTotal > 0 ? (double)used / m.RamTotal : 0, $"{Gb(used)} / {Gb(m.RamTotal)}");
        Row("系统", m.Os);
        Row("处理器", $"{m.Cpu}（{m.Cores} 线程）");
        Row("显卡", string.Join("\n", m.Gpus ?? []));
        Row("主板", m.Board);
        Row("硬盘", string.Join("\n", m.Disks ?? []));
        Row("显示器", string.Join("\n", m.Displays ?? []));
        var up = TimeSpan.FromSeconds(m.UptimeSec);
        Row("已开机", up.TotalDays >= 1 ? $"{(int)up.TotalDays} 天 {up.Hours} 小时" : $"{up.Hours} 小时 {up.Minutes} 分");
        Row("局域网 IP", m.LanIp);
        Row("虚拟 IP", m.VirtualIp);
        Row("版本", m.Versions);
    }

    void Row(string k, string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return;
        var g = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new TextBlock { Text = k, Foreground = Brushes.Gray });
        var t = new TextBox { Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap, Foreground = ThemeManager.IsDark ? Brushes.White : Brushes.Black };
        Grid.SetColumn(t, 1);
        g.Children.Add(t);
        _rows.Children.Add(g);
    }

    void Meter(string k, double frac, string text)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.Children.Add(new TextBlock { Text = k, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        var pb = new ProgressBar { Minimum = 0, Maximum = 1, Value = Math.Clamp(frac, 0, 1), Height = 8, VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(0),
            Foreground = (Brush)(Application.Current.TryFindResource("Accent") ?? Brushes.DodgerBlue), Background = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEE)) };
        Grid.SetColumn(pb, 1);
        g.Children.Add(pb);
        var tt = new TextBlock { Text = text, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(tt, 2);
        g.Children.Add(tt);
        _rows.Children.Add(g);
    }
}
