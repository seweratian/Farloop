using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FreeRemote.Core;

/// <summary>串流参数，直接映射到 Moonlight 命令行选项</summary>
public sealed class StreamSettings
{
    public string DisplayMode { get; set; } = "windowed";   // windowed / borderless / fullscreen
    public string Resolution { get; set; } = "1920x1080";
    public int Fps { get; set; } = 60;
    public int BitrateMbps { get; set; } = 0;               // 0 = Moonlight 自动
    public string Codec { get; set; } = "auto";             // auto / H.264 / HEVC / AV1
    public bool Hdr { get; set; }
    public bool Yuv444 { get; set; }
    // 0.2.0 默认关了垂直同步，导致画面撕裂。现在默认打开，并用 Rev 把旧配置迁移过来。
    public bool VSync { get; set; } = true;
    public bool FramePacing { get; set; } = true;
    public int Rev { get; set; }
    public const int CurrentRev = 2;

    public StreamSettings Clone() => System.Text.Json.JsonSerializer.Deserialize<StreamSettings>(System.Text.Json.JsonSerializer.Serialize(this))!;

    /// <summary>改了这些就必须重新建立串流才能生效</summary>
    public bool NeedsReconnect(StreamSettings o) =>
        Resolution != o.Resolution || Fps != o.Fps || BitrateMbps != o.BitrateMbps || Codec != o.Codec ||
        Hdr != o.Hdr || Yuv444 != o.Yuv444 || VSync != o.VSync || FramePacing != o.FramePacing ||
        DesktopMouse != o.DesktopMouse || AudioOnHost != o.AudioOnHost;
    public bool DesktopMouse { get; set; } = true;          // 远程桌面式鼠标（绝对坐标）；关掉为游戏模式
    public bool LocalCursor { get; set; } = true;           // 本地光标（零延迟），形状从被控端同步；需要桌面鼠标模式
    public bool AudioOnHost { get; set; }
    public bool InfoCard { get; set; } = true;              // 我们自己的半透明信息卡片
    public bool PerfOverlay { get; set; }                   // Moonlight 自带的详细统计

    [JsonIgnore]
    public string ResolutionLabel => Resolution switch
    {
        "1280x720" => "720p",
        "1920x1080" => "1080p",
        "2560x1440" => "2K",
        "3840x2160" => "4K",
        _ => Resolution,
    };

    [JsonIgnore]
    public string CodecLabel => Codec == "auto" ? "自动" : Codec;
}

/// <summary>见过的设备（离线时也要显示，并能远程开机）</summary>
public sealed class KnownDevice
{
    public string Ip { get; set; } = "";
    public string Name { get; set; } = "";
    public string[] Macs { get; set; } = Array.Empty<string>();
    public string? Broadcast { get; set; }
    public string? GatewayMac { get; set; }
    public DateTime LastSeen { get; set; }
}

