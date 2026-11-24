using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace FreeRemote.Core;

// 帧格式：[1 字节类型][4 字节长度, 小端][负载]
// 所有连接都跑在 EasyTier 的加密隧道里，这里只做双向认证，防止同网段的其他程序冒充。
public enum MsgType : byte
{
    Hello = 1,
    Auth = 2,
    Clipboard = 10,     // 负载: [1 字节种类: 1 文本 2 PNG 图片][数据]
    FileBegin = 20,
    FileChunk = 21,     // 负载: [16 字节文件 Id][数据]
    FileEnd = 22,
    FileAbort = 23,
    PairRequest = 30,
    PairResult = 31,
    Privacy = 40,       // 负载: [1 开 / 0 关]  控制端 → 被控端
    PrivacyState = 41,  // 负载: [当前状态]     被控端 → 控制端
    LocalCursor = 50,   // 控制端 → 被控端 [1/0]（详见 CursorSync.cs）
    CursorMode = 51,
    CursorShape = 52,
    CursorRef = 53,
    CursorHidden = 54,
    CursorPos = 55,
    DragOut = 60,       // 被控端 → 控制端：有文件被拖到被控端屏幕边缘（准备拖出远程画面）
    DragPull = 61,      // 控制端 → 被控端：在本地松开了，把这些文件发过来
    DragRelease = 62,   // 控制端 → 被控端：本地已松手；如果被控端左键还按着（Moonlight 没收到松开），替它松开
    DeviceInfo = 70,    // 互相告知网卡 MAC、路由器 MAC 等（远程开机用）
    WakeRequest = 71,   // 请同一局域网的设备代发开机信号
    WakeResult = 72,
    SetDisplay = 80,    // 控制端 → 被控端：换一块屏幕串流 SetDisplayMsg
    DisplayResult = 81, // 被控端 → 控制端：DisplayResultMsg（Sunshine 已重启好，可以重连画面）
    DisplayShotsReq = 83, // 控制端 → 被控端：要每块屏幕的缩略图（切换屏幕时直接看画面选）
    DisplayShots = 84,    // 被控端 → 控制端：DisplayShotsMsg
    DisplayLive = 82,   // 控制端 → 被控端：SetDisplayMsg，串流中已用 Ctrl+Alt+Shift+Fn 切到这块屏，记下来（不重启）
    // ---- 文件管理（手机端双栏文件管理器用；需要对方开着“允许被远程”）----
    FsList = 90,        // 列目录 FsListMsg（Path 为空 = 根：磁盘 + 桌面/下载/文档）
    FsListResult = 91,  // FsListResultMsg
    FsPull = 92,        // 让对方把这些文件发过来 FsPullMsg（对方用 FileBegin.Token 标记）
    FsOp = 93,          // 新建文件夹 / 重命名 / 删除（进回收站）FsOpMsg
    FsOpResult = 94,    // FsOpResultMsg
    // ---- 设备详情 / 终端 / 快速启动（被控端提供；需要“允许被远程”）----
    SysInfoReq = 110,   // [1 = 带桌面缩略图 / 0]
    SysInfo = 111,      // SysInfoMsg
    TermOpen = 112,     // 打开 PowerShell
    TermOpened = 113,   // TermOpenedMsg
    TermInput = 114,    // TermInputMsg（一行命令）
    TermOutput = 115,   // TermOutputMsg（输出；Prompt 非空 = 命令结束，当前目录）
    TermClose = 116,    // TermCloseMsg
    TermInterrupt = 117,// TermCloseMsg（Ctrl+C：结束当前命令，回 TermOpened）
    AppListReq = 118,   // 列出开始菜单 / 桌面的应用
    AppList = 119,      // AppListMsg
    LaunchApp = 120,    // LaunchAppMsg
    LaunchResult = 121, // FsOpResultMsg
    Goodbye = 123,      // 本机 Farloop 退出（组网也一起停），对方立刻把本机标为离线
    SetIpRule = 122,    // SetIpRuleMsg（手机 / 控制端远程设置被控端“打洞用的公网 IP”）
}

// Purpose：null/"control" = 远程控制会话；"info"/"wake" = 一次性的服务连接，问完就断，不算有人在控制
public record HelloMsg(string Name, string Ip, string Nonce, string Version, string? Purpose = null, string? Platform = null);
public record DeviceInfoMsg(string Name, string Ip, string[] Macs, string? Broadcast, string? GatewayMac, string Version,
    HostDisplay[]? Displays = null, string? CurrentDisplay = null, bool AdaptiveBitrate = false, bool LiveFramerate = false, bool LiveResolution = false);
