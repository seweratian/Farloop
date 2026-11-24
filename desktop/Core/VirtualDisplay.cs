using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// 虚拟屏息屏（和 UU 远程的做法一样）：
/// 远控时加一块虚拟显示器、把实体显示器从桌面拿掉 —— 显示器收不到信号自动待机（省电、OLED 不烧屏），
/// Sunshine 采集的是虚拟屏，分辨率和原来的主屏一样。结束时还原原来的显示器布局，再停用虚拟显示器。
///
/// 驱动：Virtual Display Driver（github.com/VirtualDrivers/Virtual-Display-Driver，MIT，SignPath 签名），
/// 打包在 Farloop 里，第一次用时安装；平时设备处于停用状态，不会多出一块屏。
/// 兜底：Farloop 崩溃 / 被结束后再启动时自动还原；按 Ctrl+Alt+Shift+F12 也能立刻还原（见 MainWindow）。
/// </summary>
public static class VirtualDisplay
{
    const string HwId = @"Root\MttVDD";
    static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");
    static string Dir => Path.Combine(AppConfig.DataDir, "vdd");
    static string ActiveFlag => Path.Combine(Dir, "active.flag");

    static DISPLAYCONFIG_PATH_INFO[]? _savedPaths;
    static DISPLAYCONFIG_MODE_INFO[]? _savedModes;
    static readonly object Gate = new();

    public static bool IsActive { get; private set; }

    // =========================== 安装 / 启用 / 停用 ===========================

    /// <summary>驱动和设备已经装好</summary>
    public static bool Installed => FindInstanceId() != null;

    /// <summary>安装驱动并建好设备（需要管理员），装完先停用。返回 null = 成功</summary>
    public static async Task<string?> InstallAsync()
    {
        try
        {
            if (Installed) return null;
            Directory.CreateDirectory(Path.Combine(Dir, "driver"));
            foreach (var (res, file) in new[] { ("vdd-inf", "MttVDD.inf"), ("vdd-dll", "MttVDD.dll"), ("vdd-cat", "MttVDD.cat") })
            {
                using var s = typeof(VirtualDisplay).Assembly.GetManifestResourceStream(res) ?? throw new InvalidOperationException("缺少内置驱动文件 " + file);
                using var f = File.Create(Path.Combine(Dir, "driver", file));
                s.CopyTo(f);
            }
            WriteSettings(new[] { (3840, 2160) });
            var inf = Path.Combine(Dir, "driver", "MttVDD.inf");
            string? err = await Task.Run(() => CreateRootDevice(inf));
            if (err != null) return err;
            for (int i = 0; i < 20 && FindInstanceId() == null; i++) await Task.Delay(250);
            var id = FindInstanceId();
            if (id == null) return "驱动已安装，但没有找到虚拟显示器设备";
            await SetDeviceEnabled(id, false);
            Log.Info("虚拟显示器驱动已安装：" + id);
            return null;
        }
        catch (Exception e) { Log.Error("安装虚拟显示器驱动", e); return e.Message; }
    }

    /// <summary>驱动读取的设置：分辨率列表里放上实体主屏的分辨率，虚拟屏才能设成一样大</summary>
    static void WriteSettings(IEnumerable<(int W, int H)> extra, int count = 1, string gpu = "default")
    {
        Directory.CreateDirectory(Dir);
        var res = new List<(int, int)> { (1280, 720), (1920, 1080), (2560, 1440), (3840, 2160) };
        foreach (var r in extra) if (!res.Contains(r) && r.W >= 640 && r.H >= 480) res.Add(r);
        var sb = new StringBuilder();
        sb.Append($"<?xml version='1.0' encoding='utf-8'?>\n<vdd_settings>\n  <monitors><count>{Math.Clamp(count, 1, 4)}</count></monitors>\n  <gpu><friendlyname>{System.Security.SecurityElement.Escape(gpu)}</friendlyname></gpu>\n  <global>\n");
        foreach (var hz in new[] { 30, 60, 90, 120, 144, 165 }) sb.Append($"    <g_refresh_rate>{hz}</g_refresh_rate>\n");
        sb.Append("  </global>\n  <resolutions>\n");
        foreach (var (w, h) in res) sb.Append($"    <resolution><width>{w}</width><height>{h}</height><refresh_rate>60</refresh_rate></resolution>\n");
        sb.Append("  </resolutions>\n  <options><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride>" +
                  "<HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus><logging>false</logging><debuglogging>false</debuglogging></options>\n</vdd_settings>\n");
        File.WriteAllText(Path.Combine(Dir, "vdd_settings.xml"), sb.ToString());
        using var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver");
        k.SetValue("VDDPATH", Dir);
    }

