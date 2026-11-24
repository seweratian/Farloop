using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FreeRemote.Core;

/// <summary>
/// 被控端：给手机端的双栏文件管理器提供“看电脑的文件夹、新建、重命名、删除”。
/// 收发文件本身走已有的 FileBegin/Chunk/End 传输。只在“允许被远程”打开时响应（由调用方检查）。
/// </summary>
public static class FileService
{
    /// <summary>列目录。path 为空 = 根：常用文件夹 + 所有磁盘</summary>
    public static FsListResultMsg List(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) return Root();
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) return new FsListResultMsg(path, null, Array.Empty<FsEntry>(), "文件夹不存在");
            var entries = new List<FsEntry>();
            foreach (var d in dir.EnumerateDirectories())
            {
                if (Hidden(d)) continue;
                entries.Add(new FsEntry(d.Name, true, 0, Ms(d.LastWriteTimeUtc)));
            }
            foreach (var f in dir.EnumerateFiles())
            {
                if (Hidden(f)) continue;
                entries.Add(new FsEntry(f.Name, false, f.Length, Ms(f.LastWriteTimeUtc)));
            }
            var ordered = entries.OrderByDescending(e => e.Dir).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
            // 磁盘根目录的上一级是“根列表”
            var parent = dir.Parent?.FullName ?? "";
            return new FsListResultMsg(dir.FullName, parent, ordered, null);
        }
        catch (UnauthorizedAccessException) { return new FsListResultMsg(path, Up(path), Array.Empty<FsEntry>(), "没有权限访问这个文件夹"); }
        catch (Exception e) { return new FsListResultMsg(path, Up(path), Array.Empty<FsEntry>(), e.Message); }
    }

    static FsListResultMsg Root()
    {
        var list = new List<FsEntry>();
        void Special(string name, Environment.SpecialFolder f)
        {
            var p = Environment.GetFolderPath(f);
            if (p != "" && Directory.Exists(p)) list.Add(new FsEntry(name, true, 0, 0, p));
        }
        Special("桌面", Environment.SpecialFolder.DesktopDirectory);
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) list.Add(new FsEntry("下载", true, 0, 0, downloads));
        Special("文档", Environment.SpecialFolder.MyDocuments);
        Special("图片", Environment.SpecialFolder.MyPictures);
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? (d.DriveType == DriveType.Removable ? "U 盘" : "本地磁盘") : d.VolumeLabel;
                list.Add(new FsEntry($"{label} ({d.Name.TrimEnd('\\')})", true, d.TotalSize - d.TotalFreeSpace, 0, d.RootDirectory.FullName));
            }
            catch { }
        }
        return new FsListResultMsg("", null, list.ToArray(), null);
    }

    public static FsOpResultMsg Op(FsOpMsg m)
    {
        try
        {
            switch (m.Op)
            {
                case "mkdir":
                {
                    var name = SafeName(m.NewName);
                    var p = Path.Combine(m.Path, name);
                    if (Directory.Exists(p) || File.Exists(p)) return new(false, "已存在同名文件或文件夹");
                    Directory.CreateDirectory(p);
                    return new(true, "已新建 " + name);
                }
                case "rename":
                {
                    var name = SafeName(m.NewName);
                    var dir = Path.GetDirectoryName(m.Path.TrimEnd('\\'))!;
                    var dst = Path.Combine(dir, name);
                    if (Directory.Exists(dst) || File.Exists(dst)) return new(false, "已存在同名文件或文件夹");
                    if (Directory.Exists(m.Path)) Directory.Move(m.Path, dst); else File.Move(m.Path, dst);
                    return new(true, "已重命名为 " + name);
                }
                case "delete":
                {
                    // 进回收站，误删可以找回
                    if (Directory.Exists(m.Path))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(m.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else if (File.Exists(m.Path))
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(m.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    else return new(false, "不存在");
                    return new(true, "已移到回收站");
                }
                default: return new(false, "不支持的操作");
            }
        }
        catch (Exception e) { return new(false, e.Message); }
    }

    static string SafeName(string? n)
    {
        n = (n ?? "").Trim();
        if (n == "" || n == "." || n == ".." || n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new ArgumentException("名称不合法");
        return n;
    }

    static bool Hidden(FileSystemInfo i) =>
        (i.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System) ||
        i.Name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || i.Name.EndsWith(".frpart", StringComparison.OrdinalIgnoreCase);

    static long Ms(DateTime utc) => new DateTimeOffset(utc).ToUnixTimeMilliseconds();

    static string? Up(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : Directory.GetParent(path)?.FullName ?? ""; } catch { return ""; }
    }
}
