using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>本机局域网信息：用于让别的设备知道怎么唤醒本机、以及判断两台设备是否在同一局域网</summary>
public sealed record LanInfo(string[] Macs, string? LanIp, string? Broadcast, string? GatewayIp, string? GatewayMac, string? AdapterName, string? AdapterDesc, bool Wired);

/// <summary>远程开机前，被控端自己的准备情况</summary>
public sealed record WakeReadiness(string? Adapter, string? Mac, bool Wired, bool? MagicPacket, bool? WakeArmed, bool FastStartup);

/// <summary>
/// 远程开机（Wake-on-LAN）。
/// 魔术包是局域网广播，路由器不会从外网转发进来；所以由“与目标同一局域网、正在在线的另一台 FreeRemote 设备”
/// 代为发送 —— 只依赖局域网本身，和路由器品牌、设置都无关。
/// 同一局域网的判断：两台设备的默认网关（路由器）MAC 相同。
/// </summary>
public static class WakeOnLan
{
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    static extern int SendARP(uint destIp, uint srcIp, byte[] mac, ref uint len);

    /// <summary>虚拟网卡（EasyTier、虚拟机、VPN）不算</summary>
    static bool IsVirtual(NetworkInterface n)
    {
        var s = (n.Name + " " + n.Description).ToLowerInvariant();
        return n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
               || s.Contains("wintun") || s.Contains("tap-") || s.Contains("virtual") || s.Contains("vmware") || s.Contains("hyper-v")
               || s.Contains("vethernet") || s.Contains("bluetooth") || s.StartsWith("et_") || s.Contains("easytier") || s.Contains("wireguard")
               || s.Contains("loopback") || s.Contains("pseudo");
    }

    static bool IsWired(NetworkInterface n) => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT;

    public static string FormatMac(PhysicalAddress a) => string.Join("-", a.GetAddressBytes().Select(b => b.ToString("X2")));

    /// <summary>有默认网关、正在使用的物理网卡（优先有线）</summary>
    static NetworkInterface? PrimaryAdapter() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && !IsVirtual(n)
                        && n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .OrderByDescending(IsWired)
            .FirstOrDefault();

