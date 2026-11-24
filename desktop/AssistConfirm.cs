using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace FreeRemote;

/// <summary>
/// 远程协助：有人凭设备码和密码请求控制本机时，在屏幕中间弹出的确认框（置顶，40 秒不点算拒绝）。
/// 和复制进度卡同一套深色风格。
/// </summary>
public static class AssistConfirm
{
    public static bool Ask(string who, TimeSpan timeout)
    {
        bool result = false;
        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            Topmost = true,
            ShowInTaskbar = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Title = "Farloop 远程协助请求",
        };
        if (Application.Current.TryFindResource("UiFont") is FontFamily f) w.FontFamily = f;

        var title = new TextBlock { Text = "远程协助请求", Foreground = Brushes.White, FontSize = 18, FontWeight = FontWeights.SemiBold };
        var body = new TextBlock
        {
            Text = $"“{who}” 想远程协助你的电脑。\n允许后对方能看到并操作你的屏幕，也能和你互传剪贴板、文件；\n但不能打开终端、浏览你的文件或启动程序。",
            Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            FontSize = 13.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
            LineHeight = 21,
        };
        var countdown = new TextBlock { Foreground = new SolidColorBrush(Color.FromArgb(0x90, 0xFF, 0xFF, 0xFF)), FontSize = 12, Margin = new Thickness(0, 12, 0, 0) };

        Button Btn(string text, Color bg, bool value)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(22, 9, 22, 9),
                Margin = new Thickness(10, 0, 0, 0),
                Foreground = Brushes.White,
                Background = new SolidColorBrush(bg),
                BorderThickness = new Thickness(0),
                FontSize = 14,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            b.Click += (_, _) => { result = value; w.Close(); };
            return b;
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Btn("拒绝", Color.FromRgb(0x48, 0x48, 0x4A), false));
        buttons.Children.Add(Btn("允许", Color.FromRgb(0x0A, 0x84, 0xFF), true));

        var panel = new StackPanel { Width = 420 };
        panel.Children.Add(title);
        panel.Children.Add(body);
        panel.Children.Add(countdown);
        panel.Children.Add(buttons);
        w.Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24, 20, 24, 20),
            Margin = new Thickness(16),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Direction = 270, Opacity = 0.4 },
            Child = panel,
        };

        var end = DateTime.UtcNow + timeout;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        void Tick()
        {
            var left = (int)Math.Ceiling((end - DateTime.UtcNow).TotalSeconds);
            if (left <= 0) { result = false; w.Close(); return; }
            countdown.Text = $"{left} 秒后自动拒绝";
        }
        timer.Tick += (_, _) => Tick();
        Tick();
        timer.Start();
        w.Closed += (_, _) => timer.Stop();
        w.Loaded += (_, _) => { w.Activate(); System.Media.SystemSounds.Asterisk.Play(); };
        w.ShowDialog();
        return result;
    }
}
