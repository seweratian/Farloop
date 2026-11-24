using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace FreeRemote.Core;

/// <summary>
/// 核心调度：设备发现（EasyTier 节点表）、会话管理、文件传输、剪贴板同步、串流配对。
/// 界面只和这个类打交道。
/// </summary>
public sealed class Hub : Observable, TransferManager.IHost, ClipboardSync.IHost
{
    public static Hub I { get; } = new();

    public AppConfig Config { get; private set; } = AppConfig.Load();
    public ObservableCollection<DeviceVM> Devices { get; } = new();
    public ObservableCollection<TransferVM> Transfers { get; } = new();

    public event Action<string, string?>? Toast; // 文本, 可选的“打开”路径

    Dispatcher _ui = null!;
    TransferManager _tx = null!;
    ClipboardSync _clip = null!;
    CancellationTokenSource _cts = new();
    bool _started;

    readonly object _gate = new();
    readonly List<PeerSession> _sessions = new();
    readonly ConcurrentDictionary<string, Process> _streams = new();
    readonly ConcurrentDictionary<PeerSession, TaskCompletionSource<PairResultMsg>> _pendingPairs = new();
    readonly SemaphoreSlim _connectLock = new(1, 1);

    // ---------- 状态（绑定到界面） ----------
    string _netStatus = "未启动";
    public string NetStatus { get => _netStatus; set => Set(ref _netStatus, value); }

    bool _netOk;
    public bool NetOk { get => _netOk; set { if (Set(ref _netOk, value)) { Raise(nameof(ServiceText)); Raise(nameof(ServiceBrush)); } } }
    public string ServiceText => _netOk ? "服务在线" : "服务离线";
    public System.Windows.Media.Brush ServiceBrush => _netOk ? Palette.Blue : Palette.Gray;

    /// <summary>侧边栏“允许被远程”：关掉时停掉 Sunshine（别人连不进来），打开时启动</summary>
    public async Task<string?> SetAllowControlAsync(bool on, Action<string>? progress = null)
    {
        if (on)
        {
            // 以前只在首次设置时装 Sunshine、设串流账号、设成开机自启；之后再打开这个开关，Sunshine 装了也不会启动，
            // 手机一直提示“没有开启允许被远程”。现在打开开关时把这些都补齐
            var err = await PrepareHostAsync(progress);
            if (err != null) return err;
            Config.AllowControl = true;
            Config.Save();
            Ui(EnsureComponents);
            Ui(StartAssistHost);
            return "已允许被远程";
        }
        Config.AllowControl = on;
        Config.Save();
        await Proc.RunAsync("net", "stop SunshineService", 30_000);
        lock (_gate) foreach (var x in _sessions.Where(x => !x.Outgoing).ToList()) x.Close();
        Ui(StartAssistHost); // 远程协助也跟着关
        return "已关闭被远程：别人现在连不进本机";
    }

    /// <summary>被远程需要的一切：Sunshine 已安装、有串流账号、服务开机自启并在运行、防火墙放行、插电不睡眠。返回 null = 就绪</summary>
    public async Task<string?> PrepareHostAsync(Action<string>? progress = null)
    {
        if (!Sunshine.Installed)
        {
            progress?.Invoke("正在安装 Sunshine（被远程需要它，约 1 分钟）…");
            await Installer.WingetInstallAsync("LizardByte.Sunshine", SunshineFarloop.BaseVersion);
            if (!Sunshine.Installed) await Installer.WingetInstallAsync("LizardByte.Sunshine");
            if (!Sunshine.Installed) return "Sunshine 安装失败。请从 github.com/LizardByte/Sunshine/releases 手动安装后再打开这个开关";
        }
        if (Config.SunshineUser == "" || Config.SunshinePassword == "")
        {
            progress?.Invoke("正在设置串流账号…");
            var pass = AppConfig.RandomToken(20);
            var (ok, msg) = await Sunshine.SetCredentialsAsync("fr", pass);
            if (!ok) return "设置 Sunshine 串流账号失败：" + msg;
            Config.SunshineUser = "fr"; Config.SunshinePassword = pass; Config.Save();
        }
        progress?.Invoke("正在启动 Sunshine…");
        try { await Sunshine.ApplyTuningAsync(); } catch (Exception e) { Log.Error("Sunshine 调优失败", e); }
        await Proc.RunAsync("sc", "config SunshineService start= auto", 10_000);
        if (!Sunshine.IsRunning) await Proc.RunAsync("net", "start SunshineService", 30_000);
        for (int i = 0; i < 20 && !Sunshine.IsRunning; i++) await Task.Delay(500);
        try { await Installer.ConfigureFirewallAsync(); await Installer.DisableSleepOnAcAsync(); } catch (Exception e) { Log.Error("防火墙 / 电源设置失败", e); }
        try { var why = await SunshineFarloop.EnsureAsync(); if (why != null) Log.Info("码率自适应：" + why); } catch (Exception e) { Log.Error("换定制版 Sunshine 失败", e); }
        if (!Sunshine.IsRunning) return "Sunshine 启动失败：" + await Sunshine.DiagnoseAsync();
        Log.Info("被远程已就绪（Sunshine 运行中）");
        return null;
    }

    string _banner = "";
    public string Banner { get => _banner; set { if (Set(ref _banner, value)) Raise(nameof(HasBanner)); } }
    public bool HasBanner => _banner != "";

    public string MyName => Config.DeviceName;
    public string MyIp => Config.VirtualIp;
    public string InviteCode => Config.IsConfigured ? Config.InviteCode : "";

