using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// Farloop 定制版 Sunshine：在官方 Sunshine 上加了“串流中途改码率”（控制通道 0x55F0，NVENC 直接重配置，不出关键帧），
/// 手机端据此做码率自适应（思路参考 airhand）。源码补丁在 Farloop 源码包的 sunshine-patch 目录。
///
/// 只替换 sunshine.exe 一个文件，而且只在已安装的 Sunshine 正好是补丁所基于的官方版本时替换（网页、配置等其他文件都是同一版本的）；
/// 原来的 exe 备份为 sunshine.exe.orig，卸载 Farloop 时还原。
/// </summary>
public static class SunshineFarloop
{
    /// <summary>补丁基于的官方版本（winget 上的 LizardByte.Sunshine 也装这个版本）</summary>
    public const string BaseVersion = "2026.914.233613";
    const string Resource = "sunshine-farloop";

    static string Backup => Sunshine.Exe + ".orig";

    static string? _ourHash;
    static (DateTime, long, bool)? _cache;

    public static bool Embedded => typeof(SunshineFarloop).Assembly.GetManifestResourceInfo(Resource) != null;

    static string OurHash
    {
        get
        {
            if (_ourHash != null) return _ourHash;
            using var s = typeof(SunshineFarloop).Assembly.GetManifestResourceStream(Resource)!;
            return _ourHash = Convert.ToHexString(SHA256.HashData(s));
        }
    }

    /// <summary>装着的就是 Farloop 定制版（支持码率自适应）</summary>
    public static bool Active
    {
        get
        {
            try
            {
                if (!Embedded || !Sunshine.Installed) return false;
                var fi = new FileInfo(Sunshine.Exe);
                if (_cache is { } c && c.Item1 == fi.LastWriteTimeUtc && c.Item2 == fi.Length) return c.Item3;
                bool ok;
                using (var f = File.OpenRead(Sunshine.Exe)) ok = Convert.ToHexString(SHA256.HashData(f)) == OurHash;
                _cache = (fi.LastWriteTimeUtc, fi.Length, ok);
                return ok;
            }
            catch { return false; }
        }
    }

    public static string? InstalledVersion
    {
        get { try { return FileVersionInfo.GetVersionInfo(Sunshine.Exe).ProductVersion?.Trim(); } catch { return null; } }
    }

    /// <summary>需要时把 sunshine.exe 换成定制版。返回 null = 已是定制版；否则是没换的原因（写日志用）</summary>
    public static async Task<string?> EnsureAsync()
    {
        if (!Embedded) return "安装包里没有定制版 Sunshine";
        if (!Sunshine.Installed) return "没有安装 Sunshine";
        if (Active) return null;
        var v = InstalledVersion;
        if (v != BaseVersion) return $"已安装的 Sunshine 是 {v}，定制版基于 {BaseVersion}，不替换（码率自适应不可用）";

        Log.Info("把 Sunshine 换成 Farloop 定制版（支持码率自适应）");
        await Proc.RunAsync("net", "stop SunshineService", 30_000);
        for (int i = 0; i < 20 && Sunshine.IsRunning; i++) await Task.Delay(500);
        foreach (var p in Process.GetProcessesByName("sunshine")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        try
        {
            if (!File.Exists(Backup)) File.Copy(Sunshine.Exe, Backup);
            var tmp = Sunshine.Exe + ".new";
            using (var src = typeof(SunshineFarloop).Assembly.GetManifestResourceStream(Resource)!)
            using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);
            File.Move(tmp, Sunshine.Exe, overwrite: true);
            _cache = null;
        }
        catch (Exception e)
        {
            Log.Error("替换 Sunshine 失败", e);
            return "替换失败：" + e.Message;
        }
        finally
        {
            await Proc.RunAsync("net", "start SunshineService", 30_000);
        }
        for (int i = 0; i < 50 && !Sunshine.IsRunning; i++) await Task.Delay(500);
        if (!Sunshine.IsRunning)
        {
            // 定制版起不来：换回官方版，绝不能让人连不上。再看看官方版能不能起来，分清是谁的问题
            var why = await Sunshine.DiagnoseAsync();
            Log.Error("定制版 Sunshine 没有启动，换回官方版。" + why);
            await RestoreAsync();
            for (int i = 0; i < 50 && !Sunshine.IsRunning; i++) await Task.Delay(500);
            return Sunshine.IsRunning
                ? "定制版启动失败，已换回官方版（官方版正常）：" + why
                : "Sunshine 本身就启动不了（官方版也一样，不是定制版的问题）：" + why;
        }
        Log.Info("已换成 Farloop 定制版 Sunshine");
        return null;
    }

    /// <summary>换回官方版（卸载 Farloop、定制版出问题时）</summary>
    public static async Task RestoreAsync()
    {
        if (!File.Exists(Backup)) return;
        await Proc.RunAsync("net", "stop SunshineService", 30_000);
        for (int i = 0; i < 20 && Sunshine.IsRunning; i++) await Task.Delay(500);
        foreach (var p in Process.GetProcessesByName("sunshine")) { try { p.Kill(); p.WaitForExit(3000); } catch { } }
        try { File.Copy(Backup, Sunshine.Exe, overwrite: true); File.Delete(Backup); _cache = null; Log.Info("已换回官方版 Sunshine"); }
        catch (Exception e) { Log.Error("换回官方版 Sunshine 失败", e); }
        await Proc.RunAsync("net", "start SunshineService", 30_000);
    }
}
