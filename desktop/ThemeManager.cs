using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 外观：跟随系统 / 浅色 / 深色。
/// 主题里的颜色都是共享的画刷（StaticResource 引用同一个实例），切换时直接改这些画刷的颜色，界面立即变化、不用重启：
///   接近白色的背景 → 深灰（保留原来的透明度），黑色系的文字 / 描边 → 白色（保留透明度），强调色等彩色不变。
/// </summary>
public static class ThemeManager
{
    public const int System_ = 0, Light = 1, Dark = 2;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;


    public static bool SystemIsDark()
    {
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1);
            return v is int i && i == 0;
        }
        catch { return false; }
    }

    public static void Apply(int mode)
    {
        bool dark = mode == Dark || (mode == System_ && SystemIsDark());
        IsDark = dark;
        Core.DeviceVM.DarkCards = dark;
        Walk(Application.Current.Resources, dark);
        Changed?.Invoke();
    }

    // 每个画刷资源第一次见到时的原始实例（浅色）；深色时换成改过色的新实例，界面用 DynamicResource 引用，立即生效
    static readonly Dictionary<(ResourceDictionary, object), object> LightOriginals = new();

    static void Walk(ResourceDictionary d, bool dark)
    {
        foreach (var key in new List<object>(System.Linq.Enumerable.Cast<object>(d.Keys)))
        {
            object? v;
            try { v = d[key]; } catch { continue; }
            if (v is not Brush) continue;
            if (!LightOriginals.TryGetValue((d, key), out var orig)) LightOriginals[(d, key)] = orig = v;
            if (!dark) { if (!ReferenceEquals(v, orig)) d[key] = orig; continue; }
            switch (orig)
            {
                case SolidColorBrush sb:
                {
                    var n = new SolidColorBrush(Map(sb.Color, key as string)) { Opacity = sb.Opacity };
                    n.Freeze();
                    d[key] = n;
                    break;
                }
                case GradientBrush gb:
                {
                    var n = gb.CloneCurrentValue();
                    foreach (var st in n.GradientStops) st.Color = Map(st.Color, key as string);
                    n.Freeze();
                    d[key] = n;
                    break;
                }
            }
        }
        foreach (var m in d.MergedDictionaries) Walk(m, dark);
    }

    static Color Map(Color c, string? key)
    {
        byte min = Math.Min(c.R, Math.Min(c.G, c.B)), max = Math.Max(c.R, Math.Max(c.G, c.B));
        if (min >= 0xF0)
        {
            // 不透明的整页底色更深，半透明的卡片 / 玻璃层略亮一点，层次和浅色模式一致
            byte g = c.A == 0xFF && key == "Bg" ? (byte)0x1C : (byte)0x2C;
            return Color.FromArgb(c.A, g, g, (byte)(g + 3));
        }
        if (max <= 0x10) return Color.FromArgb(c.A, 0xFF, 0xFF, 0xFF);
        return c;
    }
}
