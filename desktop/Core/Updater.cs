using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media;

namespace FreeRemote.Core;

/// <summary>一个可更新的组件（设置页里的一行）</summary>
public sealed class ComponentVM : Observable
{
    public ComponentVM(string key, string name, string role) { Key = key; Name = name; Role = role; }
    public string Key { get; }
    public string Name { get; }
    public string Role { get; }

    string? _installed;
    public string? Installed { get => _installed; set { if (Set(ref _installed, value)) RaiseAll(); } }

    string? _latest;
    public string? Latest { get => _latest; set { if (Set(ref _latest, value)) RaiseAll(); } }

    string _status = "";
    public string Status { get => _status; set { if (Set(ref _status, value)) RaiseAll(); } }

    bool _busy;
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RaiseAll(); } }

    public bool HasUpdate => !Busy && Installed != null && Latest != null && Updater.IsNewer(Latest, Installed);
    public string VersionText => Installed == null ? "未安装" : HasUpdate ? $"{Installed}  →  {Latest}" : Installed;
    public string StatusText => Status != "" ? Status : Installed == null ? "" : HasUpdate ? "有新版本" : Latest != null ? "已是最新" : "";
    public Brush StatusBrush => HasUpdate ? Palette.Blue : Palette.Gray;

    void RaiseAll()
    {
        Raise(nameof(HasUpdate)); Raise(nameof(VersionText)); Raise(nameof(StatusText)); Raise(nameof(StatusBrush));
    }
}

/// <summary>
/// 组件更新：Sunshine / Moonlight 走 winget（和安装时一致），EasyTier 查 GitHub 最新发行版后替换文件。
/// </summary>
public static class Updater
{
    public const string SunshineId = "LizardByte.Sunshine";
    public const string MoonlightId = "MoonlightGameStreamingProject.Moonlight";

    // ---------------- winget ----------------
    /// <summary>
    /// 解析 winget list 的表格。列为 名称 / ID / 版本 / [可用] / 源（有更新时才有“可用”列，表头随系统语言变化），
    /// 所以按 ID 所在位置取后面的字段，不依赖表头文字。名称里可能有空格，所以不能从左边数。
    /// </summary>
    public static (string? Installed, string? Available) ParseWingetList(string output, string id)
    {
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            var tokens = Regex.Split(raw.Trim(), @"\s+").Where(t => t != "").ToList();
            int i = tokens.FindIndex(t => t.Equals(id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) continue;
            var rest = tokens.Skip(i + 1).Where(t => t != "<" && t != ">").ToList();
            if (rest.Count == 0) return (null, null);
            string installed = rest[0];
            string? available = rest.Count >= 3 && LooksLikeVersion(rest[1]) ? rest[1] : null;
            return (installed, available);
        }
        return (null, null);
    }

    static bool LooksLikeVersion(string s) => Regex.IsMatch(s, @"^v?\d+(\.\d+)+");

    public static async Task<(string? Installed, string? Available)> WingetQueryAsync(string id)
    {
        var (_, output) = await Proc.RunAsync("winget", $"list --id {id} -e --accept-source-agreements --disable-interactivity", 90_000);
        return ParseWingetList(output, id);
    }

    public static async Task<(bool Ok, string Message)> WingetUpgradeAsync(string id)
    {
        var (code, _) = await Proc.RunAsync("winget",
            $"upgrade --id {id} -e --silent --accept-source-agreements --accept-package-agreements --disable-interactivity", 20 * 60_000);
        Log.Info($"winget upgrade {id} 退出码 {code}");
        var (installed, available) = await WingetQueryAsync(id);
        bool ok = installed != null && available == null;
        return (ok, ok ? $"已更新到 {installed}" : $"更新失败（winget 退出码 {code}）");
    }

    // ---------------- EasyTier ----------------
    public static async Task<string?> EasyTierInstalledAsync()
    {
        if (!EasyTier.Installed) return null;
        var (_, o) = await Proc.RunAsync(EasyTier.CoreExe, "--version", 10_000);
        var m = Regex.Match(o, @"(\d+\.\d+\.\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Farloop");
        return h;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }

    // ---------------- 版本比较 ----------------
    /// <summary>按数字段比较：2026.914.233613 vs 2026.1002.1；6.2.0.0 vs 6.2.1；忽略 v 前缀和 -后缀</summary>
    public static bool IsNewer(string candidate, string current)
    {
        static int[] Parts(string v) => Regex.Match(v.TrimStart('v', 'V'), @"^[\d.]+").Value
            .Split('.', StringSplitOptions.RemoveEmptyEntries).Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var a = Parts(candidate);
        var b = Parts(current);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}
