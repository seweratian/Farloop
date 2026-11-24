using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace FreeRemote;

/// <summary>
/// 切换画面参数时，把最后一帧盖在原位置，底下完成重连后再淡出 —— 看起来不会关窗重开。
/// </summary>
public sealed class FreezeFrame : Window
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);

    readonly Overlay.Snapshot _s;

    FreezeFrame(Overlay.Snapshot s, string message)
    {
        _s = s;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Background = Brushes.Black;
        Width = 100; Height = 100;

        var grid = new Grid();
        if (s.Image != null) grid.Children.Add(new Image { Source = s.Image, Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)) });

        var spinner = new TextBlock
        {
            Text = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        spinner.RenderTransform.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 10, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children =
                {
                    spinner,
                    new TextBlock { Text = message, Foreground = Brushes.White, FontSize = 13.5, Margin = new Thickness(10, 0, 0, 0),
                                    FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI") },
                },
            },
        };
        grid.Children.Add(pill);
        Content = grid;

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            Overlay.MakeOverlay(h, clickThrough: true);
            SetWindowPos(h, new IntPtr(-1), _s.X, _s.Y, _s.W, _s.H, 0x0010 | 0x0040); // 物理像素精确盖住
        };
    }

    public static FreezeFrame Show(Overlay.Snapshot s, string message)
    {
        var f = new FreezeFrame(s, message);
        f.Show();
        return f;
    }

    public async Task FadeOutAsync()
    {
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
        await Task.Delay(240);
        Close();
    }
}
