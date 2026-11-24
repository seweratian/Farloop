using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// Farloop 自己的更新：查 GitHub 仓库的最新 Release，下载里面的 Farloop.exe，启动新版本并退出旧版本
/// （新版本等旧版本退出后照常把自己装进 Program Files\Farloop）。组网不停，远程连接只断一下就恢复。
/// </summary>
public static class SelfUpdate
{
    public const string Repo = "seweratian/Farloop";
    public const string AssetName = "Farloop.exe";
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    public sealed record Release(string Version, string Url, long Size, string Notes);

    static HttpClient Http()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Farloop/" + PeerSession.Version);
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    /// <summary>GitHub 上最新的正式版（没有或读不到返回 null）</summary>
    public static async Task<Release?> LatestAsync()
    {
        using var http = Http();
        var json = await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            return new Release(tag.TrimStart('v', 'V'), a.GetProperty("browser_download_url").GetString() ?? "", a.GetProperty("size").GetInt64(), notes);
        }
        return null;
    }

    public static bool IsNewer(Release r) => Updater.IsNewer(r.Version, PeerSession.Version);

    /// <summary>下载新版本并重启到新版本。返回 null = 已在重启；否则是错误说明</summary>
    public static async Task<string?> InstallAsync(Release r, IProgress<string>? progress = null)
    {
        var dir = Path.Combine(AppConfig.DataDir, "update");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, $"Farloop-{r.Version}.exe");
        var tmp = file + ".part";
        try
        {
            using var http = Http();
            using var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(tmp))
            {
                var buf = new byte[1 << 16];
                long done = 0; int n; var last = DateTime.MinValue;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n));
                    done += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 500)
                    {
                        last = DateTime.UtcNow;
                        progress?.Report(r.Size > 0 ? $"正在下载 Farloop {r.Version}… {done * 100 / r.Size}%" : $"正在下载 Farloop {r.Version}… {done / 1048576} MB");
                    }
                }
            }
            if (r.Size > 0 && new FileInfo(tmp).Length != r.Size) return "下载不完整，请重试";
            File.Move(tmp, file, overwrite: true);
        }
        catch (Exception e)
        {
            try { File.Delete(tmp); } catch { }
            Log.Error("下载更新失败", e);
            return "下载失败：" + e.Message + $"（也可以手动下载：{ReleasesPage}）";
        }

        Log.Info($"已下载 Farloop {r.Version}，重启到新版本");
        progress?.Report("正在重启到新版本…");
        var psi = new ProcessStartInfo(file) { UseShellExecute = true };
        psi.ArgumentList.Add("--after-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(psi);
        return null;
    }

    /// <summary>新版本启动时：等旧版本退出（最多 30 秒），再继续正常启动</summary>
    public static void WaitForOldVersion(string[] args)
    {
        int i = Array.IndexOf(args, "--after-update");
        if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var pid)) return;
        try { using var p = Process.GetProcessById(pid); p.WaitForExit(30_000); } catch { }
        // 清掉更早下载的安装包（当前这个正在运行，删不掉就留着）
        try
        {
            foreach (var f in Directory.EnumerateFiles(Path.Combine(AppConfig.DataDir, "update")))
                if (!string.Equals(Path.GetFullPath(f), Path.GetFullPath(Environment.ProcessPath ?? ""), StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