public sealed class AppConfig
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Farloop");

    static string FilePath => Path.Combine(DataDir, "config.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public string NetworkName { get; set; } = "";
    public string NetworkSecret { get; set; } = "";
    public string VirtualIp { get; set; } = "";
    public string DeviceName { get; set; } = Environment.MachineName;
    public bool AllowControl { get; set; } = true;   // 可被远程（Sunshine）
    public bool CanControl { get; set; } = true;     // 可控制别人（Moonlight）
    public string SunshineUser { get; set; } = "";
    public string SunshinePassProtected { get; set; } = "";
    public string ReceiveFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Farloop 接收");
    public bool AutoStart { get; set; } = true;
    /// <summary>仅中转：本机 EasyTier 不建立 P2P（--disable-p2p），所有流量走中转节点。P2P 不稳定时的备用。</summary>
    public bool ForceRelay { get; set; }
    /// <summary>仅 P2P：数据只走直连（--p2p-only），打洞不成功时连不上</summary>
    public bool P2POnly { get; set; }
    /// <summary>打洞用的公网 IP 规则，例如 "203.0"（多条宽带时只对外公布匹配的 IP）；空 = 自动</summary>
    public string PublicIpRule { get; set; } = "";
    /// <summary>没手动设置规则时，按出口检测自动选的主线路（如联通 9929 的国内出口）</summary>
    public string AutoIpRule { get; set; } = "";
    /// <summary>路由器没开 UPnP：组网内核就不再每次打洞前白试一遍（省 1~2 秒）</summary>
    public bool NoUpnp { get; set; }
    /// <summary>远程协助：本机的设备码（9 位数字，固定）、临时密码（每次协助结束自动换）、是否允许别人凭它们协助本机</summary>
    public string AssistId { get; set; } = "";
    public string AssistPassword { get; set; } = "";
    public bool AssistEnabled { get; set; } = true;
    /// <summary>“关闭对方屏幕”时用虚拟屏让实体显示器真正熄灭（省电、OLED 不烧屏）；关掉则只盖黑色遮罩</summary>
    public bool PrivacyVirtual { get; set; } = true;
    /// <summary>实际生效的打洞 IP 规则：手动优先，否则用自动检测的</summary>
    public string EffectiveIpRule => EasyTier.NormalizeIpRule(PublicIpRule) is { Length: > 0 } r ? r : EasyTier.NormalizeIpRule(AutoIpRule);
    /// <summary>首页“多条宽带”提示已被用户关掉</summary>
    public bool NetTipDismissed { get; set; }
    /// <summary>外观：0 跟随系统，1 浅色，2 深色</summary>
    public int Theme { get; set; }
    public bool ClipboardSync { get; set; } = true;
    public bool FileClipboardSync { get; set; } = true;
    public List<string> PairedHosts { get; set; } = new();
    public StreamSettings Stream { get; set; } = new();
    public List<KnownDevice> KnownDevices { get; set; } = new();

    // 协调打洞 / 兜底中转的节点。官方 public.easytier.cn 已下线，默认用社区志愿者节点（2026-11 实测可连）。
    // 流量经 EasyTier 用网络密钥加密，节点看不到内容。可在设置里改。
    public List<string> Peers { get; set; } = new(DefaultPeers);

    public static readonly string[] DefaultPeers =
    {
        "tcp://et.sumnight.top:11010",
        "tcp://183.230.36.171:11010",
        "tcp://easytier.weiai.org.cn:11010",
        "tcp://225284.xyz:11010",
        "wss://et.chinokou.cn",
    };

    [JsonIgnore]
    public bool IsConfigured => NetworkName != "" && NetworkSecret != "" && VirtualIp != "";

    [JsonIgnore]
    public string SunshinePassword
    {
        get
        {
            if (SunshinePassProtected == "") return "";
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(SunshinePassProtected), null, DataProtectionScope.LocalMachine);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }
        set
        {
            SunshinePassProtected = value == ""
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine));
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return Migrate(JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig());
        }
        catch (Exception e) { Log.Error("读取配置失败", e); }
        return new AppConfig();
    }

    public static AppConfig Migrate(AppConfig c)
    {
        if (c.Stream.Rev < 2)
        {
            c.Stream.VSync = true;
            c.Stream.FramePacing = true;
        }
        c.Stream.Rev = StreamSettings.CurrentRev;
        return c;
    }

    public void Save()
    {
        Stream.Rev = StreamSettings.CurrentRev;
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
        File.Move(tmp, FilePath, true);
    }

    // ---------- 邀请码：把网络名和密钥打包成一串，方便复制 ----------
    public string InviteCode => "FR1-" + B64.Enc(Encoding.UTF8.GetBytes(NetworkName + "\n" + NetworkSecret));

    static readonly Regex SafeToken = new("^[A-Za-z0-9-]{8,64}$");

    public static bool TryParseInvite(string code, out string name, out string secret)
    {
        name = secret = "";
        code = code.Trim();
        if (!code.StartsWith("FR1-")) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(B64.Dec(code[4..])).Split('\n');
            if (parts.Length != 2 || !SafeToken.IsMatch(parts[0]) || !SafeToken.IsMatch(parts[1])) return false;
            name = parts[0];
            secret = parts[1];
            return true;
        }
        catch { return false; }
    }

    public static string RandomToken(int len)
    {
        const string chars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    public static string RandomIp() => $"10.144.144.{RandomNumberGenerator.GetInt32(2, 254)}";
}
