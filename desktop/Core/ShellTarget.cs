using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FreeRemote.Core;

/// <summary>
/// 屏幕上某一点“是哪个文件夹”：桌面 → 桌面文件夹；资源管理器窗口 → 它当前打开的文件夹；其它 → null。
/// 用于拖拽文件时决定落到哪里。Shell COM 需要 STA 线程（UI 线程）调用。
/// </summary>
public static class ShellTarget
{
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public enum Kind { None, Desktop, Explorer }

    public static (Kind Kind, string? Folder) At(int x, int y)
    {
        var h = WindowFromPoint(new POINT { X = x, Y = y });
        if (h == IntPtr.Zero) return (Kind.None, null);
        var root = GetAncestor(h, 2 /*GA_ROOT*/);
        var cls = ClassOf(root);
        if (cls is "Progman" or "WorkerW") return (Kind.Desktop, Desktop);
        if (cls == "CabinetWClass") return (Kind.Explorer, ExplorerFolder(root));
        return (Kind.None, null);
    }

    /// <summary>
    /// 用 Shell.Application 找到这个资源管理器窗口当前的文件夹。
    /// Windows 11 的多标签页共用一个窗口句柄，用窗口标题匹配当前标签页。
    /// </summary>
    static string? ExplorerFolder(IntPtr root)
    {
        try
        {
            var title = new StringBuilder(512);
            GetWindowText(root, title, title.Capacity);
            var t = title.ToString();
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
            string? first = null;
            foreach (dynamic w in shell.Windows())
            {
                try
                {
                    if (new IntPtr((long)w.HWND) != root) continue;
                    string path = w.Document.Folder.Self.Path;
                    if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue; // “此电脑”等虚拟位置
                    first ??= path;
                    string name = w.LocationName;
                    if (!string.IsNullOrEmpty(name) && t.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return path;
                }
                catch { }
            }
            return first;
        }
        catch (Exception e)
        {
            Log.Error("获取资源管理器文件夹失败", e);
            return null;
        }
    }
}