public record SetDisplayMsg(string Id);
public record DisplayShot(string Id, string Name, bool Primary, int Width, int Height, int StreamIndex, string? Jpg);
public record DisplayShotsMsg(DisplayShot[] Displays, string Current);
public record DisplayResultMsg(bool Ok, string Message);
public record WakeRequestMsg(string[] Macs, string? Broadcast, string TargetName);
public record WakeResultMsg(bool Ok, string Message);
public record AuthMsg(string Mac);
// DropX/DropY：拖进远程画面时的落点（被控端主屏 0..1 坐标，-1 表示不是拖放）；Token：从远程画面拖出时的令牌
// DestDir：发送方指定的目标文件夹（手机文件管理器“复制到电脑的这个文件夹”）
public record FileBeginMsg(Guid Id, Guid Batch, string Root, string RelPath, long Size, int Index, int Total, long BatchBytes, bool ForClipboard,
    float DropX = -1, float DropY = -1, string? Token = null, string? DestDir = null);
public record FsListMsg(string Path);
// Path：可直接打开的完整路径（根列表里的磁盘、桌面等用它；普通条目为空，用 父路径 + Name）
public record FsEntry(string Name, bool Dir, long Size, long Modified, string? Path = null);
public record FsListResultMsg(string Path, string? Parent, FsEntry[] Entries, string? Error);
public record FsPullMsg(string[] Paths, string Token);
public record FsOpMsg(string Op, string Path, string? NewName);
public record FsOpResultMsg(bool Ok, string Message);
public record DragOutMsg(string Token, string[] Names, int Count, long Bytes);
public record DragPullMsg(string Token);
public record FileEndMsg(Guid Id);
public record FileAbortMsg(Guid Batch, string Reason);
public record PairRequestMsg(string Pin, string ClientName);
public record PairResultMsg(bool Ok, string Message);

public static class Frames
{
    public const int MaxPayload = 64 * 1024 * 1024;

    public static async Task WriteAsync(Stream s, MsgType t, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var h = new byte[5];
        h[0] = (byte)t;
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(1), payload.Length);
        await s.WriteAsync(h, ct);
        if (payload.Length > 0) await s.WriteAsync(payload, ct);
    }

    public static async Task<(MsgType Type, byte[] Payload)> ReadAsync(Stream s, CancellationToken ct)
    {
        var h = new byte[5];
        await s.ReadExactlyAsync(h, ct);
        int len = BinaryPrimitives.ReadInt32LittleEndian(h.AsSpan(1));
        if (len < 0 || len > MaxPayload) throw new InvalidDataException("帧长度异常");
        var b = new byte[len];
        if (len > 0) await s.ReadExactlyAsync(b, ct);
        return ((MsgType)h[0], b);
    }

    public static byte[] Json<T>(T o) => JsonSerializer.SerializeToUtf8Bytes(o);
    public static T FromJson<T>(byte[] b) => JsonSerializer.Deserialize<T>(b) ?? throw new InvalidDataException("消息为空");
}

public sealed class PeerSession
{
    public const int Port = 47900;

    /// <summary>程序版本（握手时互相告知，版本不一致会提示升级）</summary>
    public static readonly string Version =
        typeof(PeerSession).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    public string RemoteVersion { get; private set; } = "";
    /// <summary>对方平台（手机端填 "android"；电脑端和旧版手机为空）</summary>
    public string RemotePlatform { get; private set; } = "";
    public string? RemotePurpose { get; private set; }
    public bool IsServiceConnection => RemotePurpose is "info" or "wake";
    string? _myPurpose;

    /// <summary>a 比 b 旧</summary>
    public static bool IsOlder(string a, string b)
    {
        static int[] P(string s) => s.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var x = P(a); var y = P(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
            if (p != q) return p < q;
        }
        return false;
    }

    readonly TcpClient _client;
    readonly NetworkStream _stream;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly CancellationTokenSource _cts = new();
    int _closed;

    public string RemoteName { get; private set; } = "";
    public string RemoteIp { get; private set; } = "";
    public bool Outgoing { get; }
    public bool IsOpen => Volatile.Read(ref _closed) == 0;
    // The listener or connector that selected the tunnel credentials sets this.
    public bool IsAssist { get; }
    int _assistPairStarted, _assistApproved;
    public bool IsApproved => !IsAssist || Volatile.Read(ref _assistApproved) == 1;
    public bool IsReady => IsOpen && IsApproved;
    public CancellationToken ClosedToken => _cts.Token;

    public bool TryBeginAssistPair() => IsAssist && !Outgoing && IsOpen &&
        Interlocked.CompareExchange(ref _assistPairStarted, 1, 0) == 0;
    public void ApproveAssist() => Volatile.Write(ref _assistApproved, 1);

    bool AllowsFrame(MsgType type, bool sending)
    {
        if (!IsAssist) return true;
        if (type == MsgType.PairRequest) return Outgoing == sending;
        if (type == MsgType.PairResult) return Outgoing != sending;
        return IsApproved;
    }

