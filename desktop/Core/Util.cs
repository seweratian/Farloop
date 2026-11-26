using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeRemote.Core;

public static class Log
{
    static readonly object Gate = new();

    public static void Info(string m) => Write("INFO", m);
    public static void Error(string m, Exception? e = null) => Write("ERR ", e == null ? m : $"{m}: {e}");

    public static string FilePath => Path.Combine(AppConfig.DataDir, "log.txt");

    static void Write(string level, string m)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.DataDir);
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024) fi.Delete();
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {m}\r\n");
            }
        }
        catch { }
    }
}

public static class B64
{
    public static string Enc(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Dec(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}

public static class Sync
{
    /// <summary>
    /// 在界面线程上同步等一个异步操作（卸载这类没有窗口可回的流程）。
    /// 必须放到线程池里跑：直接 .Wait() / GetResult() 时，里面每个 await 都要回到界面线程继续，
    /// 而界面线程正卡在等它，互相等死（0.23.0 及以前从“应用”或 Geek Uninstaller 卸载会一直卡住）。
    /// 返回 false = 超时；操作抛的异常原样抛出。
    /// </summary>
    public static bool Run(Func<Task> work, int timeoutMs)
    {
        var t = Task.Run(work);
        // 等完成但不抛（Task.Wait 会把异常包成 AggregateException），再用 GetResult 抛出原始异常
        if (!((IAsyncResult)t).AsyncWaitHandle.WaitOne(timeoutMs)) return false;
        t.GetAwaiter().GetResult();
        return true;
    }
}

public static class Proc
{
    public static async Task<(int Code, string Output)> RunAsync(string exe, string args, int timeoutMs = 120_000, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (workDir != null) psi.WorkingDirectory = workDir;
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeoutMs);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, "timeout");
        }
        return (p.ExitCode, (await o) + (await e));
    }
}
