using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FreeRemote.Core;

namespace FreeRemote;

public sealed class NotConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object v, Type t, object p, System.Globalization.CultureInfo c) => v is bool b ? !b : true;
    public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => v is bool b ? !b : false;
}

/// <summary>Windows 11 系统背景材质（DWM 实时模糊）</summary>
public static class WindowEffects
{
    public const int Mica = 2, Acrylic = 3;
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    /// <summary>去掉 WS_SYSMENU：系统不再画 最小化/最大化/关闭（贴边布局、拖动、双击最大化不受影响）</summary>
    public static void HideSystemCaptionButtons(Window w)
    {
        var h = new WindowInteropHelper(w).Handle;
        SetWindowLong(h, -16, GetWindowLong(h, -16) & ~0x00080000);
    }

    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 从 Windows 11 22H2（build 22621）开始支持</summary>
    public static bool Supported => Environment.OSVersion.Version.Build >= 22621;

    public static bool ApplyBackdrop(Window w, int type, bool dark = false)
    {
        if (!Supported) return false;
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            var src = HwndSource.FromHwnd(h);
            if (src?.CompositionTarget == null) return false;
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
            int d = dark ? 1 : 0;
            DwmSetWindowAttribute(h, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
            int round = 2;
            DwmSetWindowAttribute(h, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
            int t = type;
            return DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref t, sizeof(int)) == 0;
        }
        catch (Exception e)
        {
            Log.Error("设置窗口材质失败", e);
            return false;
        }
    }
}