    // ---------- 生命周期 ----------
    public void Init(Window mainWindow)
    {
        _ui = mainWindow.Dispatcher;
        _tx = new TransferManager(this);
        _clip = new ClipboardSync(this);
        _clip.Attach(mainWindow);
        _clip.Blocked += msg => ShowToast(msg);
        StartStatusTimer();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) => { foreach (var t in Transfers) t.Refresh(); };
        timer.Start();
    }

    public void ReloadConfig(AppConfig cfg)
    {
        Config = cfg;
        Raise(nameof(Config)); Raise(nameof(MyName)); Raise(nameof(MyIp)); Raise(nameof(InviteCode));
    }

    public void Start()
    {
        if (_started || !Config.IsConfigured) return;
        _started = true;
        _ = Task.Run(async () => { try { await Installer.EnsureEasyTierRunningAsync(); } catch (Exception e) { Log.Error("启动组网失败", e); } });
        if (Config.AllowControl)
            _ = Task.Run(async () => { try { await Sunshine.ApplyTuningAsync(); } catch (Exception e) { Log.Error("Sunshine 调优失败", e); } });
        _ = Task.Run(() => ListenLoop(_cts.Token));
        Ui(() => { EnsureComponents(); StartUpdateSchedule(); LoadKnownDevices(); StartDragHelper(); });
        _ = Task.Run(() => PollLoop(_cts.Token));
        _ = Task.Run(async () => { Assist.CleanupStale(); await Task.Delay(3000); Ui(StartAssistHost); });
        _ = Task.Run(() => UpnpLoop(_cts.Token));
    }

    /// <summary>设置页显示的路由器端口映射状态</summary>
    public string UpnpStatus => Upnp.Status;

    string _publicIpInfo = "";
    /// <summary>设置页：本机公网 IP；多条宽带（多个公网 IPv4）时给出提示</summary>
    public string PublicIpInfo { get => _publicIpInfo; private set => Set(ref _publicIpInfo, value); }
    bool _multiIp;
    public bool MultiPublicIp { get => _multiIp; private set => Set(ref _multiIp, value); }

    NetProbe.Result? _probe;
    /// <summary>最近一次出口检测结果（设置页按钮、远程设备详情用）</summary>
    public NetProbe.Result? Probe => _probe;

    async Task RefreshPublicIpAsync()
    {
        NetProbe.Result r;
        try { r = await NetProbe.RunAsync(); } catch (Exception e) { Log.Error("出口检测", e); r = new NetProbe.Result(new(), 0, false); }
        // EasyTier 自己看到的公网 IP 也算上（检测时没出现的记 0 次）
        foreach (var ip in await EasyTier.GetPublicIpv4Async())
            if (!r.Ips.Any(x => x.Ip == ip)) r.Ips.Add((ip, 0));
        // 没手动设置时自动选主线路：一条宽带两个出口 IP（联通 9929 等）时，打洞地址必须和发往国内的出口一致
        if (EasyTier.NormalizeIpRule(Config.PublicIpRule).Length == 0 && r.Multi && r.SuggestedRule is { } sug && sug != Config.AutoIpRule)
        {
            Config.AutoIpRule = sug;
            Log.Info($"自动选择打洞线路：{sug}.*（主线路 {r.Main}）");
            _ = Task.Run(ApplyNetSettingsAsync);
        }
        var text = NetAdvice(r);
        Ui(() => { _probe = r; PublicIpInfo = text; MultiPublicIp = r.Multi; Raise(nameof(Probe)); RaiseNetTip(); });
    }

    /// <summary>给人看的网络说明和建议</summary>
    string NetAdvice(NetProbe.Result r)
    {
        if (r.Ips.Count == 0) return "还没有检测到公网 IP";
        var rule = EasyTier.NormalizeIpRule(Config.PublicIpRule);
        var auto = EasyTier.NormalizeIpRule(Config.AutoIpRule);
        var cur = rule.Length > 0 ? $"当前：只用 {rule}.* 打洞" : auto.Length > 0 ? $"当前：自动，已选主线路 {auto}.*" : "当前：自动";
        if (!r.Multi) return $"只有一条宽带（公网 IP {r.Ips[0].Ip}），对打洞友好，不需要设置。{(rule.Length > 0 ? "\n" + cur : "")}";
        int total = Math.Max(1, r.Ips.Sum(x => x.Count));
        var list = string.Join("、", r.Ips.Select(x => x.Count > 0 ? $"{x.Ip}（{x.Count * 100 / total}%）" : x.Ip));
        return $"检测到 {r.Ips.Count} 条宽带出口：{list}。" +
               (r.PerFlow ? "路由器按连接随机选线路，打洞时包会从不同线路出去，很容易失败。" : "通常是一条宽带按目的地分了出口（例如联通 9929：访问境外和国内用不同 IP），打洞地址必须用国内那个。") +
               $"\n建议：只用主线路 {r.Main} 打洞（规则 {r.SuggestedRule}）。{cur}";
    }

    /// <summary>首页提示：多条宽带、还没选线路、也没被关掉时显示</summary>
    public string NetTip
    {
        get
        {
            var p = _probe;
            if (p == null || !p.Multi || p.SuggestedRule == null || Config.NetTipDismissed) return "";
            if (Config.EffectiveIpRule.Length > 0) return "";
            return $"这台电脑有 {p.Ips.Count} 条宽带出口，手机打洞可能变慢。建议只用主线路 {p.Main} 打洞。";
        }
    }
    public bool HasNetTip => NetTip != "";
    void RaiseNetTip() { Raise(nameof(NetTip)); Raise(nameof(HasNetTip)); }

    public async Task UseSuggestedRuleAsync()
    {
        var r = _probe?.SuggestedRule;
        if (r != null) await SetIpRuleAsync(r);
    }

    public void DismissNetTip() { Config.NetTipDismissed = true; Config.Save(); RaiseNetTip(); }

    /// <summary>某个 IP 对应的最短区分前缀（按钮用）</summary>
    public string RuleFor(string ip)
    {
        var others = _probe?.Ips.Select(x => x.Ip).Where(x => x != ip).ToList() ?? new();
        var parts = ip.Split('.');
        for (int n = 2; n <= 4; n++)
        {
            var prefix = string.Join(".", parts.Take(n));
            if (others.All(x => !x.StartsWith(prefix + "."))) return prefix;
        }
        return ip;
    }

    /// <summary>设置打洞用的公网 IP 规则（设置页按钮、远程手机都走这里）</summary>
    public async Task SetIpRuleAsync(string rule)
    {
        rule = EasyTier.NormalizeIpRule(rule);
        if (rule == EasyTier.NormalizeIpRule(Config.PublicIpRule)) return;
        Config.PublicIpRule = rule;
        Log.Info("打洞用的公网 IP 规则：" + (rule.Length == 0 ? "自动" : rule));
        Ui(RaiseNetTip);
        await ApplyNetSettingsAsync();
    }

    /// <summary>设置页改了 仅 P2P / 公网 IP 规则：重新生成开机脚本并重启组网</summary>
    public async Task ApplyNetSettingsAsync()
    {
        Config.Save();
        ShowToast("正在应用网络设置，组网会重连约 10 秒");
        try { await Installer.RegisterEasyTierAsync(Config); } catch (Exception e) { Log.Error("应用网络设置", e); ShowToast("应用失败：" + e.Message); }
        await Task.Delay(15000);
        await RefreshPublicIpAsync();
    }

    /// <summary>每 20 分钟（重新）做一次 UPnP 端口映射：路由器重启或租约过期后也能恢复</summary>
    async Task UpnpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool ok = Upnp.TryMap(MyLan().LanIp);
                if (ok == Config.NoUpnp) { Config.NoUpnp = !ok; Config.Save(); } // 下次组网重启时生效
            }
            catch (Exception e) { Log.Error("UPnP", e); }
            try { await RefreshPublicIpAsync(); } catch { }
            Ui(() => Raise(nameof(UpnpStatus)));
            try { await Task.Delay(TimeSpan.FromMinutes(Upnp.Mapped ? 20 : 5), ct); } catch { return; }
        }
    }

    bool _prepTried, _farloopSunshineDone, _sunshineFailShown, _ddReset;
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _mobileIps = new();
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _gone = new();

    /// <summary>退出前告诉已连接的手机 / 电脑“我下线了”，它们立刻从列表里去掉本机，不用等组网超时</summary>
    public void SayGoodbye()
    {
        List<PeerSession> all;
        lock (_gate) all = _sessions.ToList();
        try { Task.WaitAll(all.Select(s => s.SendAsync(MsgType.Goodbye, Array.Empty<byte>())).ToArray(), 1500); } catch { }
    }

    public void Stop()
    {
        StopAssistHost();
        Assist.StopAll();
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        _started = false;
        lock (_gate) foreach (var s in _sessions.ToList()) s.Close();
    }

    void Ui(Action a) { if (_ui.CheckAccess()) a(); else _ui.BeginInvoke(a); }
    public void ShowToast(string text, string? path = null) => Ui(() => Toast?.Invoke(text, path));

    // ---------- 监听：只绑定虚拟网卡 IP，外网根本连不到 ----------
    Task ListenLoop(CancellationToken ct) => ListenLoop(() => Config.VirtualIp, () => Config.NetworkSecret, ct);

    async Task ListenLoop(Func<string> myIp, Func<string> secret, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpListener? l = null;
            try
            {
                l = new TcpListener(IPAddress.Parse(myIp()), PeerSession.Port);
                l.Start();
                Log.Info($"监听 {myIp()}:{PeerSession.Port}");
                using var reg = ct.Register(() => l.Stop());
                while (!ct.IsCancellationRequested)
                {
                    var c = await l.AcceptTcpClientAsync(ct);
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var s = await PeerSession.AcceptAsync(c, Config.DeviceName, myIp(), secret(), ct);
                            if (s.IsServiceConnection) HandleServiceSession(s); // 问设备信息 / 代发开机信号，不算有人在控制
                            else AddSession(s);
                        }
                        catch (Exception e) { Log.Error("接入连接失败", e); c.Dispose(); }
                    });
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e)
            {
                // 虚拟网卡还没起来时会绑定失败，稍后重试
                Log.Info("监听等待虚拟网卡：" + e.Message);
                try { l?.Stop(); } catch { }
                try { await Task.Delay(3000, ct); } catch { break; }
            }
        }
    }

    // ---------- 设备发现 ----------
    async Task PollLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (Config.AllowControl && (Config.SunshineUser == "" || !Sunshine.Installed) && !_prepTried)
            {
                // 允许被远程却没装好（比如旧版只在首次设置时装 Sunshine）：自动补齐一次
                _prepTried = true;
                var err = await PrepareHostAsync();
                if (err != null) { Log.Error("被远程未就绪：" + err); ShowToast(err); }
            }
            // 换成支持码率自适应的定制版 Sunshine（没人在远程本机时才换，换的时候 Sunshine 会重启几秒）
            if (Config.AllowControl && !_farloopSunshineDone && !IsBeingControlled)
            {
                _farloopSunshineDone = true;
                var why = await SunshineFarloop.EnsureAsync();
                if (why != null) Log.Info("码率自适应：" + why);
            }
            if (Config.AllowControl && !_ddReset && Sunshine.IsRunning)
            {
                _ddReset = true;
                await Sunshine.ResetDisplayPersistenceAsync(Config);
            }
            if (Config.AllowControl)
            {
                try
                {
                    var why = await Sunshine.EnsureRunningAsync();
                    // 起不来时提示一次（原因来自 Sunshine 自己的日志），别让人以为开了“允许被远程”就一定能连
                    if (why != null && !_sunshineFailShown) { _sunshineFailShown = true; ShowToast("Sunshine（被远程组件）启动失败：" + why); }
                    if (why == null && Sunshine.IsRunning) _sunshineFailShown = false;
                } catch (Exception e) { Log.Error("启动 Sunshine 失败", e); }
            }
            var running = EasyTier.IsRunning;
            var peers = running ? await EasyTier.GetPeersAsync() : new List<PeerInfo>();
            var others = peers.Where(p => !p.IsLocal && p.Ip != "" && p.Ip != Config.VirtualIp).ToList();
            // 说过“再见”的设备：路由还没超时也当离线；路由消失、它重新连上或 3 分钟后恢复正常判断
            foreach (var g in _gone.ToArray())
                if (others.All(p => p.Ip != g.Key) || DateTime.UtcNow - g.Value > TimeSpan.FromMinutes(3)) _gone.TryRemove(g.Key, out _);
            others = others.Where(p => !_gone.ContainsKey(p.Ip)).ToList();
            Ui(() =>
            {
                NetOk = running;
                NetStatus = !running ? "组网服务未运行" : others.Count == 0 ? "已启动，等待其他设备" : $"在线设备 {others.Count} 台";
                foreach (var p in others)
                {
                    var d = Devices.FirstOrDefault(x => x.Ip == p.Ip);
                    if (d == null) { d = new DeviceVM(p.Ip); Devices.Add(d); }
                    d.Name = p.DisplayName == "" ? p.Ip : p.DisplayName;
                    d.IsMobile = p.IsMobile;
                    if (p.IsMobile) _mobileIps[p.Ip] = true;
                    d.Online = true;
                    d.IsP2P = p.IsP2P;
                    d.Latency = p.LatencyMs;
                    d.NatType = p.NatType;
                    d.Tunnel = p.Tunnel;
                    d.LossRate = p.LossRate;
                }
                foreach (var d in Devices.Where(d => others.All(p => p.Ip != d.Ip))) d.Online = false;
                // 手机离线后不留在列表里（不能远程开机，而且手机换了虚拟 IP 会留下一个重名的旧条目）
                foreach (var d in Devices.Where(d => d.IsMobile && !d.Online && !d.IsConnected).ToList()) Devices.Remove(d);
            });
            // 在线的设备，没有它的网卡信息（或信息太旧）就去问一下 —— 关机后才能远程开机
            foreach (var p in others)
                if (!p.IsMobile && NeedsInfo(p.Ip)) _ = RefreshDeviceInfoAsync(p.Ip); // 手机不监听会话端口，也不能远程开机
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    // ---------- 会话 ----------
    void AddSession(PeerSession s)
    {
        s.Message += OnMessage;
        s.Closed += OnClosed;
        lock (_gate) _sessions.Add(s);
        _gone.TryRemove(s.RemoteIp, out _);
        s.Start();
        Log.Info($"会话建立：{s.RemoteName} {s.RemoteIp} ({(s.Outgoing ? "出" : "入")}) 对方版本 {s.RemoteVersion}");
        // 两端版本不一致时提示（比如被控端没升级，新功能会悄悄失效 —— 0.5.0 的“拖影”就是这么来的）
        // 手机 App 的版本号和电脑端各自独立，不能拿来比
        if (_mobileIps.ContainsKey(s.RemoteIp) || s.RemotePlatform == "android") { }
        else if (PeerSession.IsOlder(s.RemoteVersion, PeerSession.Version))
            ShowToast($"{s.RemoteName} 还是旧版 Farloop {s.RemoteVersion}（本机 {PeerSession.Version}），本地鼠标、文件拖拽等功能会失效，请在那台电脑上升级");
        else if (PeerSession.IsOlder(PeerSession.Version, s.RemoteVersion))
            ShowToast($"本机 Farloop 是旧版 {PeerSession.Version}，{s.RemoteName} 已是 {s.RemoteVersion}，请升级本机");
        UpdateBanner();
        UpdateEdgeCatcher();
        _ = s.SendJsonAsync(MsgType.DeviceInfo, SelfInfo()); // 顺便交换网卡信息（远程开机用）
    }

    PeerSession? _privacyOwner;

    /// <summary>控制端：对方发来的设备详情 / 终端输出 / 应用列表（界面线程，参数：对方 IP、消息类型、负载）</summary>
    public event Action<string, MsgType, byte[]>? RemoteMessage;

    /// <summary>控制端：给对方发一条消息（终端、设备详情、快速启动用）</summary>
    public async Task<bool> SendToAsync(DeviceVM d, MsgType t, byte[] payload)
    {
        try { var s = await GetSessionAsync(d.Ip); await s.SendAsync(t, payload); return true; }
        catch (Exception e) { ShowToast($"连接 {d.Name} 失败：{Friendly(e)}"); return false; }
    }

    public Task<bool> SendJsonToAsync<T>(DeviceVM d, MsgType t, T obj) => SendToAsync(d, t, Frames.Json(obj));

    /// <summary>控制端：让被控端黑屏 / 恢复</summary>
    public async Task SetPrivacyAsync(DeviceVM d, bool on)
    {
        try
        {
            var s = await GetSessionAsync(d.Ip);
            await s.SendAsync(MsgType.Privacy, new[] { (byte)(on ? 1 : 0) });
        }
        catch (Exception e) { ShowToast("切换屏幕失败：" + Friendly(e)); }
    }

    void OnClosed(PeerSession s)
    {
        // 安全：开黑屏的那一端断开后，被控端屏幕必须自动恢复
        if (_privacyOwner == s) Ui(() => { FreeRemote.PrivacyScreen.Set(false); _privacyOwner = null; });
        if (_cursorHost.Target == s) _cursorHost.Stop();
        RemoteTools.CloseAllFor(s); // 对方断开：关掉它开的终端
        lock (_gate) _sessions.Remove(s);
        _tx.AbortFrom(s);
        if (_pendingPairs.TryRemove(s, out var tcs)) tcs.TrySetResult(new PairResultMsg(false, "连接断开"));
        Log.Info($"会话结束：{s.RemoteName} {s.RemoteIp}");
        UpdateBanner();
        UpdateEdgeCatcher();
        // 被控端：最后一个控制者走了 → 关掉 Sunshine 会话，让它把 HDR 等显示设置还原
        if (!s.Outgoing && Config.AllowControl && !IsBeingControlled) _ = Sunshine.CloseAppAsync(Config);
        if (!s.Outgoing && Assist.IsAssistIp(s.RemoteIp)) _ = AfterAssistSessionAsync();
    }

    PeerSession? OutgoingTo(string ip)
    {
        lock (_gate) return _sessions.FirstOrDefault(s => s.Outgoing && s.RemoteIp == ip && s.IsOpen);
    }

    async Task<PeerSession> GetSessionAsync(string ip)
    {
        await _connectLock.WaitAsync();
        try
        {
            var s = OutgoingTo(ip);
            if (s != null) return s;
            s = Assist.IsAssistIp(ip)
                ? await PeerSession.ConnectAsync(ip, Config.DeviceName, Assist.HelperIp, _helpSecret, _cts.Token)
                : await PeerSession.ConnectAsync(ip, Config.DeviceName, Config.VirtualIp, Config.NetworkSecret, _cts.Token);
            AddSession(s);
            return s;
        }
        finally { _connectLock.Release(); }
    }

    void UpdateBanner()
    {
        List<PeerSession> list;
        lock (_gate) list = _sessions.ToList();
        Ui(() =>
        {
            var incoming = list.Where(s => !s.Outgoing && !Assist.IsAssistIp(s.RemoteIp)).Select(s => s.RemoteName).Distinct().ToList();
            var helpers = list.Where(s => !s.Outgoing && Assist.IsAssistIp(s.RemoteIp)).Select(s => s.RemoteName).Distinct().ToList();
            var outgoing = Devices.Where(d => d.IsConnected).Select(d => d.Name).ToList();
            if (AssistDevice is { IsConnected: true } ad) outgoing.Add(ad.Name + "（协助）");
            var parts = new List<string>();
            if (outgoing.Count > 0) parts.Add($"正在远程 {string.Join("、", outgoing)}");
            if (incoming.Count > 0) parts.Add($"{string.Join("、", incoming)} 已连接到本机");
            if (helpers.Count > 0) parts.Add($"{string.Join("、", helpers)} 正在远程协助本机（在“协助”页可断开）");
            if (parts.Count > 0 && Config.ClipboardSync) parts.Add("剪贴板同步已开启");
            Banner = string.Join("  ·  ", parts);
        });
    }

    void OnMessage(PeerSession s, MsgType t, byte[] b)
    {
        switch (t)
        {
            case MsgType.Clipboard:
                Ui(() => _clip.ApplyRemote(b));
                break;
            case MsgType.FileBegin:
            case MsgType.FileChunk:
            case MsgType.FileEnd:
            case MsgType.FileAbort:
                _tx.Handle(s, t, b);
                break;
            case MsgType.PairRequest:
                _ = HandlePairRequestAsync(s, Frames.FromJson<PairRequestMsg>(b));
                break;
            case MsgType.PairResult:
                if (_pendingPairs.TryRemove(s, out var tcs)) tcs.TrySetResult(Frames.FromJson<PairResultMsg>(b));
                break;
            case MsgType.Privacy when b.Length == 1:
                Ui(() =>
                {
                    bool on = b[0] == 1 && Controllable(s);
                    FreeRemote.PrivacyScreen.Set(on);
                    _privacyOwner = on ? s : null;
                    _ = s.SendAsync(MsgType.PrivacyState, new[] { (byte)(FreeRemote.PrivacyScreen.IsOn ? 1 : 0) });
                });
                break;
            // ---- 光标同步：被控端 ----
            case MsgType.LocalCursor when b.Length == 1:
            {
                bool on = b[0] == 1 && Controllable(s);
                if (on) _cursorHost.Start(s);
                else if (_cursorHost.Target == s) _cursorHost.Stop();
                bool need = _cursorHost.NeedToggleFor(on); // Sunshine 画面里要不要画光标
                Log.Info($"本地光标 {(on ? "开" : "关")}（来自 {s.RemoteName}），需切换 Sunshine 光标：{need}");
                _ = s.SendAsync(MsgType.CursorMode, new[] { (byte)(need ? 1 : 0) });
                break;
            }
            // ---- 光标同步：控制端 ----
            case MsgType.CursorMode when b.Length == 1:
                // 经 Moonlight 发给 Sunshine：切换画面里的光标。先确保 Moonlight 在前台，按键才会送到
                if (b[0] == 1) Ui(async () =>
                {
                    if (_streams.TryGetValue(s.RemoteIp, out var mp) && !mp.HasExited)
                    {
                        var w = FreeRemote.Overlay.FindStreamWindow((uint)mp.Id);
                        if (w != IntPtr.Zero) { FreeRemote.Overlay.Focus(w); await Task.Delay(150); }
                    }
                    await FreeRemote.Overlay.SendMoonlightHotkeyAsync('N');
                });
                break;
            case MsgType.CursorShape: Ui(() => CursorFor(s)?.OnShape(b)); break;
            case MsgType.CursorRef: Ui(() => CursorFor(s)?.OnRef(b)); break;
            case MsgType.CursorHidden: Ui(() => CursorFor(s)?.OnHidden()); break;
            case MsgType.CursorPos: Ui(() => CursorFor(s)?.OnPos(b)); break;

            // ---- 文件拖出远程画面 ----
            case MsgType.DragOut:
            {
                var m = Frames.FromJson<DragOutMsg>(b);
                Ui(() => MatchDragOut(s, m, null));
                break;
            }
            case MsgType.DragRelease:
                // 被控端：控制端已松手。稍等 Moonlight 可能转来的“松开”，左键还按着就替它松开，文件落到屏幕边缘接收条上
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    if (!Controllable(s) || !FreeRemote.Win.LeftDown) return;
                    Log.Info("控制端已松手但本机左键仍按着，自动松开");
                    FreeRemote.Win.LeftUp();
                });
                break;
            case MsgType.DragPull:
            {
                var m = Frames.FromJson<DragPullMsg>(b);
                if (_dragOut.TryRemove(m.Token, out var paths)) _ = _tx.SendAsync(s, paths, false, token: m.Token);
                break;
            }

            // ---- 设备详情 / 终端 / 快速启动（被控端）----
            case MsgType.SysInfoReq:
                _ = Task.Run(async () =>
                {
                    if (!Trusted(s))
                    {
                        Log.Info($"{s.RemoteName} 请求设备信息，但本机关闭了“允许被远程”");
                        try { await s.SendJsonAsync(MsgType.SysInfo, new { Denied = true, Name = Config.DeviceName }); } catch { }
                        return;
                    }
                    try {
                    var info = RemoteTools.SysInfo(Config, CpuFrac, MyLan().LanIp ?? "", b.Length > 0 && b[0] == 1);
                    var pr = _probe;
                    if (pr != null)
                        info = info with
                        {
                            PublicIps = pr.Ips.Select(x => x.Ip).ToArray(), PublicIpCounts = pr.Ips.Select(x => x.Count).ToArray(),
                            PerFlow = pr.PerFlow, IpRule = Config.EffectiveIpRule, SuggestedRule = pr.SuggestedRule,
                            NetAdvice = PublicIpInfo,
                        };
                    try { await s.SendJsonAsync(MsgType.SysInfo, info); } catch { }
                    } catch (Exception e) { Log.Error("生成设备信息失败", e); try { await s.SendJsonAsync(MsgType.SysInfo, new { Error = e.Message, Name = Config.DeviceName }); } catch { } }
                });
                break;
            case MsgType.TermOpen:
                if (Trusted(s)) _ = s.SendJsonAsync(MsgType.TermOpened, RemoteTools.OpenTerminal(s));
                break;
            case MsgType.TermInput:
                if (Trusted(s)) { var m = Frames.FromJson<TermInputMsg>(b); RemoteTools.Input(m.Id, m.Line); }
                break;
            case MsgType.TermInterrupt:
            {
                var r = Trusted(s) ? RemoteTools.Interrupt(s, Frames.FromJson<TermCloseMsg>(b).Id) : null;
                if (r != null) _ = s.SendJsonAsync(MsgType.TermOpened, r);
                break;
            }
            case MsgType.TermClose:
                RemoteTools.Close(Frames.FromJson<TermCloseMsg>(b).Id);
                break;
            case MsgType.AppListReq:
                _ = Task.Run(async () =>
                {
                    var r = Trusted(s) ? RemoteTools.ListApps() : new AppListMsg(Array.Empty<AppEntry>());
                    try { await s.SendJsonAsync(MsgType.AppList, r); } catch { }
                });
                break;
            case MsgType.Goodbye:
                // 对方退出了 Farloop（组网也停了）：立刻标为离线，不等组网路由超时
                Log.Info($"{s.RemoteName} 已退出 Farloop");
                _gone[s.RemoteIp] = DateTime.UtcNow;
                Ui(() => { var d = Devices.FirstOrDefault(x => x.Ip == s.RemoteIp); if (d != null) { d.Online = false; if (d.IsMobile) Devices.Remove(d); } });
                break;
            case MsgType.SetIpRule:
                if (Trusted(s)) { var rule = Frames.FromJson<SetIpRuleMsg>(b).Rule ?? ""; _ = Task.Run(() => SetIpRuleAsync(rule)); }
                break;
            case MsgType.LaunchApp:
            {
                var (ok, msg) = Trusted(s) ? RemoteTools.Launch(Frames.FromJson<LaunchAppMsg>(b).Path) : (false, "对方没有开启“允许被远程”");
                _ = s.SendJsonAsync(MsgType.LaunchResult, new FsOpResultMsg(ok, msg));
                break;
            }
            // ---- 控制端：收到对方的设备详情 / 终端输出 / 应用列表 ----
            case MsgType.SysInfo:
            case MsgType.TermOpened:
            case MsgType.TermOutput:
            case MsgType.AppList:
            case MsgType.LaunchResult:
                Ui(() => RemoteMessage?.Invoke(s.RemoteIp, t, b));
                break;

            // ---- 文件管理（手机端双栏文件管理器）----
            case MsgType.FsList:
                _ = Task.Run(async () =>
                {
                    var r = Trusted(s) ? FileService.List(Frames.FromJson<FsListMsg>(b).Path)
                                                : new FsListResultMsg("", null, Array.Empty<FsEntry>(), "对方没有开启“允许被远程”");
                    try { await s.SendJsonAsync(MsgType.FsListResult, r); } catch { }
                });
                break;
            case MsgType.FsOp:
                _ = Task.Run(async () =>
                {
                    var m = Frames.FromJson<FsOpMsg>(b);
                    var r = Trusted(s) ? FileService.Op(m) : new FsOpResultMsg(false, "对方没有开启“允许被远程”");
                    Log.Info($"文件管理：{m.Op} {m.Path} {m.NewName} → {r.Message}");
                    try { await s.SendJsonAsync(MsgType.FsOpResult, r); } catch { }
                });
                break;
            case MsgType.FsPull:
            {
                var m = Frames.FromJson<FsPullMsg>(b);
                if (Trusted(s) && m.Paths.Length > 0)
                {
                    Log.Info($"文件管理：{s.RemoteName} 拉取 {m.Paths.Length} 项");
                    _ = _tx.SendAsync(s, m.Paths, false, token: m.Token);
                }
                break;
            }

            // ---- 多显示器 ----
            case MsgType.SetDisplay:
                _ = Task.Run(async () =>
                {
                    var m = Frames.FromJson<SetDisplayMsg>(b);
                    var r = Controllable(s) ? await HostDisplays.SetAsync(m.Id) : (false, "对方设置了不允许被远程控制");
                    try
                    {
                        await s.SendJsonAsync(MsgType.DisplayResult, new DisplayResultMsg(r.Item1, r.Item2));
                        await s.SendJsonAsync(MsgType.DeviceInfo, SelfInfo()); // 告诉对方新的“当前屏幕”
                    }
                    catch { }
                    Ui(() => { if (_edgeOn) { Drag?.Edge(false); Drag?.Edge(true); } }); // 边缘接收条移到新屏幕
                });
                break;
            case MsgType.DisplayLive:
                if (Controllable(s))
                {
                    HostDisplays.Remember(Frames.FromJson<SetDisplayMsg>(b).Id);
                    _ = s.SendJsonAsync(MsgType.DeviceInfo, SelfInfo());
                    Ui(() => { if (_edgeOn) { Drag?.Edge(false); Drag?.Edge(true); } });
                }
                break;
            case MsgType.DisplayResult:
                if (_pendingDisplay.TryRemove(s.RemoteIp, out var dt)) dt.TrySetResult(Frames.FromJson<DisplayResultMsg>(b));
                break;

            case MsgType.DeviceInfo:
                StoreDevice(Frames.FromJson<DeviceInfoMsg>(b), s.RemoteIp);
                break;

            case MsgType.PrivacyState when b.Length == 1:
                Ui(() => { var d = Devices.FirstOrDefault(x => x.Ip == s.RemoteIp); if (d != null) d.PrivacyOn = b[0] == 1; });
                break;
        }
    }

    // ---------- 被控端：收到配对请求，把 PIN 交给 Sunshine ----------
    async Task HandlePairRequestAsync(PeerSession s, PairRequestMsg m)
    {
        PairResultMsg res;
        if (Assist.IsAssistIp(s.RemoteIp))
        {
            res = await HandleAssistPairAsync(s, m);
            Log.Info($"远程协助请求来自 {m.ClientName}: {res.Ok} {res.Message}");
            try { await s.SendJsonAsync(MsgType.PairResult, res); } catch { }
            return;
        }
        if (!Config.AllowControl) res = new(false, "对方设置了不允许被远程控制");
        else
        {
            await Sunshine.EnsureRunningAsync();
            var (ok, msg) = await Sunshine.SubmitPinAsync(Config, m.Pin, "Farloop-" + m.ClientName, s.RemoteIp, _cts.Token);
            res = new(ok, msg);
        }
        Log.Info($"配对请求来自 {s.RemoteName}: {res.Ok} {res.Message}");
        try { await s.SendJsonAsync(MsgType.PairResult, res); } catch { }
    }

    // ---------- 控制端：连接（自动配对 + 打开串流） ----------
    public async Task ConnectAsync(DeviceVM d)
    {
        if (d.IsBusy) return;
        if (!Moonlight.Installed) { ShowToast("本机没有安装 Moonlight，无法远程控制。请在设置里勾选“控制别人”后重新安装。"); return; }
        try
        {
            d.Busy = "正在连接…";
            var s = await GetSessionAsync(d.Ip);

            bool assist = Assist.IsAssistIp(d.Ip);
            if (assist || !Config.PairedHosts.Contains(d.Ip))
            {
                d.Busy = assist ? "等待对方点“允许”…" : "首次连接，正在自动配对…";
                var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4");
                var tcs = new TaskCompletionSource<PairResultMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
                _pendingPairs[s] = tcs;
                var pairProc = Moonlight.StartPair(d.Ip, pin);
                await Task.Delay(1500);
                await s.SendJsonAsync(MsgType.PairRequest, new PairRequestMsg(pin, Config.DeviceName));
                PairResultMsg res;
                try { res = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(45)); }
                catch (TimeoutException) { res = new(false, "等待超时"); }
                if (!res.Ok)
                {
                    try { if (!pairProc.HasExited) pairProc.Kill(); } catch { }
                    ShowToast(assist ? $"没有连上：{res.Message}" : $"自动配对失败：{res.Message}。可在被控端打开 https://localhost:47990/pin 手动输入 PIN {pin}");
                    return;
                }
                // 实测：配对成功后 moonlight pair 进程不会自己退出，稍等它写完证书后关掉
                try { await pairProc.WaitForExitAsync(new CancellationTokenSource(3_000).Token); } catch { }
                try { if (!pairProc.HasExited) pairProc.Kill(); } catch { }
                if (!assist) { Config.PairedHosts.Add(d.Ip); Config.Save(); }
            }

            d.Busy = "正在打开画面…";
            var active = Config.Stream.Clone();
            var p = Moonlight.StartStream(d.Ip, active);
            _streams[d.Ip] = p;
            d.IsConnected = true;
            _overlays[d.Ip] = new FreeRemote.Overlay(d, p, active, active.InfoCard);
            _ = StartLocalCursorAsync(d, p, active);
            UpdateBanner();
            WatchExit(d, p);
        }
        catch (Exception e)
        {
            Log.Error("连接失败", e);
            ShowToast($"连接 {d.Name} 失败：{Friendly(e)}");
        }
        finally { d.Busy = ""; }
    }

    readonly Dictionary<string, FreeRemote.Overlay> _overlays = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<DisplayResultMsg>> _pendingDisplay = new();

    /// <summary>
    /// 连接方式切换：仅中转 / 自动（P2P 优先）。改的是本机 EasyTier 的启动参数（--disable-p2p），
    /// 需要重启组网（约 5–10 秒），之后画面自动重连。
    /// </summary>
    public async Task SetForceRelayAsync(DeviceVM d, bool relay)
    {
        if (Config.ForceRelay == relay || d.IsBusy) return;
        Config.ForceRelay = relay;
        Config.Save();
        Log.Info("连接方式切换为：" + (relay ? "仅中转" : "自动（P2P 优先）"));
        bool streaming = _streams.ContainsKey(d.Ip);
        if (streaming) _restarting.Add(d.Ip); // 组网重启期间 Moonlight 会断开，不算结束远程
        try
        {
            d.Busy = "正在切换连接方式…";
            ShowToast($"正在切换为{(relay ? "仅中转" : "P2P 优先")}，网络会重连约 10 秒");
            await Installer.RegisterEasyTierAsync(Config);
            // 等对方重新出现在组网里
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(1000);
                var peers = await EasyTier.GetPeersAsync();
                var p = peers.FirstOrDefault(x => x.Ip == d.Ip);
                if (p != null && p.LatencyMs != null && (relay ? !p.IsP2P : true)) break;
            }
        }
        catch (Exception e)
        {
            Log.Error("切换连接方式失败", e);
            ShowToast("切换连接方式失败：" + Friendly(e));
        }
        finally
        {
            d.Busy = "";
            if (streaming) _restarting.Remove(d.Ip);
        }
        if (streaming) await RestartStreamAsync(d);
    }

    /// <summary>控制端：让被控端换一块屏幕串流（对方重启 Sunshine，约 5 秒），然后重连画面</summary>
    public async Task SwitchDisplayAsync(DeviceVM d, HostDisplay disp)
    {
        if (d.IsBusy) return;
        try
        {
            d.Busy = "正在切换屏幕…";
            ShowToast($"正在切换到 {d.Name} 的「{disp.Name}」，约 5 秒");
            var s = await GetSessionAsync(d.Ip);
            var tcs = new TaskCompletionSource<DisplayResultMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingDisplay[d.Ip] = tcs;
            // 对方重启 Sunshine 时 Moonlight 会断开退出，不能当成“结束远程”
            _restarting.Add(d.Ip);
            DisplayResultMsg r;
            try
            {
                await s.SendJsonAsync(MsgType.SetDisplay, new SetDisplayMsg(disp.Id));
                try { r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
                catch (TimeoutException) { r = new(false, "对方没有响应"); }
            }
            finally { _restarting.Remove(d.Ip); }
            if (!r.Ok) { ShowToast("切换屏幕失败：" + r.Message); return; }
            d.CurrentDisplay = disp.Primary ? "" : disp.Id;
            d.Busy = "";
            if (_streams.ContainsKey(d.Ip)) await RestartStreamAsync(d); // Sunshine 已重启，画面必须重连
            else await ConnectAsync(d);
        }
        catch (Exception e)
        {
            Log.Error("切换屏幕失败", e);
            ShowToast("切换屏幕失败：" + Friendly(e));
        }
        finally { d.Busy = ""; }
    }

    void WatchExit(DeviceVM d, Process p)
    {
        p.Exited += (_, _) => Ui(() =>
        {
            // 快速重连时旧进程退出，不算结束远程
            if (_restarting.Contains(d.Ip) || !_streams.TryGetValue(d.Ip, out var cur) || cur != p) return;
            _streams.TryRemove(d.Ip, out Process? _);
            CloseOverlay(d.Ip);
            StopLocalCursor(d.Ip);
            d.IsConnected = false;
            // 画面关闭 = 结束远程，同时断开文件/剪贴板会话（有传输在进行时保留）
            if (!Transfers.Any(t => t.Outgoing && !t.Finished)) OutgoingTo(d.Ip)?.Close();
            UpdateBanner();
            if (Assist.IsAssistIp(d.Ip)) EndAssistHelp("协助已结束");
        });
    }

    void CloseOverlay(string ip)
    {
        if (_overlays.Remove(ip, out var o)) o.Close();
    }

    /// <summary>
    /// 控制中心“立即应用”：Moonlight 不支持串流中改分辨率/帧率/码率，所以关掉重开。
    /// 被控端的 Desktop 会话一直在，新连接直接续上，约 2 秒。
    /// </summary>
    public async Task RestartStreamAsync(DeviceVM d)
    {
        if (!_streams.TryGetValue(d.Ip, out var old) || !_restarting.Add(d.Ip)) return;
        FreeRemote.FreezeFrame? freeze = null;
        try
        {
            var active = Config.Stream.Clone();
            _overlays.TryGetValue(d.Ip, out var o);

            // 1. 记住窗口状态并冻结当前画面
            var snap = o?.TakeSnapshot();
            if (snap != null)
            {
                // 沿用当前的显示方式（用户可能用快捷键切过全屏/窗口）
                active.DisplayMode = snap.Windowed ? "windowed" : active.DisplayMode == "fullscreen" ? "fullscreen" : "borderless";
                freeze = FreeRemote.FreezeFrame.Show(snap, $"正在切换到 {active.ResolutionLabel} · {active.Fps} FPS · {active.CodecLabel}");
            }

            // 2. 底下重连
            try { old.Kill(); await old.WaitForExitAsync(new CancellationTokenSource(5000).Token); } catch { }
            var p = Moonlight.StartStream(d.Ip, active);
            _streams[d.Ip] = p;
            o?.Attach(p, active);
            WatchExit(d, p);

            // 3. 新画面窗口出现后放回原位置/大小，等首帧后淡出冻结画面
            var h = await FreeRemote.Overlay.WaitForStreamWindowAsync(p, 15_000);
            if (h != IntPtr.Zero && snap != null) FreeRemote.Overlay.RestorePlacement(h, snap);
            _ = StartLocalCursorAsync(d, p, active); // 新 Moonlight 进程要重新打开本地光标
            await Task.Delay(h != IntPtr.Zero ? 700 : 0);
            Log.Info($"已按新参数重连：{active.ResolutionLabel} {active.Fps}fps {active.Codec} {active.BitrateMbps}M");
        }
        catch (Exception e)
        {
            Log.Error("重连失败", e);
            ShowToast("应用失败：" + e.Message);
        }
        finally
        {
            _restarting.Remove(d.Ip);
            if (freeze != null) await freeze.FadeOutAsync();
        }
    }

    readonly HashSet<string> _restarting = new();

    public void SetInfoCard(DeviceVM d, bool on)
    {
        if (_overlays.TryGetValue(d.Ip, out var o)) o.SetCardVisible(on, Config.Stream);
    }

    // ================= 底部实时状态条：虚拟网络上下行、CPU、内存 =================
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct MEMSTAT { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MEMSTAT m);

    string _upText = "0.0 Mbps", _downText = "0.0 Mbps";
    double _upFrac, _downFrac, _cpuFrac, _memFrac;
    public string UpText { get => _upText; set => Set(ref _upText, value); }
    public string DownText { get => _downText; set => Set(ref _downText, value); }
    public double UpFrac { get => _upFrac; set => Set(ref _upFrac, value); }
    public double DownFrac { get => _downFrac; set => Set(ref _downFrac, value); }
    public double CpuFrac { get => _cpuFrac; set { if (Set(ref _cpuFrac, value)) Raise(nameof(CpuText)); } }
    public double MemFrac { get => _memFrac; set { if (Set(ref _memFrac, value)) Raise(nameof(MemText)); } }
    public string CpuText => $"{_cpuFrac * 100:0}%";
    public string MemText => $"{_memFrac * 100:0}%";

    System.Net.NetworkInformation.NetworkInterface? _vnic;
    long _lastRx = -1, _lastTx;
    long _lastIdle, _lastBusy;
    DateTime _lastStatAt;

    void StartStatusTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => UpdateStatus();
        t.Start();
    }

    DateTime _vnicLookedAt;

    void UpdateStatus()
    {
        // 三项各自 try：以前一个 try 包全部，虚拟网卡读取出错时 CPU、内存也跟着不再刷新
        var now = DateTime.UtcNow;
        try
        {
            // 虚拟网卡（EasyTier）的实时收发速率：远程画面、文件、剪贴板都走它。
            // 组网重启（改设置、退出后重开）时网卡会重建，旧对象就失效了：找不到 / 出错时每 3 秒重新找一次
            if (_vnic == null && (now - _vnicLookedAt).TotalSeconds >= 3)
            {
                _vnicLookedAt = now;
                _vnic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                        && n.GetIPProperties().UnicastAddresses.Any(a => a.Address.ToString() == Config.VirtualIp));
                _lastRx = -1;
            }
            if (_vnic != null)
            {
                var st = _vnic.GetIPStatistics();
                if (_lastRx >= 0 && st.BytesReceived >= _lastRx && st.BytesSent >= _lastTx)
                {
                    var secs = Math.Max((now - _lastStatAt).TotalSeconds, 0.2);
                    double up = (st.BytesSent - _lastTx) * 8 / secs / 1e6, down = (st.BytesReceived - _lastRx) * 8 / secs / 1e6;
                    UpText = $"{up:0.0} Mbps"; DownText = $"{down:0.0} Mbps";
                    UpFrac = Math.Min(1, up / 100); DownFrac = Math.Min(1, down / 100); // 以 100 Mbps 为满格
                }
                _lastRx = st.BytesReceived; _lastTx = st.BytesSent;
            }
            else { UpText = DownText = "0.0 Mbps"; UpFrac = DownFrac = 0; }
        }
        catch
        {
            _vnic = null; _lastRx = -1; // 网卡没了（组网重启），下次重新找
            UpText = DownText = "0.0 Mbps"; UpFrac = DownFrac = 0;
        }
        _lastStatAt = now;

        try
        {
            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                long busy = kernel - idle + user; // kernel 时间里包含了 idle
                if (_lastBusy > 0)
                {
                    long dBusy = busy - _lastBusy, dIdle = idle - _lastIdle;
                    if (dBusy + dIdle > 0) CpuFrac = Math.Clamp((double)dBusy / (dBusy + dIdle), 0, 1);
                }
                _lastBusy = busy; _lastIdle = idle;
            }
        }
        catch { }
        try
        {
            var m = new MEMSTAT { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMSTAT>() };
            if (GlobalMemoryStatusEx(ref m)) MemFrac = m.Load / 100.0;
        }
        catch { }
    }

    // ================= 远程协助（设备码 + 临时密码，见 Core/Assist.cs） =================
    /// <summary>被协助端：这个会话能不能看 / 操作屏幕（自己人看“允许被远程”，协助者看“允许别人协助我”）</summary>
    bool Controllable(PeerSession s) => Assist.IsAssistIp(s.RemoteIp) ? AssistHostActive : Config.AllowControl;
    /// <summary>只给自己人：终端、文件管理、启动程序、改打洞线路</summary>
    bool Trusted(PeerSession s) => Config.AllowControl && !Assist.IsAssistIp(s.RemoteIp);

    CancellationTokenSource? _assistListen;
    int _assistRetries;
    readonly List<string> _assistClients = new();   // 协助者在本机 Sunshine 里的配对名，结束后删除
    string _helpSecret = "";

    public bool AssistHostActive => Config.AssistEnabled && Config.AllowControl && Sunshine.Installed && Assist.IsRunning(Assist.Role.Host);
    public string AssistIdText => Assist.FormatId(Config.AssistId);
    public string AssistPassword => Config.AssistPassword;

    string _assistStatus = "";
    public string AssistStatus { get => _assistStatus; set => Set(ref _assistStatus, value); }
    string _assistHelpStatus = "";
    public string AssistHelpStatus { get => _assistHelpStatus; set => Set(ref _assistHelpStatus, value); }
    bool _assistHelping;
    public bool AssistHelping { get => _assistHelping; set => Set(ref _assistHelping, value); }
    bool _assistBeingHelped;
    public bool AssistBeingHelped { get => _assistBeingHelped; set => Set(ref _assistBeingHelped, value); }
    /// <summary>协助者：正在协助的那台电脑</summary>
    public DeviceVM? AssistDevice { get; private set; }

    void EnsureAssistIdentity()
    {
        bool changed = false;
        if (Config.AssistId.Length != 9) { Config.AssistId = Assist.NewId(); changed = true; }
        if (Config.AssistPassword.Length < 6) { Config.AssistPassword = Assist.NewPassword(); changed = true; }
        if (changed) Config.Save();
        Raise(nameof(AssistIdText)); Raise(nameof(AssistPassword));
    }

    /// <summary>被协助端：按设置开启 / 关闭（开机、改设置、换密码后调用）</summary>
    public void StartAssistHost()
    {
        if (!Config.IsConfigured) return;
        EnsureAssistIdentity();
        if (!Config.AssistEnabled) { StopAssistHost(); AssistStatus = "已关闭：别人不能用设备码连接本机"; return; }
        if (!Config.AllowControl || !Sunshine.Installed) { StopAssistHost(); AssistStatus = "需要先开启“允许被远程”（会安装 Sunshine）"; return; }
        try
        {
            Assist.Start(Assist.Role.Host, Config, Config.AssistId, Config.AssistPassword, Config.DeviceName);
            if (_assistListen == null)
            {
                _assistListen = new CancellationTokenSource();
                var ct = _assistListen.Token;
                _ = Task.Run(() => ListenLoop(() => Assist.HostIp, () => Assist.Secret(Config.AssistId, Config.AssistPassword), ct));
                _ = Task.Run(async () => { try { await Installer.ConfigureAssistFirewallAsync(); } catch (Exception e) { Log.Error("协助防火墙规则失败", e); } });
            }
            AssistStatus = "等待对方连接。把设备码和密码告诉对方，对方在 Farloop 的“远程协助”里输入即可";
        }
        catch (Exception e)
        {
            Log.Error("启动远程协助失败", e);
            AssistStatus = "启动失败：" + e.Message + "（稍后自动重试）";
            // 常见原因：刚启动时组网组件正在更新，文件被占用。15 秒后再试，最多 3 次
            if (_assistRetries++ < 3) _ = Task.Run(async () => { await Task.Delay(15_000); Ui(StartAssistHost); });
            return;
        }
        _assistRetries = 0;
        Raise(nameof(AssistHostActive));
    }

    public void StopAssistHost()
    {
        _assistListen?.Cancel();
        _assistListen = null;
        Assist.Stop(Assist.Role.Host);
        CloseAssistSessions();
        Raise(nameof(AssistHostActive));
    }

    void CloseAssistSessions()
    {
        List<PeerSession> l;
        lock (_gate) l = _sessions.Where(x => !x.Outgoing && Assist.IsAssistIp(x.RemoteIp)).ToList();
        foreach (var x in l) x.Close();
    }

    public void SetAssistEnabled(bool on)
    {
        Config.AssistEnabled = on;
        Config.Save();
        StartAssistHost();
    }

    /// <summary>换一个临时密码：知道旧密码的人马上断开、再也进不来</summary>
    public void RefreshAssistPassword()
    {
        Config.AssistPassword = Assist.NewPassword();
        Config.Save();
        Raise(nameof(AssistPassword));
        CloseAssistSessions();
        if (Config.AssistEnabled) StartAssistHost();
    }

    /// <summary>被协助端：本机用户确认（最多等 40 秒，不点算拒绝）</summary>
    Task<bool> ConfirmAssistAsync(string who)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(() =>
        {
            try { tcs.TrySetResult(FreeRemote.AssistConfirm.Ask(who, TimeSpan.FromSeconds(40))); }
            catch (Exception e) { Log.Error("协助确认框出错", e); tcs.TrySetResult(false); }
        });
        return tcs.Task;
    }

    async Task<PairResultMsg> HandleAssistPairAsync(PeerSession s, PairRequestMsg m)
    {
        if (!AssistHostActive) return new(false, "对方关闭了远程协助");
        if (!await ConfirmAssistAsync(m.ClientName)) return new(false, "对方拒绝了协助请求（或没有在 40 秒内点“允许”）");
        await Sunshine.EnsureRunningAsync();
        var name = $"Farloop协助-{m.ClientName}-{RandomNumberGenerator.GetInt32(0, 0x10000):x4}";
        var (ok, msg) = await Sunshine.SubmitPinAsync(Config, m.Pin, name, s.RemoteIp, _cts.Token);
        if (ok)
        {
            lock (_assistClients) _assistClients.Add(name);
            AssistBeingHelped = true;
            AssistStatus = $"{m.ClientName} 正在远程协助本机";
        }
        return new(ok, ok ? "已允许" : msg);
    }

    /// <summary>被协助端：断开所有协助者（界面上的“断开”）</summary>
    public void DisconnectAssist()
    {
        RefreshAssistPassword(); // 换密码 = 网络密钥变了，对方的画面和连接立刻断开
        _ = Sunshine.CloseAppAsync(Config);
        _ = AfterAssistSessionAsync(0);
    }

    async Task AfterAssistSessionAsync(int delayMs = 8000)
    {
        await Task.Delay(delayMs); // 对方只是短暂重连的话不算结束
        bool still;
        lock (_gate) still = _sessions.Any(x => !x.Outgoing && Assist.IsAssistIp(x.RemoteIp) && x.IsOpen);
        if (still) return;
        List<string> names;
        lock (_assistClients) { names = _assistClients.ToList(); _assistClients.Clear(); }
        if (names.Count == 0 && !AssistBeingHelped) return;
        foreach (var n in names)
        {
            var (ok, msg) = await Sunshine.UnpairByNameAsync(Config, n);
            Log.Info($"协助结束，删除配对 {n}：{(ok ? "成功" : msg)}");
        }
        AssistBeingHelped = false;
        Ui(() =>
        {
            if (delayMs > 0) RefreshAssistPassword(); // 每次协助结束换密码
            AssistStatus = "协助已结束，密码已更换";
        });
    }

    /// <summary>协助者：凭对方的设备码和密码连过去</summary>
    public async Task AssistConnectAsync(string idText, string pwText)
    {
        var id = Assist.NormalizeId(idText);
        var pw = Assist.NormalizePassword(pwText);
        if (AssistHelping) return;
        if (id.Length != 9) { AssistHelpStatus = "设备码是 9 位数字"; return; }
        if (pw.Length < 4) { AssistHelpStatus = "请输入对方的临时密码"; return; }
        if (id == Config.AssistId) { AssistHelpStatus = "这是本机自己的设备码"; return; }
        if (!Moonlight.Installed) { AssistHelpStatus = "本机没有安装 Moonlight，不能控制别人（设置 → 重新设置时勾选“用这台电脑控制别人”）"; return; }
        AssistHelping = true;
        try
        {
            _helpSecret = Assist.Secret(id, pw);
            Assist.Start(Assist.Role.Helper, Config, id, pw, Config.DeviceName);
            PeerInfo? host = null;
            for (int i = 1; i <= 45 && host == null && AssistHelping; i++)
            {
                AssistHelpStatus = $"正在寻找对方电脑… {i} 秒";
                await Task.Delay(1000);
                host = (await Assist.PeersAsync(Assist.Role.Helper)).FirstOrDefault(p => p.Ip == Assist.HostIp && p.LatencyMs != null);
            }
            if (host == null)
            {
                EndAssistHelp(AssistHelping ? "连不上对方：请确认设备码和密码都对，并且对方电脑开着 Farloop、开启了“允许别人协助我”" : "已取消");
                return;
            }
            var d = new DeviceVM(Assist.HostIp) { Name = host.DisplayName == "" ? "对方电脑" : host.DisplayName, Online = true, IsP2P = host.IsP2P, Latency = host.LatencyMs };
            AssistDevice = d;
            AssistHelpStatus = $"找到了 {d.Name}（{(host.IsP2P ? "P2P 直连" : "中转")}），等对方点“允许”…";
            await ConnectAsync(d);
            if (d.IsConnected) AssistHelpStatus = $"正在协助 {d.Name}。关闭远程画面即结束协助";
            else EndAssistHelp("没有连上（对方拒绝了，或没有在 40 秒内点“允许”）");
        }
        catch (Exception e)
        {
            Log.Error("远程协助连接失败", e);
            EndAssistHelp("连接失败：" + Friendly(e));
        }
    }

    /// <summary>协助者：结束协助（关画面、退出临时组网）</summary>
    public void EndAssistHelp(string why)
    {
        var d = AssistDevice;
        if (d != null && _streams.TryRemove(d.Ip, out var p)) { try { if (!p.HasExited) p.Kill(); } catch { } CloseOverlay(d.Ip); StopLocalCursor(d.Ip); }
        if (d != null) OutgoingTo(d.Ip)?.Close();
        AssistDevice = null;
        Assist.Stop(Assist.Role.Helper);
        _helpSecret = "";
        AssistHelping = false;
        AssistHelpStatus = why;
        UpdateBanner();
    }

    // ================= 远程开机 =================
    readonly ConcurrentDictionary<string, DateTime> _infoInFlight = new();
    LanInfo? _myLan;
    DateTime _myLanAt;

    LanInfo MyLan()
    {
        if (_myLan == null || (DateTime.UtcNow - _myLanAt).TotalMinutes > 10) { _myLan = WakeOnLan.GetLocal(); _myLanAt = DateTime.UtcNow; }
        return _myLan;
    }

    DeviceInfoMsg SelfInfo()
    {
        var l = MyLan();
        HostDisplay[]? displays = null;
        string? current = null;
        if (Config.AllowControl && Sunshine.Installed)
        {
            try { displays = HostDisplays.List().ToArray(); current = HostDisplays.CurrentId(); } catch { }
        }
        return new DeviceInfoMsg(Config.DeviceName, Config.VirtualIp, l.Macs, l.Broadcast, l.GatewayMac, PeerSession.Version, displays, current,
            AdaptiveBitrate: Config.AllowControl && SunshineFarloop.Active);
    }

    KnownDevice? Known(string ip) { lock (Config.KnownDevices) return Config.KnownDevices.FirstOrDefault(k => k.Ip == ip); }

    bool NeedsInfo(string ip)
    {
        var k = Known(ip);
        if (k != null && k.Macs.Length > 0 && (DateTime.Now - k.LastSeen).TotalHours < 6) return false;
        return !_infoInFlight.TryGetValue(ip, out var t) || (DateTime.UtcNow - t).TotalMinutes > 2;
    }

    /// <summary>屏幕增减后（进入 / 退出息屏）把新的设备信息（含屏幕列表）发给连着本机的设备</summary>
    public void BroadcastSelfInfo()
    {
        List<PeerSession> l;
        lock (_gate) l = _sessions.Where(x => !x.Outgoing && x.IsOpen).ToList();
        if (l.Count == 0) return;
        var info = SelfInfo();
        foreach (var x in l) _ = x.SendJsonAsync(MsgType.DeviceInfo, info);
    }

    /// <summary>从列表里删掉一台设备（离线的；在线的下一轮刷新又会出现）</summary>
    public void RemoveDevice(DeviceVM d)
    {
        lock (Config.KnownDevices) Config.KnownDevices.RemoveAll(k => k.Ip == d.Ip);
        try { Config.Save(); } catch { }
        Devices.Remove(d);
        Log.Info($"移除设备 {d.Name} {d.Ip}");
    }

    void StoreDevice(DeviceInfoMsg m, string ip)
    {
        if (ip == Config.VirtualIp) return;
        var stale = new List<string>();
        lock (Config.KnownDevices)
        {
            // 同一台电脑换了虚拟 IP（重装、重新加入网络）：网卡 MAC 相同的旧记录删掉，免得列表里一个在线、一个永远离线
            if (m.Macs.Length > 0)
            {
                foreach (var o in Config.KnownDevices.Where(x => x.Ip != ip && x.Macs.Intersect(m.Macs, StringComparer.OrdinalIgnoreCase).Any()).ToList())
                {
                    stale.Add(o.Ip);
                    Config.KnownDevices.Remove(o);
                }
            }
            var k = Config.KnownDevices.FirstOrDefault(x => x.Ip == ip);
            if (k == null) Config.KnownDevices.Add(k = new KnownDevice { Ip = ip });
            k.Name = m.Name;
            if (m.Macs.Length > 0) k.Macs = m.Macs;
            k.Broadcast = m.Broadcast ?? k.Broadcast;
            k.GatewayMac = m.GatewayMac ?? k.GatewayMac;
            k.LastSeen = DateTime.Now;
        }
        try { Config.Save(); } catch { }
        Ui(() =>
        {
            foreach (var sip in stale)
            {
                var od = Devices.FirstOrDefault(x => x.Ip == sip);
                if (od != null && !od.Online && !od.IsConnected) { Devices.Remove(od); Log.Info($"{m.Name} 换了虚拟 IP（{sip} → {ip}），删掉旧记录"); }
            }
            var d = Devices.FirstOrDefault(x => x.Ip == ip);
            if (d == null) Devices.Add(d = new DeviceVM(ip));
            if (string.IsNullOrEmpty(d.Name) || d.Name == ip) d.Name = m.Name;
            d.CanWake = m.Macs.Length > 0;
            if (m.Displays != null) { d.Displays = m.Displays; d.CurrentDisplay = m.CurrentDisplay ?? ""; }
        });
        Log.Info($"设备信息：{m.Name} {ip} MAC={string.Join(",", m.Macs)} 网关={m.GatewayMac}");
    }

    /// <summary>启动时把见过的设备（含离线的）放进列表</summary>
    void LoadKnownDevices()
    {
        List<KnownDevice> list;
        lock (Config.KnownDevices)
        {
            // 0.10.1 及以前的 bug 留下的 “::ffff:10.x.x.x” 记录，删掉（正常的那条会自己重新记录）
            if (Config.KnownDevices.RemoveAll(k => k.Ip.Contains(':')) > 0) { try { Config.Save(); } catch { } }
            list = Config.KnownDevices.ToList();
        }
        foreach (var k in list)
        {
            if (k.Ip == Config.VirtualIp || Devices.Any(d => d.Ip == k.Ip)) continue;
            Devices.Add(new DeviceVM(k.Ip) { Name = k.Name, Online = false, CanWake = k.Macs.Length > 0 });
        }
    }

    /// <summary>用一次性短连接问对方的网卡信息（不算远程会话）</summary>
    async Task RefreshDeviceInfoAsync(string ip)
    {
        _infoInFlight[ip] = DateTime.UtcNow;
        try
        {
            var s = await PeerSession.ConnectAsync(ip, Config.DeviceName, Config.VirtualIp, Config.NetworkSecret, _cts.Token, "info");
            var tcs = new TaskCompletionSource<DeviceInfoMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
            s.Message += (_, t, b) => { if (t == MsgType.DeviceInfo) tcs.TrySetResult(Frames.FromJson<DeviceInfoMsg>(b)); };
            s.Closed += _ => tcs.TrySetCanceled();
            s.Start();
            await s.SendJsonAsync(MsgType.DeviceInfo, SelfInfo());
            var info = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8));
            StoreDevice(info, ip);
            s.Close();
        }
        catch (Exception e) when (e is not OperationCanceledException) { Log.Info($"获取 {ip} 设备信息失败：{e.Message}"); }
        catch (OperationCanceledException) { }
    }

    /// <summary>对方发来的一次性服务连接：交换设备信息，或代发开机信号</summary>
    void HandleServiceSession(PeerSession s)
    {
        s.Message += async (sender, t, b) =>
        {
            try
            {
                if (t == MsgType.DeviceInfo)
                {
                    StoreDevice(Frames.FromJson<DeviceInfoMsg>(b), s.RemoteIp);
                    await s.SendJsonAsync(MsgType.DeviceInfo, SelfInfo());
                }
                else if (t == MsgType.WakeRequest && !Assist.IsAssistIp(s.RemoteIp))
                {
                    var m = Frames.FromJson<WakeRequestMsg>(b);
                    Log.Info($"{s.RemoteName} 请本机代发开机信号给 {m.TargetName}");
                    int n = await WakeOnLan.SendAsync(m.Macs, m.Broadcast);
                    await s.SendJsonAsync(MsgType.WakeResult, new WakeResultMsg(n > 0, n > 0 ? $"已由 {Config.DeviceName} 在局域网内发出开机信号" : "发送失败"));
                }
            }
            catch (Exception e) { Log.Error("处理服务连接失败", e); }
            finally { _ = Task.Delay(1000).ContinueWith(_ => s.Close()); }
        };
        s.Start();
        _ = Task.Delay(20_000).ContinueWith(_ => s.Close()); // 兜底
    }

    /// <summary>
    /// 远程开机：找一台和目标在同一局域网（路由器 MAC 相同）且在线的设备代发魔术包；本机就在那个局域网则直接发。
    /// </summary>
    public async Task WakeAsync(DeviceVM d)
    {
        var k = Known(d.Ip);
        if (k == null || k.Macs.Length == 0)
        {
            ShowToast($"还不知道 {d.Name} 的网卡地址。它需要先开着 Farloop 在线一次（几分钟内会自动交换信息），之后才能远程开机");
            return;
        }
        if (d.IsBusy) return;
        try
        {
            d.Busy = "正在发送开机信号…";
            string via;
            var me = MyLan();
            if (k.GatewayMac != null && me.GatewayMac == k.GatewayMac)
            {
                await WakeOnLan.SendAsync(k.Macs, k.Broadcast);
                via = "本机";
            }
            else
            {
                var relay = Devices.Where(x => x.Online && x.Ip != d.Ip)
                                   .Select(x => (Vm: x, K: Known(x.Ip)))
                                   .FirstOrDefault(x => x.K != null && k.GatewayMac != null && x.K.GatewayMac == k.GatewayMac);
                if (relay.Vm == null)
                {
                    ShowToast($"{d.Name} 所在的局域网里没有在线的 Farloop 设备可以帮忙开机。" +
                              "开机信号只能在同一局域网内发出：在那边放一台常开的电脑/小主机装上 Farloop 即可，和路由器品牌无关");
                    return;
                }
                var s = await PeerSession.ConnectAsync(relay.Vm.Ip, Config.DeviceName, Config.VirtualIp, Config.NetworkSecret, _cts.Token, "wake");
                var tcs = new TaskCompletionSource<WakeResultMsg>(TaskCreationOptions.RunContinuationsAsynchronously);
                s.Message += (_, t, b) => { if (t == MsgType.WakeResult) tcs.TrySetResult(Frames.FromJson<WakeResultMsg>(b)); };
                s.Closed += _ => tcs.TrySetCanceled();
                s.Start();
                await s.SendJsonAsync(MsgType.WakeRequest, new WakeRequestMsg(k.Macs, k.Broadcast, d.Name));
                var r = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
                s.Close();
                if (!r.Ok) { ShowToast("开机失败：" + r.Message); return; }
                via = relay.Vm.Name;
            }

            // 等它上线（开机 + 登录前组网服务就会启动）
            d.Busy = $"已通过 {via} 发出开机信号，等待上线…";
            for (int i = 0; i < 120 && !d.Online; i++) await Task.Delay(1000);
            ShowToast(d.Online ? $"{d.Name} 已上线" :
                $"{d.Name} 两分钟内没有上线。请确认它开启了远程开机（设置 → 远程开机），以及 BIOS 里打开了“网络唤醒 / Wake on LAN”");
        }
        catch (Exception e)
        {
            Log.Error("远程开机失败", e);
            ShowToast("远程开机失败：" + Friendly(e));
        }
        finally { d.Busy = ""; }
    }

    // ================= 组件更新 =================
    public ObservableCollection<ComponentVM> Components { get; } = new();

    bool _hasUpdates;
    public bool HasUpdates { get => _hasUpdates; set => Set(ref _hasUpdates, value); }

    bool _checking;
    public bool CheckingUpdates { get => _checking; set => Set(ref _checking, value); }

    string _updateCheckedText = "";
    public string UpdateCheckedText { get => _updateCheckedText; set => Set(ref _updateCheckedText, value); }

    DispatcherTimer? _dailyCheck;
    public bool IsStreaming => !_streams.IsEmpty;

    SelfUpdate.Release? _farloopRelease;

    void EnsureComponents()
    {
        void Add(string key, string name, string role) { if (Components.All(c => c.Key != key)) Components.Add(new ComponentVM(key, name, role)); }
        if (Components.All(c => c.Key != "farloop")) Components.Insert(0, new ComponentVM("farloop", "Farloop", "本程序（从 GitHub 更新：" + SelfUpdate.Repo + "）"));
        if (Config.AllowControl) Add(Updater.SunshineId, "Sunshine", "被远程：画面采集与编码");
        if (Config.CanControl) Add(Updater.MoonlightId, "Moonlight", "控制别人：解码与显示");
        Add("easytier", "EasyTier", "组网与打洞（Farloop 内置打洞加速版，随 Farloop 一起更新）");
    }

    void StartUpdateSchedule()
    {
        if (_dailyCheck != null) return;
        // 启动 1 分钟后查一次，之后每天一次（只提示，不自动安装）
        _ = Task.Run(async () => { await Task.Delay(60_000); Ui(() => _ = CheckUpdatesAsync(silent: true)); });
        _dailyCheck = new DispatcherTimer { Interval = TimeSpan.FromHours(24) };
        _dailyCheck.Tick += (_, _) => _ = CheckUpdatesAsync(silent: true);
        _dailyCheck.Start();
    }

    public async Task CheckUpdatesAsync(bool silent = false)
    {
        if (CheckingUpdates) return;
        CheckingUpdates = true;
        EnsureComponents();
        try
        {
            foreach (var c in Components)
            {
                c.Status = "检查中…";
                try
                {
                    if (c.Key == "farloop")
                    {
                        _farloopRelease = await SelfUpdate.LatestAsync();
                        c.Installed = PeerSession.Version;
                        c.Latest = _farloopRelease?.Version ?? PeerSession.Version;
                    }
                    else if (c.Key == "easytier")
                    {
                        // 内置的打洞加速版不走官方更新（官方版会把加速改动覆盖掉），随 FreeRemote 一起升级
                        c.Installed = await Updater.EasyTierInstalledAsync();
                        c.Latest = c.Installed;
                    }
                    else if (c.Key == Updater.SunshineId && SunshineFarloop.Active)
                    {
                        // 定制版（码率自适应）随 Farloop 一起更新：官方版会把它覆盖掉
                        c.Installed = SunshineFarloop.BaseVersion + "（Farloop 定制版）";
                        c.Latest = c.Installed;
                    }
                    else
                    {
                        var (inst, avail) = await Updater.WingetQueryAsync(c.Key);
                        c.Installed = inst;
                        c.Latest = avail ?? inst;
                    }
                    c.Status = "";
                }
                catch (Exception e)
                {
                    Log.Error($"检查 {c.Name} 更新失败", e);
                    c.Status = "检查失败（网络问题？）";
                }
            }
            HasUpdates = Components.Any(c => c.HasUpdate);
            UpdateCheckedText = $"上次检查：{DateTime.Now:HH:mm}";
            if (HasUpdates && silent)
                ShowToast($"有 {Components.Count(c => c.HasUpdate)} 个组件可以更新，去 设置 → 组件更新 查看");
        }
        finally { CheckingUpdates = false; }
    }

    /// <summary>返回 null 表示可以更新；否则是不能更新的原因</summary>
    public string? UpdateBlocker(ComponentVM c) =>
        c.Key == Updater.MoonlightId && IsStreaming ? "正在远程别人，结束远程后再更新 Moonlight" : null;

    /// <summary>更新会打断当前连接时，给出提醒文字</summary>
    public string? UpdateWarning(ComponentVM c) => c.Key switch
    {
        Updater.SunshineId when IsBeingControlled => "本机正在被远程，更新 Sunshine 会中断对方的画面（更新完自动恢复，对方重新连接即可）。继续吗？",
        "easytier" when IsBeingControlled || IsStreaming => "更新 EasyTier 会让组网断开几秒，当前的远程会中断。继续吗？",
        _ => null,
    };

    public async Task UpdateAsync(ComponentVM c)
    {
        if (c.Busy || !c.HasUpdate) return;
        c.Busy = true;
        try
        {
            (bool Ok, string Message) r;
            if (c.Key == "farloop")
            {
                var rel = _farloopRelease ?? await SelfUpdate.LatestAsync();
                if (rel == null || !SelfUpdate.IsNewer(rel)) { r = (false, "已是最新版本"); }
                else
                {
                    var err = await SelfUpdate.InstallAsync(rel, new Progress<string>(m => c.Status = m));
                    if (err == null) { Ui(() => ((App)System.Windows.Application.Current).ExitForUpdate()); return; }
                    r = (false, err);
                }
            }
            else if (c.Key == "easytier")
            {
                // 内置的是 Farloop 打洞加速版：绝不用官方版覆盖（会丢掉加速、线路规则等改动），只随 Farloop 一起升级
                r = (false, "EasyTier 随 Farloop 一起更新，请升级 Farloop");
            }
            else
            {
                c.Status = "正在下载并安装…";
                r = await Updater.WingetUpgradeAsync(c.Key);
                var (inst, avail) = await Updater.WingetQueryAsync(c.Key);
                c.Installed = inst;
                c.Latest = avail ?? inst;
                if (c.Key == Updater.SunshineId)
                {
                    // 安装程序会重启服务；配置（账号、画质、HDR 切换）保留，这里再确认一次
                    await Sunshine.ApplyTuningAsync();
                    await Sunshine.EnsureRunningAsync();
                }
            }
            c.Status = "";
            ShowToast($"{c.Name}：{r.Message}");
        }
        catch (Exception e)
        {
            Log.Error($"更新 {c.Name} 失败", e);
            ShowToast($"{c.Name} 更新失败：{e.Message}");
        }
        finally
        {
            c.Busy = false;
            HasUpdates = Components.Any(x => x.HasUpdate);
        }
    }

    // ================= 文件拖拽 =================
    readonly ConcurrentDictionary<string, string[]> _dragOut = new();   // 被控端：令牌 → 被拖的文件
    readonly ConcurrentDictionary<string, string> _dragDest = new();    // 控制端：令牌 → 本地落点文件夹
    FreeRemote.EdgeCatcher? _edge;
    FreeRemote.DragGhost? _ghost;

    /// <summary>控制端：文件拖进远程画面并松开</summary>
    public async Task DropToRemoteAsync(DeviceVM d, string[] paths, float nx, float ny)
    {
        try
        {
            var s = await GetSessionAsync(d.Ip);
            await _tx.SendAsync(s, paths, false, nx, ny);
        }
        catch (Exception e)
        {
            Log.Error("拖放发送失败", e);
            ShowToast($"发送到 {d.Name} 失败：{Friendly(e)}");
        }
    }

    // 拖出远程画面：本地松手（得到落点文件夹）和 被控端收到放在屏幕边缘的文件（得到文件列表）几乎同时发生，
    // 谁先到谁等另一个，8 秒内配上就开始传
    (string Ip, string Folder, DateTime At)? _dragOutTarget;
    (PeerSession S, DragOutMsg M, DateTime At)? _dragOutFiles;

    /// <summary>控制端：从远程画面拖到本地并松开了</summary>
    public void SetDragOutTarget(DeviceVM d, string? folder)
    {
        if (folder == null) { _dragOutTarget = null; return; }
        _dragOutTarget = (d.Ip, folder, DateTime.UtcNow);
        Log.Info($"拖出远程画面：本地已松手 → {folder}");
        // 松手可能发生在 Moonlight 窗口外，它不一定把“松开”转给被控端；让被控端自己确认一下
        var sess = OutgoingTo(d.Ip);
        if (sess != null) _ = sess.SendAsync(MsgType.DragRelease, Array.Empty<byte>()).ContinueWith(_ => { });
        // 一直没配上就告诉用户，不要默默失败
        var mine = _dragOutTarget;
        _ = Task.Delay(8_500).ContinueWith(_ => Ui(() =>
        {
            if (_dragOutTarget is { } t && mine is { } m0 && t.At == m0.At)
            {
                _dragOutTarget = null;
                Log.Error("拖出远程画面：没有收到被控端的文件列表");
                ShowToast($"没有从 {d.Name} 拿到拖出的文件。请确认是从对方的资源管理器或桌面拖出来的");
            }
        }));
        if (_dragOutFiles is { } f && f.S.RemoteIp == d.Ip && (DateTime.UtcNow - f.At).TotalSeconds < 8) MatchDragOut(f.S, f.M, folder);
    }

    void MatchDragOut(PeerSession s, DragOutMsg m, string? folder)
    {
        if (folder == null && _dragOutTarget is { } t && t.Ip == s.RemoteIp && (DateTime.UtcNow - t.At).TotalSeconds < 8) folder = t.Folder;
        if (folder == null) { _dragOutFiles = (s, m, DateTime.UtcNow); return; } // 先到的是文件列表，等本地松手
        _dragOutTarget = null;
        _dragOutFiles = null;
        _dragDest[m.Token] = folder;
        Log.Info($"拖出远程画面：{m.Count} 项 → {folder}");
        _ = s.SendJsonAsync(MsgType.DragPull, new DragPullMsg(m.Token));
    }

    /// <summary>拖拽助手（普通权限进程）：接收层、屏幕边缘接收条、拖出提示都在它里面</summary>
    public FreeRemote.DragHelper.Host? Drag { get; private set; }

    void StartDragHelper()
    {
        if (Drag != null) return;
        Drag = new FreeRemote.DragHelper.Host(_ui);
        Drag.DropIn += (ip, paths, nx, ny) =>
        {
            var d = Devices.FirstOrDefault(x => x.Ip == ip);
            if (d != null) _ = DropToRemoteAsync(d, paths, nx, ny);
            // 拖放结束后前台还是资源管理器：把远程画面切回前台，并重新套用本地光标（否则光标可能看不见）
            if (_streams.TryGetValue(ip, out var mp) && !mp.HasExited)
            {
                var w = FreeRemote.Overlay.FindStreamWindow((uint)mp.Id);
                if (w != IntPtr.Zero) FreeRemote.Overlay.Focus(w);
            }
            if (_cursorClients.TryGetValue(ip, out var cc)) cc.Reapply();
        };
        Drag.DragOutRelease += (ip, folder) =>
        {
            var d = Devices.FirstOrDefault(x => x.Ip == ip);
            if (d != null) SetDragOutTarget(d, folder);
        };
        Drag.EdgeDrop += OnFilesAtEdge;
        try { Drag.Start(); } catch (Exception e) { Log.Error("拖拽助手启动失败", e); }
    }

    bool _edgeOn;

    /// <summary>被控端：有人在控制本机时，在屏幕边缘放好接收条</summary>
    void UpdateEdgeCatcher()
    {
        Ui(() =>
        {
            bool want = Config.AllowControl && IsBeingControlled;
            if (want == _edgeOn) return;
            _edgeOn = want;
            Drag?.Edge(want);
        });
    }

    bool IsBeingControlled { get { lock (_gate) return _sessions.Any(x => !x.Outgoing && x.IsOpen); } }

    void OnFilesAtEdge(string[] paths)
    {
        PeerSession? s = _cursorHost.Target;
        if (s == null || !s.IsOpen) lock (_gate) s = _sessions.LastOrDefault(x => !x.Outgoing && x.IsOpen);
        if (s == null) return;
        var token = Guid.NewGuid().ToString("N");
        _dragOut[token] = paths;
        long bytes = 0;
        try { bytes = TransferManager.Expand(paths).Sum(f => new FileInfo(f.Full).Length); } catch { }
        var names = paths.Take(3).Select(x => Path.GetFileName(x.TrimEnd('\\'))).ToArray();
        Log.Info($"文件拖到屏幕边缘：{paths.Length} 项，通知 {s.RemoteName}");
        _ = s.SendJsonAsync(MsgType.DragOut, new DragOutMsg(token, names, paths.Length, bytes));
    }

    string? TransferManager.IHost.ResolveDropFolder(PeerSession from, FileBeginMsg m)
    {
        // 控制端：拖出远程画面时选好的本地文件夹
        if (m.Token != null && _dragDest.TryRemove(m.Token, out var local)) return local;
        // 手机文件管理器：直接指定了电脑上的目标文件夹
        if (m.DestDir != null && Config.AllowControl && Directory.Exists(m.DestDir)) return m.DestDir;
        // 被控端：拖进远程画面的落点 → 落点下的资源管理器文件夹 / 桌面
        if (m.DropX >= 0 && Config.AllowControl)
        {
            var b = HostDisplays.StreamedBounds(); // 落点相对于正在串流的那块屏幕
            int x = b.Left + (int)(m.DropX * b.Width), y = b.Top + (int)(m.DropY * b.Height);
            string? folder = null;
            _ui.Invoke(() => folder = ShellTarget.At(x, y).Folder); // Shell COM 要在 STA 线程
            Log.Info($"拖放落点 ({x},{y}) → {folder ?? "默认接收文件夹"}");
            return folder;
        }
        return null;
    }

    // ================= 本地光标（零延迟） =================
    readonly CursorHost _cursorHost = new();
    readonly Dictionary<string, CursorClient> _cursorClients = new();
    readonly Dictionary<string, int> _localCursorShownPid = new(); // 已经给哪个 Moonlight 进程按过 Ctrl+Alt+Shift+C

    CursorClient? CursorFor(PeerSession s) => _cursorClients.TryGetValue(s.RemoteIp, out var c) ? c : null;

    /// <summary>
    /// 串流建立后：让 Moonlight 显示本地光标（Ctrl+Alt+Shift+C），通知被控端开始发送光标形状/位置，
    /// 被控端判断后让我们按 Ctrl+Alt+Shift+N 关掉 Sunshine 画面里的光标，避免出现两个光标。
    /// </summary>
    async Task StartLocalCursorAsync(DeviceVM d, Process p, StreamSettings active)
    {
        if (!active.LocalCursor || !active.DesktopMouse) return;
        try
        {
            var h = await FreeRemote.Overlay.WaitForStreamWindowAsync(p, 20_000);
            if (h == IntPtr.Zero) return;
            await Task.Delay(1500); // 等串流真正建立，按键才会被处理
            if (p.HasExited || !_streams.TryGetValue(d.Ip, out var cur) || cur != p) return;

            if (!_localCursorShownPid.TryGetValue(d.Ip, out var shown) || shown != p.Id)
            {
                FreeRemote.Overlay.Focus(h);
                await Task.Delay(150);
                await FreeRemote.Overlay.SendMoonlightHotkeyAsync('C');
                _localCursorShownPid[d.Ip] = p.Id;
            }
            if (_cursorClients.TryGetValue(d.Ip, out var cc)) cc.Attach(p, active);
            else _cursorClients[d.Ip] = new CursorClient(p, active, _ui);

            var s = await GetSessionAsync(d.Ip);
            await s.SendAsync(MsgType.LocalCursor, new byte[] { 1 });
        }
        catch (Exception e) { Log.Error("开启本地光标失败", e); }
    }

    void StopLocalCursor(string ip)
    {
        if (_cursorClients.Remove(ip, out var c)) c.Dispose();
        _localCursorShownPid.Remove(ip);
    }

    /// <summary>控制中心里的“本地鼠标”开关，即时生效</summary>
    public async Task SetLocalCursorAsync(DeviceVM d, bool on)
    {
        Config.Stream.LocalCursor = on;
        Config.Save();
        if (!_streams.TryGetValue(d.Ip, out var p)) return;
        if (on) { await StartLocalCursorAsync(d, p, Config.Stream); return; }
        try
        {
            if (_localCursorShownPid.TryGetValue(d.Ip, out var pid) && pid == p.Id)
                await FreeRemote.Overlay.SendMoonlightHotkeyAsync('C'); // 再按一次：Moonlight 隐藏本地光标
            StopLocalCursor(d.Ip);
            var s = await GetSessionAsync(d.Ip);
            await s.SendAsync(MsgType.LocalCursor, new byte[] { 0 }); // 被控端会让我们按 N 把画面里的光标打开
        }
        catch (Exception e) { Log.Error("关闭本地光标失败", e); }
    }

    public void Disconnect(DeviceVM d)
    {
        CloseOverlay(d.Ip);
        StopLocalCursor(d.Ip);
        if (_streams.TryRemove(d.Ip, out var p)) { try { p.Kill(); } catch { } }
        OutgoingTo(d.Ip)?.Close();
        d.IsConnected = false;
        UpdateBanner();
    }

    public void ForgetPairings()
    {
        Config.PairedHosts.Clear();
        Config.Save();
    }

    // ---------- 文件 ----------
    public async Task SendFilesAsync(DeviceVM d, IReadOnlyList<string> paths)
    {
        try
        {
            var s = await GetSessionAsync(d.Ip);
            await _tx.SendAsync(s, paths, false);
        }
        catch (Exception e)
        {
            Log.Error("发送失败", e);
            ShowToast($"发送到 {d.Name} 失败：{Friendly(e)}");
        }
    }

    static string Friendly(Exception e) => e switch
    {
        SocketException or OperationCanceledException => "对方不在线，或对方的 Farloop 没有运行",
        InvalidDataException ide => ide.Message,
        _ => e.Message,
    };

    // ---------- TransferManager.IHost ----------
    string TransferManager.IHost.ReceiveFolder => Config.ReceiveFolder;
    void TransferManager.IHost.AddTransfer(TransferVM vm) => Ui(() =>
    {
        Transfers.Insert(0, vm);
        // 浮动进度卡片：只在控制端显示（被控端显示的话会出现在远程画面里，控制端会看到两张）
        if (vm.IsClipboard) return;
        bool iControlPeer = _streams.ContainsKey(vm.PeerIp) || OutgoingTo(vm.PeerIp) != null;
        if (!iControlPeer && IsBeingControlled) return;
        var ip = vm.PeerIp;
        try
        {
            FreeRemote.CopyProgress.Track(vm, () =>
                _streams.TryGetValue(ip, out var p) && !p.HasExited ? FreeRemote.Overlay.FindStreamWindow((uint)p.Id) : IntPtr.Zero);
        }
        catch (Exception e) { Log.Error("显示复制进度失败", e); }
    });

    void TransferManager.IHost.OnBatchReceived(PeerSession from, IReadOnlyList<string> roots, string folder, bool forClipboard)
    {
        if (forClipboard) Ui(() => _clip.ApplyRemoteFiles(roots.ToArray()));
        else ShowToast($"已收到来自 {from.RemoteName} 的 {roots.Count} 项文件", folder);
    }

    // ---------- ClipboardSync.IHost ----------
    bool ClipboardSync.IHost.ClipboardEnabled => Config.ClipboardSync;
    bool ClipboardSync.IHost.FileClipboardEnabled => Config.FileClipboardSync;
    bool ClipboardSync.IHost.HasSessions { get { lock (_gate) return _sessions.Count > 0; } }

    List<PeerSession> DistinctSessions()
    {
        lock (_gate) return _sessions.Where(s => s.IsOpen).GroupBy(s => s.RemoteIp).Select(g => g.First()).ToList();
    }

    void ClipboardSync.IHost.BroadcastClipboard(byte[] payload)
    {
        foreach (var s in DistinctSessions())
            _ = s.SendAsync(MsgType.Clipboard, payload).ContinueWith(t => Log.Error("同步剪贴板失败", t.Exception), TaskContinuationOptions.OnlyOnFaulted);
    }

    void ClipboardSync.IHost.SendClipboardFiles(string[] paths)
    {
        long total;
        try { total = TransferManager.Expand(paths).Sum(f => new FileInfo(f.Full).Length); }
        catch { return; }
        if (total > ClipboardSync.MaxClipboardFileBytes)
        {
            ShowToast("复制的文件超过 2 GB，请拖到设备卡片上发送");
            return;
        }
        foreach (var s in DistinctSessions()) _ = _tx.SendAsync(s, paths, true);
    }
}