    public static LanInfo GetLocal()
    {
        // 所有有线物理网卡的 MAC 都记下（插哪个口都能唤醒）；无线网卡唤醒支持很少，只在没有有线网卡时用
        var physical = NetworkInterface.GetAllNetworkInterfaces().Where(n => !IsVirtual(n) && n.GetPhysicalAddress().GetAddressBytes().Length == 6).ToList();
        var macs = physical.Where(IsWired).Select(n => FormatMac(n.GetPhysicalAddress())).Distinct().ToList();

        var p = PrimaryAdapter();
        string? lanIp = null, bcast = null, gwIp = null, gwMac = null;
        if (p != null)
        {
            var ip = p.GetIPProperties();
            var ua = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
            if (ua != null)
            {
                lanIp = ua.Address.ToString();
                var a = ua.Address.GetAddressBytes(); var m = ua.IPv4Mask.GetAddressBytes();
                bcast = new IPAddress(a.Select((b, i) => (byte)(b | ~m[i])).ToArray()).ToString();
            }
            var gw = ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork);
            if (gw != null) { gwIp = gw.ToString(); gwMac = ArpLookup(gw); }
            var pm = FormatMac(p.GetPhysicalAddress());
            if (!macs.Contains(pm)) macs.Insert(IsWired(p) ? 0 : macs.Count, pm);
        }
        return new LanInfo(macs.ToArray(), lanIp, bcast, gwIp, gwMac, p?.Name, p?.Description, p != null && IsWired(p));
    }

    static string? ArpLookup(IPAddress ip)
    {
        try
        {
            var mac = new byte[6]; uint len = 6;
            if (SendARP(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), 0, mac, ref len) != 0 || len != 6) return null;
            return string.Join("-", mac.Select(b => b.ToString("X2")));
        }
        catch { return null; }
    }

    public static byte[] MagicPacket(string mac)
    {
        var m = mac.Split('-', ':').Select(h => Convert.ToByte(h, 16)).ToArray();
        if (m.Length != 6) throw new FormatException("MAC 地址格式不对：" + mac);
        var p = new byte[6 + 16 * 6];
        for (int i = 0; i < 6; i++) p[i] = 0xFF;
        for (int r = 0; r < 16; r++) Array.Copy(m, 0, p, 6 + r * 6, 6);
        return p;
    }

    /// <summary>
    /// 在本机所在局域网发魔术包：从每块物理网卡发出，目标 255.255.255.255 和子网广播，端口 9 和 7，各发 3 轮。
    /// 返回成功发出的包数。
    /// </summary>
    public static async Task<int> SendAsync(IEnumerable<string> macs, string? subnetBroadcast)
    {
        var packets = macs.Select(MagicPacket).ToList();
        var locals = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && !IsVirtual(n))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => (Ip: a.Address, Bcast: new IPAddress(a.Address.GetAddressBytes().Select((b, i) => (byte)(b | ~a.IPv4Mask.GetAddressBytes()[i])).ToArray()))))
            .ToList();
        int sent = 0;
        for (int round = 0; round < 3; round++)
        {
            foreach (var (ip, bc) in locals)
            {
                try
                {
                    using var u = new UdpClient(new IPEndPoint(ip, 0)) { EnableBroadcast = true };
                    var targets = new List<IPAddress> { IPAddress.Broadcast, bc };
                    if (subnetBroadcast != null && IPAddress.TryParse(subnetBroadcast, out var sb) && !targets.Contains(sb)) targets.Add(sb);
                    foreach (var t in targets.Distinct())
                        foreach (var port in new[] { 9, 7 })
                            foreach (var pk in packets) { await u.SendAsync(pk, pk.Length, new IPEndPoint(t, port)); sent++; }
                }
                catch (Exception e) { Log.Error($"从 {ip} 发送唤醒包失败", e); }
            }
            await Task.Delay(150);
        }
        Log.Info($"已发送唤醒包 {sent} 个，目标 MAC {string.Join(",", macs)}");
        return sent;
    }

    // ---------------- 被控端：远程开机的准备 ----------------
    public static async Task<WakeReadiness> CheckLocalAsync()
    {
        var p = PrimaryAdapter();
        bool fastStartup = true;
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
            fastStartup = Convert.ToInt32(k?.GetValue("HiberbootEnabled") ?? 1) != 0;
        }
        catch { }
        if (p == null) return new WakeReadiness(null, null, false, null, null, fastStartup);

        bool? magic = null, armed = null;
        try
        {
            var (_, o) = await Proc.RunAsync("powershell", $"-NoProfile -Command \"(Get-NetAdapterPowerManagement -Name '{p.Name}').WakeOnMagicPacket\"", 20_000);
            var t = o.Trim();
            if (t.Length > 0) magic = t.Equals("Enabled", StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        try
        {
            var (_, o) = await Proc.RunAsync("powercfg", "/devicequery wake_armed", 20_000);
            armed = o.Contains(p.Description, StringComparison.OrdinalIgnoreCase);
        }
        catch { }
        return new WakeReadiness(p.Name, FormatMac(p.GetPhysicalAddress()), IsWired(p), magic, armed, fastStartup);
    }

    /// <summary>一键开启：网卡允许魔术包唤醒、允许唤醒电脑、关闭快速启动（快速启动会让关机后网卡断电，无法唤醒）</summary>
    public static async Task EnableLocalAsync()
    {
        var p = PrimaryAdapter() ?? throw new InvalidOperationException("没找到正在使用的网卡");
        string name = p.Name.Replace("'", "''");
        await Proc.RunAsync("powershell", "-NoProfile -Command \"" +
            $"Enable-NetAdapterPowerManagement -Name '{name}' -WakeOnMagicPacket -ErrorAction SilentlyContinue; " +
            $"Set-NetAdapterAdvancedProperty -Name '{name}' -RegistryKeyword '*WakeOnMagicPacket' -RegistryValue 1 -ErrorAction SilentlyContinue\"", 60_000);
        await Proc.RunAsync("powercfg", $"/deviceenablewake \"{p.Description}\"", 20_000);
        using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power"))
            k.SetValue("HiberbootEnabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
        Log.Info($"已开启远程开机：网卡 {p.Name}，关闭快速启动");
    }
}
