using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace FreeRemote.Core;

/// <summary>
/// 文件 / 文件夹传输。走同一条加密会话：FileBegin → 若干 FileChunk → FileEnd。
/// 一次发送的多个文件属于同一个 Batch；ForClipboard 的 Batch 收完后放进对方剪贴板，可直接粘贴。
/// </summary>
public sealed class TransferManager
{
    const int ChunkSize = 256 * 1024;

    public interface IHost
    {
        string ReceiveFolder { get; }
        /// <summary>拖放落点对应的文件夹；null = 用默认接收文件夹</summary>
        string? ResolveDropFolder(PeerSession from, FileBeginMsg first);
        void AddTransfer(TransferVM vm);
        void OnBatchReceived(PeerSession from, IReadOnlyList<string> roots, string folder, bool forClipboard);
    }

    sealed class RecvFile
    {
        public required FileStream Stream;
        public required string PartPath;
        public required string FinalPath;
        public required RecvBatch Batch;
    }

    sealed class RecvBatch
    {
        public required PeerSession From;
        public required string BaseDir;
        public required bool ForClipboard;
        public required TransferVM VM;
        public required int Total;
        public int Done;
        public readonly Dictionary<string, string> RootMap = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly IHost _host;
    readonly ConcurrentDictionary<Guid, RecvFile> _files = new();
    readonly ConcurrentDictionary<Guid, RecvBatch> _batches = new();

    public TransferManager(IHost host) { _host = host; }

    // ===================== 发送 =====================
    public static List<(string Full, string Root, string Rel)> Expand(IEnumerable<string> paths)
    {
        var list = new List<(string, string, string)>();
        foreach (var raw in paths)
        {
            var p = raw.TrimEnd('\\', '/');
            if (File.Exists(p))
            {
                var n = Path.GetFileName(p);
                list.Add((p, n, n));
            }
            else if (Directory.Exists(p))
            {
                var root = Path.GetFileName(p);
                if (root == "") continue; // 整个盘符不支持
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    list.Add((f, root, root + "/" + Path.GetRelativePath(p, f).Replace('\\', '/')));
            }
        }
        return list;
    }

