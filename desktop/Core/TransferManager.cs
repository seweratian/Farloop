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
        public required long Size;
    }

    sealed class RecvBatch
    {
        public required Guid Id;
        public required PeerSession From;
        public required string BaseDir;
        public required bool ForClipboard;
        public required TransferVM VM;
        public required int Total;
        public int Done;
        public int Started;
        public long CommittedBytes;
        public readonly Dictionary<string, string> RootMap = new(StringComparer.OrdinalIgnoreCase);
    }

    readonly IHost _host;
    readonly ConcurrentDictionary<(PeerSession From, Guid Id), RecvFile> _files = new();
    readonly ConcurrentDictionary<(PeerSession From, Guid Id), RecvBatch> _batches = new();

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
        var sizes = files.Select(f => new FileInfo(f.Full).Length).ToArray();
        long total = sizes.Sum();
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
                long size = sizes[i];
                if (fs.Length != size) throw new IOException($"文件大小已改变：{rel}");
                await s.SendJsonAsync(MsgType.FileBegin, new FileBeginMsg(id, batch, root, rel, size, i, files.Count, total, forClipboard, dropX, dropY, token));
                id.TryWriteBytes(buf.AsSpan(0, 16));
                long sent = 0;
                int n;
                while ((n = await fs.ReadAsync(buf.AsMemory(16, ChunkSize))) > 0)
                {
                    if (n > size - sent) throw new IOException($"文件大小已改变：{rel}");
                    await s.SendAsync(MsgType.FileChunk, buf.AsMemory(0, 16 + n));
                    sent += n;
                    vm.Add(n);
                }
                if (sent != size || fs.Length != size) throw new IOException($"文件大小已改变：{rel}");
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
            case MsgType.FileBegin:
                var m = Frames.FromJson<FileBeginMsg>(payload);
                try { OnBegin(from, m); }
                catch (Exception e) { AbortBatch(from, m.Batch, e.Message); throw; }
                break;
            case MsgType.FileChunk:
            case MsgType.FileEnd:
                var id = type == MsgType.FileEnd ? Frames.FromJson<FileEndMsg>(payload).Id : new Guid(payload.AsSpan(0, 16));
                if (!_files.TryGetValue((from, id), out var file)) break;
                try
                {
                    if (type == MsgType.FileEnd) OnEnd(id, file);
                    else OnChunk(payload, file);
                }
                catch (Exception e) { AbortBatch(from, file.Batch.Id, e.Message); throw; }
                break;
            case MsgType.FileAbort:
                var a = Frames.FromJson<FileAbortMsg>(payload);
                AbortBatch(from, a.Batch, "对方取消：" + a.Reason);
                break;
        }
    }

    void OnBegin(PeerSession from, FileBeginMsg m)
    {
        if (m.Index != 0 && !_batches.ContainsKey((from, m.Batch))) return;
        var batch = _batches.GetOrAdd((from, m.Batch), _ =>
        {
            var baseDir = m.ForClipboard
                ? Path.Combine(Path.GetTempPath(), "FarloopClip", m.Batch.ToString("N")[..8])
                : (m.DropX >= 0 || m.Token != null || m.DestDir != null ? _host.ResolveDropFolder(from, m) : null) ?? _host.ReceiveFolder;
            Directory.CreateDirectory(baseDir);
            var vm = new TransferVM(m.Root, (m.ForClipboard ? "剪贴板 ← " : "来自 ") + from.RemoteName, m.BatchBytes, false, from.RemoteIp, m.ForClipboard) { OpenPath = baseDir };
            _host.AddTransfer(vm);
            return new RecvBatch { Id = m.Batch, From = from, BaseDir = baseDir, ForClipboard = m.ForClipboard, VM = vm, Total = m.Total };
        });
        if (m.Size < 0 || m.BatchBytes < 0 || m.Total <= 0 || m.Total != batch.Total ||
            m.Index != batch.Started || m.Index >= batch.Total ||
            m.BatchBytes != batch.VM.Total || m.Size > m.BatchBytes)
            throw new InvalidDataException("文件传输信息不一致");

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
        var part = Path.Combine(Path.GetDirectoryName(final)!, Guid.NewGuid().ToString("N") + ".frpart");
        lock (batch)
        {
            if (!from.IsOpen) throw new IOException("连接已断开");
            if (_files.ContainsKey((from, m.Id))) throw new InvalidDataException("文件编号重复");
            var fs = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
            _files[(from, m.Id)] = new RecvFile { Stream = fs, PartPath = part, FinalPath = final, Batch = batch, Size = m.Size };
            batch.Started++;
        }
    }

    static void OnChunk(byte[] payload, RecvFile f)
    {
        int count = payload.Length - 16;
        lock (f.Batch)
        {
            if (count > f.Size - f.Stream.Position) throw new InvalidDataException("文件超过声明大小");
            f.Stream.Write(payload, 16, count);
            f.Batch.VM.Add(count);
        }
    }

    void OnEnd(Guid id, RecvFile f)
    {
        var b = f.Batch;
        lock (b)
        {
            if (f.Stream.Position != f.Size) throw new InvalidDataException("接收的文件大小与声明不符");
            long remaining = b.VM.Total - b.CommittedBytes;
            if (f.Size > remaining || (b.Done + 1 == b.Total && f.Size != remaining))
                throw new InvalidDataException("接收的批次大小与声明不符");
            f.Stream.Dispose();
            File.Move(f.PartPath, f.FinalPath);
            _files.TryRemove((b.From, id), out _);
            b.CommittedBytes += f.Size;
            if (++b.Done < b.Total) return;
            if (!_batches.TryRemove((b.From, b.Id), out _)) return;
        }

        b.VM.Complete();
        var roots = b.RootMap.Values.Select(r => Path.Combine(b.BaseDir, r)).ToList();
        _host.OnBatchReceived(b.From, roots, b.BaseDir, b.ForClipboard);
    }

    void AbortBatch(PeerSession from, Guid batchId, string reason)
    {
        if (!_batches.TryRemove((from, batchId), out var b)) return;
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
        lock (b)
        {
            foreach (var kv in _files.Where(kv => kv.Value.Batch == b).ToList())
            {
                if (!_files.TryRemove(kv.Key, out var f)) continue;
                try { f.Stream.Dispose(); } catch { }
                try { File.Delete(f.PartPath); } catch (Exception e) { Log.Error("清理未完成的文件失败", e); }
            }
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