    public event Action<PeerSession, MsgType, byte[]>? Message;
    public event Action<PeerSession>? Closed;

    PeerSession(TcpClient c, bool outgoing, bool isAssist)
    {
        _client = c;
        _client.NoDelay = true;
        _stream = c.GetStream();
        Outgoing = outgoing;
        IsAssist = isAssist;
        // 默认的 TcpClient 是双栈套接字，对方地址会是 “::ffff:10.x.x.x”，和设备列表里的 IP 对不上，
        // 会导致：重复建连接、本地光标形状收不到、拖出文件配不上。统一转成纯 IPv4。
        var addr = ((IPEndPoint)c.Client.RemoteEndPoint!).Address;
        if (addr.IsIPv4MappedToIPv6) addr = addr.MapToIPv4();
        RemoteIp = addr.ToString();
    }

    public static async Task<PeerSession> ConnectAsync(string ip, string myName, string myIp, string secret, CancellationToken ct, string? purpose = null, bool isAssist = false)
    {
        var c = new TcpClient(AddressFamily.InterNetwork);
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(8000);
        try
        {
            await c.ConnectAsync(IPAddress.Parse(ip), Port, t.Token);
            var s = new PeerSession(c, true, isAssist) { _myPurpose = purpose };
            await s.HandshakeAsync(myName, myIp, secret, t.Token);
            return s;
        }
        catch { c.Dispose(); throw; }
    }

    public static async Task<PeerSession> AcceptAsync(TcpClient c, string myName, string myIp, string secret, CancellationToken ct, bool isAssist)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(8000);
        var s = new PeerSession(c, false, isAssist);
        await s.HandshakeAsync(myName, myIp, secret, t.Token);
        return s;
    }

    // 双向挑战应答。MAC 里带上发送方角色，防止对方把我们的应答原样反射回来。
    async Task HandshakeAsync(string myName, string myIp, string secret, CancellationToken ct)
    {
        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        string myRole = Outgoing ? "C" : "S", peerRole = Outgoing ? "S" : "C";

        await Frames.WriteAsync(_stream, MsgType.Hello, Frames.Json(new HelloMsg(myName, myIp, nonce, Version, _myPurpose)), ct);
        var (t1, b1) = await Frames.ReadAsync(_stream, ct);
        if (t1 != MsgType.Hello) throw new InvalidDataException("握手失败");
        var hello = Frames.FromJson<HelloMsg>(b1);
        if (hello.Nonce == nonce) throw new InvalidDataException("握手失败");
        RemoteName = string.IsNullOrWhiteSpace(hello.Name) ? RemoteIp : hello.Name;
        RemoteVersion = hello.Version ?? "";
        RemotePlatform = hello.Platform ?? "";
        RemotePurpose = hello.Purpose;

        await Frames.WriteAsync(_stream, MsgType.Auth, Frames.Json(new AuthMsg(Mac(secret, hello.Nonce, myRole))), ct);
        var (t2, b2) = await Frames.ReadAsync(_stream, ct);
        if (t2 != MsgType.Auth) throw new InvalidDataException("握手失败");
        var auth = Frames.FromJson<AuthMsg>(b2);
        var expected = Convert.FromBase64String(Mac(secret, nonce, peerRole));
        byte[] got;
        try { got = Convert.FromBase64String(auth.Mac); } catch { throw new InvalidDataException("认证失败"); }
        if (!CryptographicOperations.FixedTimeEquals(got, expected)) throw new InvalidDataException("认证失败：网络密钥不一致");
    }

    static string Mac(string secret, string nonce, string role) =>
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"FreeRemote/1|{role}|{nonce}")));

    public void Start() => _ = Task.Run(ReadLoop);

    async Task ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var (t, b) = await Frames.ReadAsync(_stream, _cts.Token);
                if (!AllowsFrame(t, sending: false)) continue;
                try { Message?.Invoke(this, t, b); }
                catch (Exception e) { Log.Error($"处理消息 {t} 出错", e); }
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or EndOfStreamException) { }
        catch (Exception e) { Log.Error("连接读取出错", e); }
        finally { Close(); }
    }

    public async Task SendAsync(MsgType t, ReadOnlyMemory<byte> payload)
    {
        if (!AllowsFrame(t, sending: true)) throw new InvalidOperationException("远程协助会话尚未授权此操作");
        await _writeLock.WaitAsync(_cts.Token);
        try { await Frames.WriteAsync(_stream, t, payload, _cts.Token); }
        catch { Close(); throw; }
        finally { _writeLock.Release(); }
    }

    public Task SendJsonAsync<T>(MsgType t, T obj) => SendAsync(t, Frames.Json(obj));

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1) return;
        _cts.Cancel();
        try { _client.Close(); } catch { }
        Closed?.Invoke(this);
    }
}