    /// <remarks>
    /// 用 SetupAPI 的 DIF_PROPERTYCHANGE（和设备管理器里“启用 / 禁用设备”一样，各版本 Windows 都支持；做法参考 airhand，MIT）。
    /// 以前用 pnputil /enable-device，旧一些的 Windows 10 不支持（返回 50），虚拟显示器装上后就一直启用着，多出一块屏
    /// </remarks>
    static async Task SetDeviceEnabled(string instanceId, bool on)
    {
        var err = SetEnabledSetupApi(instanceId, on);
        if (err == null) return;
        Log.Info($"SetupAPI {(on ? "启用" : "停用")}虚拟显示器失败（{err}），改用 pnputil");
        var (code, o) = await Proc.RunAsync("pnputil", $"/{(on ? "enable" : "disable")}-device \"{instanceId}\"", 30_000);
        if (code != 0) Log.Info($"pnputil {(on ? "启用" : "停用")}虚拟显示器：{code} {o.Trim()}");
    }

    static string? SetEnabledSetupApi(string instanceId, bool on)
    {
        var g = DisplayClass;
        IntPtr set = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (set == new IntPtr(-1)) return "SetupDiCreateDeviceInfoList " + Marshal.GetLastWin32Error();
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiOpenDeviceInfoW(set, instanceId, IntPtr.Zero, 0, ref d)) return "打开设备 " + Marshal.GetLastWin32Error();
            var pc = new SP_PROPCHANGE_PARAMS
            {
                cbSize = 8, InstallFunction = DIF_PROPERTYCHANGE,
                StateChange = on ? DICS_ENABLE : DICS_DISABLE, Scope = DICS_FLAG_GLOBAL, HwProfile = 0,
            };
            if (!SetupDiSetClassInstallParamsW(set, ref d, ref pc, (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>())) return "设置参数 " + Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, set, ref d)) return "执行 " + Marshal.GetLastWin32Error();
            return null;
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    // =========================== 进入 / 退出虚拟屏 ===========================

    /// <summary>进入虚拟屏：实体显示器熄灭。返回 null = 成功</summary>
    public static async Task<string?> EnterAsync()
    {
        if (IsActive) return null;
        var id = FindInstanceId();
        if (id == null) { var e = await InstallAsync(); if (e != null) return e; id = FindInstanceId(); if (id == null) return "没有虚拟显示器设备"; }

        // 1. 记下现在的布局，以及要用的分辨率（现在最大那块屏）
        // 只有间接显示器（如 UU 的虚拟屏）在用时，QDC_ONLY_ACTIVE_PATHS 会返回 ERROR_NOT_SUPPORTED（实测），改读系统当前布局
        if (!QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes) && !QueryConfig(QDC_DATABASE_CURRENT, out paths, out modes))
            return "读取显示器布局失败";
        var (w, h) = LargestActiveResolution(paths);
        _lastRes = (w, h);
        // 每块实体屏对应一块虚拟屏：分辨率、位置、主屏都照原样，窗口留在原来那块屏上（双屏内容不会挤到一块屏）
        var layout = PhysicalLayout(paths, modes, id);
        if (layout.Count == 0) layout.Add(new Screen1(w, h, 0, 0, true));
        _layout = layout;
        lock (Gate) { _savedPaths = paths; _savedModes = modes; }
        // 虚拟屏要在“主屏所在的那块显卡”上渲染：双显卡笔记本（Intel 核显 + NVIDIA 独显）上驱动默认用独显，
        // 而 Sunshine 启动时按主屏选的是核显的编码器（QuickSync），虚拟屏到了独显上就编不了码，画面停住、连接被断开（实测）
        var gpu = PrimaryRenderAdapter();
        Log.Info($"虚拟屏使用显卡：{gpu}");
        WriteSettings(layout.Select(l => (l.W, l.H)).Append((w, h)), layout.Count, gpu);
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ActiveFlag, DateTime.Now.ToString("s"));

        // 2. 启用虚拟显示器，等它们都出现（驱动启用时读设置文件里的个数）
        await SetDeviceEnabled(id, true);
        int found = 0;
        for (int i = 0; i < 40 && found < layout.Count; i++) { await Task.Delay(250); found = VirtualTargetCount(id); }
        if (found == 0) { await ExitAsync("虚拟显示器没有出现"); return "虚拟显示器没有出现（驱动可能被系统拦截）"; }
        if (found < layout.Count) Log.Info($"虚拟屏只出现了 {found} 块（要 {layout.Count} 块），先用这些");

        // 3. 只保留虚拟屏（实体屏全部拿掉 → 无信号待机）
        var err = Isolate(id);
        if (err != null)
        {
            Log.Error("切换到虚拟屏失败：" + err);
            await ExitAsync("切换失败");
            return "切换到虚拟屏失败（" + err + "）";
        }
        IsActive = true;

        // 4. 每块虚拟屏的分辨率、位置、主屏照原来的实体屏摆
        await Task.Delay(500);
        ApplyLayout(id, layout);
        HostDisplays.InvalidateOrder();
        Log.Info($"虚拟屏息屏：开启（{layout.Count} 块虚拟屏：{string.Join("，", layout.Select(l => $"{l.W}×{l.H}@{l.X},{l.Y}{(l.Primary ? " 主屏" : "")}"))}；实体显示器已熄灭）");
        _ = Task.Run(() => KeepIsolatedAsync(id));
        return null;
    }

    /// <summary>
    /// 息屏期间盯着显示布局：显示器没信号进入待机时，很多显示器（尤其 DisplayPort）会向电脑报告“断开 → 又接上”，
    /// Windows 当成插了新显示器，按它记着的布局把实体屏接回桌面，屏幕又亮了（实测：黑几秒又亮）。
    /// 发现有实体屏回到桌面就再撤下（做法参考 airhand 的管理线程，MIT）
    /// </summary>
    /// <remarks>
    /// 0.18.0 实测：每 3 秒被接回一次、重新熄灭一次，来回切换显示布局 → 画面黑 / 灰 / 闪现、Sunshine 反复重新初始化，几乎没法操作。
    /// 根本办法在 Isolate 里：把“只有虚拟屏”存进显示设置数据库（Windows 按“接着哪些显示器”分别记），
    /// 显示器插拔时 Windows 套用的就是它，不会再把实体屏接回来。这里只是兜底：
    /// 连续两秒都看到实体屏才动手；1 分钟里动手超过 3 次就停手（宁可屏幕亮着，也不要画面一直抽搐）
    /// </remarks>
    static async Task KeepIsolatedAsync(string id)
    {
        int fixes = 0, seen = 0;
        var recent = new Queue<DateTime>();
        bool gaveUp = false;
        while (IsActive && !_exiting)
        {
            await Task.Delay(1000);
            if (!IsActive || _exiting) break;
            try
            {
                if (!QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var paths, out _) && !QueryConfig(QDC_DATABASE_CURRENT, out paths, out _)) continue;
                bool physical = paths.Any(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && !IsVirtualAdapter(p.targetInfo.adapterId, id));
                if (!physical) { seen = 0; continue; }
                if (++seen < 2 || gaveUp) continue; // 一闪而过的不管
                seen = 0;
                var now = DateTime.UtcNow;
                while (recent.Count > 0 && now - recent.Peek() > TimeSpan.FromMinutes(1)) recent.Dequeue();
                if (recent.Count >= 3)
                {
                    gaveUp = true;
                    Log.Error("息屏中实体显示器 1 分钟里被系统接回超过 3 次，不再抢（避免画面反复闪烁）；这台电脑的显示器会亮着");
                    continue;
                }
                recent.Enqueue(now);
                var err = Isolate(id);
                fixes++;
                Log.Info($"息屏中实体显示器被系统接回桌面（第 {fixes} 次），{(err == null ? "已重新熄灭" : "重新熄灭失败：" + err)}");
                if (err == null && _layout is { } lay) { await Task.Delay(300); ApplyLayout(id, lay); }
            }
            catch (Exception e) { Log.Error("息屏守护出错", e); }
        }
    }

    static (int W, int H)? _lastRes;

    /// <summary>主屏所在的显卡名（如 “Intel(R) UHD Graphics”）；主屏本身是虚拟屏（UU 等）或读不到时返回 "default"（驱动自己选）</summary>
    static string PrimaryRenderAdapter()
    {
        try
        {
            for (uint i = 0; ; i++)
            {
                var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
                if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                const int ATTACHED = 1, PRIMARY = 4;
                if ((dd.StateFlags & ATTACHED) == 0 || (dd.StateFlags & PRIMARY) == 0) continue;
                var name = (dd.DeviceString ?? "").Trim();
                string[] notGpu = { "Virtual", "IDD", "GameViewer", "Parsec", "Basic Display", "Basic Render", "Mtt", "VDD", "Remote" };
                if (name == "" || notGpu.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) return "default";
                return name;
            }
        }
        catch { }
        return "default";
    }

    /// <summary>息屏前一块实体屏的样子（源模式：分辨率、桌面上的位置）</summary>
    public sealed record Screen1(int W, int H, int X, int Y, bool Primary);
    static List<Screen1>? _layout;

    /// <summary>息屏中：每块虚拟屏对应原来哪块实体屏（按位置从左到右，界面上显示用）</summary>
    public static IReadOnlyList<Screen1> Layout => _layout ?? new List<Screen1>();

    /// <summary>桌面上的实体屏（不含我们的虚拟屏），按位置从左到右、从上到下</summary>
    static List<Screen1> PhysicalLayout(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes, string id)
    {
        var list = new List<Screen1>();
        foreach (var p in paths)
        {
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) == 0 || IsVirtualAdapter(p.targetInfo.adapterId, id)) continue;
            var i = p.sourceInfo.modeInfoIdx;
            if (i == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || i >= modes.Length || modes[i].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
            var m = modes[i];
            if (m.srcWidth < 640 || m.srcHeight < 480) continue;
            if (list.Any(l => l.X == m.srcPosX && l.Y == m.srcPosY)) continue; // 复制模式：同一个位置只算一块
            list.Add(new Screen1((int)m.srcWidth, (int)m.srcHeight, m.srcPosX, m.srcPosY, m.srcPosX == 0 && m.srcPosY == 0));
        }
        return list.OrderBy(l => l.X).ThenBy(l => l.Y).Take(4).ToList();
    }

    /// <summary>虚拟显示器报告了几块屏（可接到桌面上的输出个数）</summary>
    static int VirtualTargetCount(string id)
    {
        if (!QueryConfig(QDC_ALL_PATHS, out var paths, out _)) return 0;
        return paths.Where(p => p.targetInfo.targetAvailable != 0 && IsVirtualAdapter(p.targetInfo.adapterId, id))
                    .Select(p => p.targetInfo.id).Distinct().Count();
    }

    /// <summary>
    /// 把桌面上的虚拟屏摆成和原来的实体屏一样：第 i 块（按输出编号）用第 i 块实体屏的分辨率和位置，原来的主屏还是主屏。
    /// 用 ChangeDisplaySettingsEx 逐块登记（CDS_NORESET），最后一次性生效
    /// </summary>
    static void ApplyLayout(string id, List<Screen1> layout)
    {
        if (!QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var paths, out _) && !QueryConfig(QDC_ALL_PATHS, out paths, out _)) return;
        var gdis = paths.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && IsVirtualAdapter(p.targetInfo.adapterId, id))
                        .OrderBy(p => p.targetInfo.id).Select(SourceGdiName).Where(g => g != null).Cast<string>().Distinct().ToList();
        for (int i = 0; i < gdis.Count && i < layout.Count; i++)
        {
            var l = layout[i];
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (!EnumDisplaySettingsW(gdis[i], ENUM_CURRENT_SETTINGS, ref dm)) continue;
            dm.dmPelsWidth = l.W; dm.dmPelsHeight = l.H; dm.dmDisplayFrequency = 60;
            dm.dmPositionX = l.X; dm.dmPositionY = l.Y;
            dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_POSITION;
            int flags = CDS_UPDATEREGISTRY | CDS_NORESET | (l.Primary ? CDS_SET_PRIMARY : 0);
            int r = ChangeDisplaySettingsExW(gdis[i], ref dm, IntPtr.Zero, flags, IntPtr.Zero);
            if (r != 0) Log.Info($"虚拟屏 {i + 1} 设为 {l.W}×{l.H}@{l.X},{l.Y} 失败：{r}");
        }
        ChangeDisplaySettingsExW(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero); // 一次性生效
    }

    /// <summary>退出虚拟屏：还原原来的显示器布局，停用虚拟显示器</summary>
    static volatile bool _exiting;

    public static async Task ExitAsync(string why = "")
    {
        _exiting = true; // 息屏守护马上停手，不和还原抢
        try { await ExitCoreAsync(why); } finally { _exiting = false; }
    }

    static async Task ExitCoreAsync(string why)
    {
        DISPLAYCONFIG_PATH_INFO[]? paths; DISPLAYCONFIG_MODE_INFO[]? modes;
        lock (Gate) { paths = _savedPaths; modes = _savedModes; _savedPaths = null; _savedModes = null; }
        bool restored = false;
        if (paths != null && modes != null)
        {
            int r = SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES);
            restored = r == 0;
            if (!restored) Log.Error($"还原显示器布局失败：{r}，改用系统记住的布局");
        }
        var id = FindInstanceId();
        if (id != null) await SetDeviceEnabled(id, false);
        if (!restored)
        {
            await Task.Delay(800);
            SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_USE_DATABASE_CURRENT);
        }
        try { File.Delete(ActiveFlag); } catch { }
        _layout = null;
        HostDisplays.InvalidateOrder();
        if (IsActive) Log.Info("虚拟屏息屏：关闭，实体显示器已恢复" + (why != "" ? "（" + why + "）" : ""));
        IsActive = false;
    }

    /// <summary>启动时：上次没还原就退出了（崩溃 / 被结束），现在还原；没在息屏时确保虚拟显示器是停用的（否则会多出一块屏）</summary>
    public static async Task RecoverAsync()
    {
        if (!File.Exists(ActiveFlag))
        {
            var id = FindInstanceId();
            if (id != null) await SetDeviceEnabled(id, false);
            return;
        }
        Log.Info("上次虚拟屏息屏没有正常关闭，现在还原显示器");
        IsActive = true;
        await ExitAsync("启动时还原");
    }

    /// <summary>卸载 Farloop 时：先确保实体屏恢复，再删除虚拟显示器设备和驱动</summary>
    public static async Task UninstallAsync()
    {
        if (IsActive || File.Exists(ActiveFlag)) { IsActive = true; await ExitAsync("卸载"); }
        var id = FindInstanceId();
        if (id != null) await Proc.RunAsync("pnputil", $"/remove-device \"{id}\"", 30_000);
        var (_, o) = await Proc.RunAsync("pnputil", "/enum-drivers", 30_000);
        string? oem = null;
        foreach (var line in o.Split('\n'))
        {
            var t = line.Trim();
            if (t.Contains(".inf", StringComparison.OrdinalIgnoreCase) && t.Contains("oem", StringComparison.OrdinalIgnoreCase)) oem = t.Substring(t.LastIndexOf(' ') + 1);
            if (oem != null && t.Contains("mttvdd.inf", StringComparison.OrdinalIgnoreCase)) { await Proc.RunAsync("pnputil", $"/delete-driver {oem} /uninstall /force", 30_000); break; }
        }
        try { Microsoft.Win32.Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\MikeTheTech\VirtualDisplayDriver", false); } catch { }
    }

    // =========================== 设备查找（SetupAPI） ===========================

    static string? FindInstanceId()
    {
        var g = DisplayClass;
        IntPtr set = SetupDiGetClassDevsW(ref g, null, IntPtr.Zero, 0);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            var buf = new byte[2048];
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref d); i++)
            {
                if (!SetupDiGetDeviceRegistryPropertyW(set, ref d, SPDRP_HARDWAREID, out _, buf, (uint)buf.Length, out uint need)) continue;
                var ids = Encoding.Unicode.GetString(buf, 0, (int)Math.Min(need, (uint)buf.Length)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                if (!ids.Any(x => x.Equals(HwId, StringComparison.OrdinalIgnoreCase) || x.Equals("MttVDD", StringComparison.OrdinalIgnoreCase))) continue;
                var sb = new StringBuilder(512);
                if (SetupDiGetDeviceInstanceIdW(set, ref d, sb, sb.Capacity, out _)) return sb.ToString();
            }
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        return null;
    }

    static string? CreateRootDevice(string inf)
    {
        var g = DisplayClass;
        IntPtr set = SetupDiCreateDeviceInfoList(ref g, IntPtr.Zero);
        if (set == new IntPtr(-1)) return "SetupDiCreateDeviceInfoList 失败";
        try
        {
            var d = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            if (!SetupDiCreateDeviceInfoW(set, "Display", ref g, null, IntPtr.Zero, DICD_GENERATE_ID, ref d))
                return "创建设备失败：" + Marshal.GetLastWin32Error();
            var hw = Encoding.Unicode.GetBytes(HwId + "\0\0");
            if (!SetupDiSetDeviceRegistryPropertyW(set, ref d, SPDRP_HARDWAREID, hw, (uint)hw.Length))
                return "设置硬件 ID 失败：" + Marshal.GetLastWin32Error();
            if (!SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref d))
                return "注册设备失败：" + Marshal.GetLastWin32Error();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
        if (!UpdateDriverForPlugAndPlayDevicesW(IntPtr.Zero, HwId, inf, INSTALLFLAG_FORCE, out _))
            return "安装驱动失败：" + Marshal.GetLastWin32Error();
        return null;
    }

    // =========================== 显示器布局（CCD API） ===========================

    static bool QueryConfig(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        paths = Array.Empty<DISPLAYCONFIG_PATH_INFO>(); modes = Array.Empty<DISPLAYCONFIG_MODE_INFO>();
        for (int tries = 0; tries < 3; tries++)
        {
            if (GetDisplayConfigBufferSizes(flags, out uint np, out uint nm) != 0) return false;
            paths = new DISPLAYCONFIG_PATH_INFO[np]; modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (np == 0) return false;
            int r = QueryDisplayConfig(flags, ref np, paths, ref nm, modes, flags == QDC_DATABASE_CURRENT ? Marshal.AllocHGlobal(4) : IntPtr.Zero);
            if (r == 0) { Array.Resize(ref paths, (int)np); Array.Resize(ref modes, (int)nm); return true; }
            if (r != 122 /*ERROR_INSUFFICIENT_BUFFER*/) return false;
        }
        return false;
    }

    static bool IsVirtualAdapter(LUID adapter, string instanceId)
    {
        var a = AdapterPath(adapter);
        return a != null && a.Contains(instanceId.Replace('\\', '#'), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 只留虚拟显示器在桌面上（做法参考 airhand 的 topology.rs，MIT）：它已经在桌面上就接着用（带上它现在的模式），
    /// 不在就接上一条空着的输出；虚拟屏从 (0,0) 起排，第一个是主屏；本机的显示器全部撤下。
    /// 先用“完整配置”（路径 + 模式）设置，不行再退一步只给路径（拓扑）让 Windows 自己定模式。不存进显示设置数据库。
    /// 以前只用后一种，有的电脑（实测一台笔记本，虚拟屏已经在桌面上时）返回 31
    /// </summary>
    static string? Isolate(string instanceId)
    {
        if (!QueryConfig(QDC_ALL_PATHS, out var paths, out var modes)) return "读取显示器布局失败";
        var chosen = new List<DISPLAYCONFIG_PATH_INFO>();
        var usedSrc = new HashSet<(uint, int, uint)>();
        var usedTgt = new HashSet<(uint, int, uint)>();
        foreach (var p in paths)
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && IsVirtualAdapter(p.targetInfo.adapterId, instanceId))
            {
                chosen.Add(p);
                usedSrc.Add((p.sourceInfo.adapterId.Low, p.sourceInfo.adapterId.High, p.sourceInfo.id));
                usedTgt.Add((p.targetInfo.adapterId.Low, p.targetInfo.adapterId.High, p.targetInfo.id));
            }
        foreach (var p in paths)
        {
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 || p.targetInfo.targetAvailable == 0 || !IsVirtualAdapter(p.targetInfo.adapterId, instanceId)) continue;
            if (usedTgt.Contains((p.targetInfo.adapterId.Low, p.targetInfo.adapterId.High, p.targetInfo.id))) continue;
            if (usedSrc.Contains((p.sourceInfo.adapterId.Low, p.sourceInfo.adapterId.High, p.sourceInfo.id))) continue;
            var q = p;
            q.flags |= DISPLAYCONFIG_PATH_ACTIVE;
            q.sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            q.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            usedSrc.Add((q.sourceInfo.adapterId.Low, q.sourceInfo.adapterId.High, q.sourceInfo.id));
            usedTgt.Add((q.targetInfo.adapterId.Low, q.targetInfo.adapterId.High, q.targetInfo.id));
            chosen.Add(q);
        }
        if (chosen.Count == 0) return "虚拟显示器没有接上";

        // 只带上留下的路径用到的模式（重新编号），源模式从 (0,0) 往右排
        var newModes = new List<DISPLAYCONFIG_MODE_INFO>();
        int x = 0;
        var arr = chosen.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            uint Remap(uint idx)
            {
                if (idx == DISPLAYCONFIG_PATH_MODE_IDX_INVALID || idx >= modes.Length) return DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                var m = modes[idx];
                if (m.infoType == DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) { m.srcPosX = x; m.srcPosY = 0; x += (int)m.srcWidth; }
                newModes.Add(m);
                return (uint)(newModes.Count - 1);
            }
            arr[i].sourceInfo.modeInfoIdx = Remap(arr[i].sourceInfo.modeInfoIdx);
            arr[i].targetInfo.modeInfoIdx = Remap(arr[i].targetInfo.modeInfoIdx);
        }
        int r = SetDisplayConfig((uint)arr.Length, arr, (uint)newModes.Count, newModes.Count > 0 ? newModes.ToArray() : null,
            SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
        if (r == 0) return null;
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i].sourceInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            arr[i].targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        }
        int again = SetDisplayConfig((uint)arr.Length, arr, 0, null, SDC_APPLY | SDC_TOPOLOGY_SUPPLIED | SDC_ALLOW_PATH_ORDER_CHANGES | SDC_SAVE_TO_DATABASE);
        return again == 0 ? null : $"SetDisplayConfig {r}，退一步 {again}";
    }

    /// <summary>桌面上的虚拟屏的 GDI 名（\\.\DISPLAYn），设分辨率用</summary>
    static string? ActiveVirtualGdi(string instanceId)
    {
        if (!QueryConfig(QDC_ONLY_ACTIVE_PATHS, out var paths, out _) && !QueryConfig(QDC_ALL_PATHS, out paths, out _)) return null;
        foreach (var p in paths)
            if ((p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0 && IsVirtualAdapter(p.targetInfo.adapterId, instanceId)) return SourceGdiName(p);
        return null;
    }

    static DISPLAYCONFIG_PATH_INFO? FindVirtualPath(string instanceId)
    {
        if (!QueryConfig(QDC_ALL_PATHS, out var paths, out _)) return null;
        var want = instanceId.Replace('\\', '#');
        foreach (var p in paths)
        {
            if (p.targetInfo.targetAvailable == 0) continue;
            var a = AdapterPath(p.targetInfo.adapterId);
            if (a != null && a.Contains(want, StringComparison.OrdinalIgnoreCase)) return p;
        }
        return null;
    }

    static (int W, int H) LargestActiveResolution(DISPLAYCONFIG_PATH_INFO[] paths)
    {
        int bw = 1920, bh = 1080;
        var names = new List<string>();
        foreach (var p in paths) { var g = SourceGdiName(p); if (g != null) names.Add(g); }
        for (uint i = 0; ; i++)
        {
            var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
            if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
            if ((dd.StateFlags & 1) != 0 && !names.Contains(dd.DeviceName)) names.Add(dd.DeviceName); // ATTACHED_TO_DESKTOP
        }
        foreach (var gdi in names)
        {
            var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
            if (EnumDisplaySettingsW(gdi, ENUM_CURRENT_SETTINGS, ref dm) && dm.dmPelsWidth * dm.dmPelsHeight > bw * bh) { bw = dm.dmPelsWidth; bh = dm.dmPelsHeight; }
        }
        return (bw, bh);
    }

    static void SetResolution(string gdi, int w, int h)
    {
        var dm = new DEVMODE { dmSize = (short)Marshal.SizeOf<DEVMODE>() };
        if (!EnumDisplaySettingsW(gdi, ENUM_CURRENT_SETTINGS, ref dm)) return;
        if (dm.dmPelsWidth == w && dm.dmPelsHeight == h) return;
        dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = 60;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        int r = ChangeDisplaySettingsExW(gdi, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
        if (r != 0) Log.Info($"虚拟屏设为 {w}×{h} 失败：{r}");
    }

    static string? SourceGdiName(DISPLAYCONFIG_PATH_INFO p)
    {
        var s = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
        s.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
        s.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>();
        s.header.adapterId = p.sourceInfo.adapterId;
        s.header.id = p.sourceInfo.id;
        return DisplayConfigGetDeviceInfo(ref s) == 0 ? s.viewGdiDeviceName : null;
    }

    static string? AdapterPath(LUID adapter)
    {
        var a = new DISPLAYCONFIG_ADAPTER_NAME();
        a.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME;
        a.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>();
        a.header.adapterId = adapter;
        return DisplayConfigGetDeviceInfo(ref a) == 0 ? a.adapterDevicePath : null;
    }

    // =========================== P/Invoke ===========================

    const uint QDC_ALL_PATHS = 1, QDC_ONLY_ACTIVE_PATHS = 2, QDC_DATABASE_CURRENT = 4;
    const uint SDC_TOPOLOGY_SUPPLIED = 0x10, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_APPLY = 0x80,
               SDC_ALLOW_CHANGES = 0x400, SDC_ALLOW_PATH_ORDER_CHANGES = 0x2000, SDC_USE_DATABASE_CURRENT = 0x0F, SDC_SAVE_TO_DATABASE = 0x200;
    const uint DISPLAYCONFIG_PATH_ACTIVE = 1, DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;
    const uint DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME = 1, DISPLAYCONFIG_DEVICE_INFO_GET_ADAPTER_NAME = 4;
    const int ENUM_CURRENT_SETTINGS = -1, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, CDS_UPDATEREGISTRY = 1;
    const int DM_POSITION = 0x20, CDS_SET_PRIMARY = 0x10, CDS_NORESET = 0x10000000;
    const uint DICD_GENERATE_ID = 1, SPDRP_HARDWAREID = 1, DIF_REGISTERDEVICE = 0x19, INSTALLFLAG_FORCE = 1;
    const uint DIF_PROPERTYCHANGE = 0x12, DICS_ENABLE = 1, DICS_DISABLE = 2, DICS_FLAG_GLOBAL = 1;
    const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;

    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct DISPLAYCONFIG_RATIONAL { public uint n, d; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public uint srcWidth;
        [FieldOffset(20)] public uint srcHeight;
        [FieldOffset(24)] public uint srcPixelFormat;
        [FieldOffset(28)] public int srcPosX;
        [FieldOffset(32)] public int srcPosY;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct SP_PROPCHANGE_PARAMS { public uint cbSize, InstallFunction, StateChange, Scope, HwProfile; }
    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_ADAPTER_NAME { public DISPLAYCONFIG_DEVICE_INFO_HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string adapterDevicePath; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString; public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevicesW(string? dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths, ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
    [DllImport("user32.dll")] static extern int SetDisplayConfig(uint numPaths, DISPLAYCONFIG_PATH_INFO[]? paths, uint numModes, DISPLAYCONFIG_MODE_INFO[]? modes, uint flags);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettingsW(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string dev, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsExW(string? dev, IntPtr dm, IntPtr hwnd, int flags, IntPtr param);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr SetupDiGetClassDevsW(ref Guid g, string? enumerator, IntPtr hwnd, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid g, IntPtr hwnd);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint i, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, uint prop, out uint type, byte[] buf, uint size, out uint need);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA d, uint prop, byte[] buf, uint size);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA d, StringBuilder id, int size, out int need);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string name, ref Guid g, string? desc, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiCallClassInstaller(uint fn, IntPtr set, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiOpenDeviceInfoW(IntPtr set, string instanceId, IntPtr hwnd, uint flags, ref SP_DEVINFO_DATA d);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool SetupDiSetClassInstallParamsW(IntPtr set, ref SP_DEVINFO_DATA d, ref SP_PROPCHANGE_PARAMS p, uint size);
    [DllImport("newdev.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool UpdateDriverForPlugAndPlayDevicesW(IntPtr hwnd, string hwid, string inf, uint flags, out bool reboot);
}
