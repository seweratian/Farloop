using System;
using System.Threading;

namespace FreeRemote.Core;

/// <summary>
/// 路由器自动端口映射（UPnP）：把 EasyTier 的 11010（UDP + TCP）映射到本机。
/// 映射成功后，别的设备（尤其是 4G/5G 手机这种对称型 NAT）可以直接连到这台电脑的公网地址，
/// 不用再靠打洞碰运气，几秒内就是 P2P。路由器没开 UPnP 时什么也不做。
/// 用 Windows 自带的 HNetCfg.NATUPnP（COM），在后台线程里跑，失败不影响其他功能。
/// </summary>
public static class Upnp
{
    public const int Port = 11010;
    const string Desc = "Farloop (EasyTier)";

    /// <summary>一句话状态，显示在设置页</summary>
    public static string Status { get; private set; } = "正在检测…";
    public static bool Mapped { get; private set; }

    /// <summary>尝试映射；返回是否成功。阻塞几秒，别在界面线程调用。</summary>
    public static bool TryMap(string? lanIp)
    {
        if (string.IsNullOrEmpty(lanIp)) { Status = "没有找到本机局域网地址"; return false; }
        bool ok = false;
        string status = "";
        var t = new Thread(() =>
        {
            try
            {
                var type = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
                if (type == null) { status = "系统不支持 UPnP"; return; }
                dynamic nat = Activator.CreateInstance(type)!;
                dynamic? col = nat.StaticPortMappingCollection;
                if (col == null) { status = "路由器没有开启 UPnP（在路由器设置里打开后，手机等设备更容易 P2P 直连）"; return; }
                string ext = "";
                foreach (var proto in new[] { "UDP", "TCP" })
                {
                    try { col.Remove(Port, proto); } catch { }
                    dynamic m = col.Add(Port, proto, Port, lanIp, true, Desc);
                    try { ext = (string)m.ExternalIPAddress; } catch { }
                }
                ok = true;
                status = $"已自动映射端口 {Port}（UDP/TCP）" + (string.IsNullOrEmpty(ext) ? "" : $"，公网地址 {ext}");
            }
            catch (Exception e)
            {
                status = "路由器拒绝了端口映射：" + e.Message;
            }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(20))) status = "路由器 UPnP 没有响应";
        Mapped = ok;
        Status = status;
        Log.Info("UPnP：" + status);
        return ok;
    }
}