    public async Task SendAsync(PeerSession s, IReadOnlyList<string> paths, bool forClipboard,
        float dropX = -1, float dropY = -1, string? token = null)
    {
        var files = Expand(paths);
        if (files.Count == 0) return;
        long total = files.Sum(f => new FileInfo(f.Full).Length);
        var roots = files.Select(f => f.Root).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var title = roots.Count == 1 ? roots[0] : $"{roots[0]} 等 {roots.Count} 项";
        var vm = new TransferVM(title, (forClipboard ? "剪贴板 → " : "发送到 ") + s.RemoteName, total, true, s.RemoteIp, forClipboard);
        _host.AddTransfer(vm);

        var batch = Guid.NewGuid();
        var buf = new byte[16 + ChunkSize];
        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                var (full, root, rel) = files[i];
                var id = Guid.NewGuid();
                await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, true);
                await s.SendJsonAsync(MsgType.FileBegin, new FileBeginMsg(id, batch, root, rel, fs.Length, i, files.Count, total, forClipboard, dropX, dropY, token));
                id.TryWriteBytes(buf.AsSpan(0, 16));
                int n;
                while ((n = await fs.ReadAsync(buf.AsMemory(16, ChunkSize))) > 0)
                {
                    await s.SendAsync(MsgType.FileChunk, buf.AsMemory(0, 16 + n));
                    vm.Add(n);
                }
                await s.SendJsonAsync(MsgType.FileEnd, new FileEndMsg(id));
            }
            vm.Complete();
        }
        catch (Exception e)
        {
            Log.Error("发送文件失败", e);
            vm.Fail(e.Message);
            try { await s.SendJsonAsync(MsgType.FileAbort, new FileAbortMsg(batch, e.Message)); } catch { }
        }
    }

    // ===================== 接收（在网络线程上同步执行） =====================
    public void Handle(PeerSession from, MsgType type, byte[] payload)
    {
        switch (type)
        {
            case MsgType.FileBegin: OnBegin(from, Frames.FromJson<FileBeginMsg>(payload)); break;
            case MsgType.FileChunk: OnChunk(payload); break;
            case MsgType.FileEnd: OnEnd(Frames.FromJson<FileEndMsg>(payload).Id); break;
            case MsgType.FileAbort:
                var a = Frames.FromJson<FileAbortMsg>(payload);
                AbortBatch(a.Batch, "对方取消：" + a.Reason);
                break;
        }
    }

    void OnBegin(PeerSession from, FileBeginMsg m)
    {
        var batch = _batches.GetOrAdd(m.Batch, _ =>
        {
            var baseDir = m.ForClipboard
                ? Path.Combine(Path.GetTempPath(), "FarloopClip", m.Batch.ToString("N")[..8])
                : (m.DropX >= 0 || m.Token != null || m.DestDir != null ? _host.ResolveDropFolder(from, m) : null) ?? _host.ReceiveFolder;
            Directory.CreateDirectory(baseDir);
            var vm = new TransferVM(m.Root, (m.ForClipboard ? "剪贴板 ← " : "来自 ") + from.RemoteName, m.BatchBytes, false, from.RemoteIp, m.ForClipboard) { OpenPath = baseDir };
            _host.AddTransfer(vm);
            return new RecvBatch { From = from, BaseDir = baseDir, ForClipboard = m.ForClipboard, VM = vm, Total = m.Total };
        });
        if (batch.From != from) throw new InvalidDataException("批次来源不一致");

        var segs = SafeSegments(m.RelPath);
        if (segs.Length == 0 || !segs[0].Equals(SafeSegments(m.Root).FirstOrDefault(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("非法路径：" + m.RelPath);

        // 同名时自动改名为 “xxx (1)”，不覆盖已有文件
        if (!batch.RootMap.TryGetValue(segs[0], out var mapped))
        {
            mapped = UniqueName(batch.BaseDir, segs[0]);
            batch.RootMap[segs[0]] = mapped;
            if (batch.RootMap.Count == 1) batch.VM.Title = mapped;
            else batch.VM.Title = $"{batch.RootMap.Values.First()} 等 {batch.RootMap.Count} 项";
        }
        segs[0] = mapped;

        var final = Path.GetFullPath(Path.Combine(new[] { batch.BaseDir }.Concat(segs).ToArray()));
        var baseFull = Path.GetFullPath(batch.BaseDir).TrimEnd('\\') + "\\";
        if (!final.StartsWith(baseFull, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("非法路径");

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        var part = final + ".frpart";
        var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        fs.SetLength(0);
        _files[m.Id] = new RecvFile { Stream = fs, PartPath = part, FinalPath = final, Batch = batch };
    }

    void OnChunk(byte[] payload)
    {
        if (payload.Length < 16) return;
        var id = new Guid(payload.AsSpan(0, 16));
        if (!_files.TryGetValue(id, out var f)) return;
        f.Stream.Write(payload, 16, payload.Length - 16);
        f.Batch.VM.Add(payload.Length - 16);
    }

    void OnEnd(Guid id)
    {
        if (!_files.TryRemove(id, out var f)) return;
        f.Stream.Dispose();
        File.Move(f.PartPath, f.FinalPath, true);
        var b = f.Batch;
        if (++b.Done < b.Total) return;

        b.VM.Complete();
        foreach (var kv in _batches.Where(kv => kv.Value == b).ToList()) _batches.TryRemove(kv.Key, out _);
        var roots = b.RootMap.Values.Select(r => Path.Combine(b.BaseDir, r)).ToList();
        _host.OnBatchReceived(b.From, roots, b.BaseDir, b.ForClipboard);
    }

    void AbortBatch(Guid batchId, string reason)
    {
        if (!_batches.TryRemove(batchId, out var b)) return;
        CleanupBatch(b, reason);
    }

    /// <summary>会话断开时，清理这个会话还没收完的文件</summary>
    public void AbortFrom(PeerSession s)
    {
        foreach (var kv in _batches.Where(kv => kv.Value.From == s).ToList())
            if (_batches.TryRemove(kv.Key, out var b)) CleanupBatch(b, "连接已断开");
    }

    void CleanupBatch(RecvBatch b, string reason)
    {
        foreach (var kv in _files.Where(kv => kv.Value.Batch == b).ToList())
        {
            if (!_files.TryRemove(kv.Key, out var f)) continue;
            try { f.Stream.Dispose(); File.Delete(f.PartPath); } catch { }
        }
        b.VM.Fail(reason);
    }

    static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>拆分相对路径并拒绝 ..、盘符、非法字符，防止对方把文件写到接收目录外面</summary>
    public static string[] SafeSegments(string rel)
    {
        var segs = rel.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var s in segs)
            if (s == "." || s == ".." || s.IndexOfAny(Invalid) >= 0 || s.EndsWith('.') || s.EndsWith(' '))
                throw new InvalidDataException("非法路径：" + rel);
        return segs;
    }

    static string UniqueName(string dir, string name)
    {
        if (!File.Exists(Path.Combine(dir, name)) && !Directory.Exists(Path.Combine(dir, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            var n = $"{stem} ({i}){ext}";
            if (!File.Exists(Path.Combine(dir, n)) && !Directory.Exists(Path.Combine(dir, n))) return n;
        }
    }
}
