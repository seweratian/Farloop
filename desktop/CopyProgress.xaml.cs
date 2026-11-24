using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using FreeRemote.Core;

namespace FreeRemote;

/// <summary>
/// 拖拽复制时的浮动进度卡片（控制端）。贴在远程画面右下角；没有远程画面时在屏幕右下角。
/// 完成后停留几秒自动消失；鼠标可穿透，不影响远程操作。
/// </summary>
public partial class CopyProgress : Window
{
    static CopyProgress? _instance;
    readonly ObservableCollection<TransferVM> _items = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    Func<IntPtr>? _anchor;

    CopyProgress()
    {
        InitializeComponent();
        List.ItemsSource = _items;
        SourceInitialized += (_, _) => Overlay.MakeOverlay(new WindowInteropHelper(this).Handle, clickThrough: true);
        _timer.Tick += (_, _) => Place();
        SizeChanged += (_, _) => Place();
    }

    /// <summary>显示一个传输的进度。anchor 返回远程画面窗口（可为空）。</summary>
    public static void Track(TransferVM vm, Func<IntPtr>? anchor)
    {
        _instance ??= new CopyProgress();
        var w = _instance;
        if (anchor != null) w._anchor = anchor;
        if (w._items.Contains(vm)) return;
        w._items.Insert(0, vm);
        while (w._items.Count > 4) w._items.RemoveAt(w._items.Count - 1); // 最多同时显示 4 个
        vm.PropertyChanged += w.OnItemChanged;
        if (!w.IsVisible) { w.Show(); w._timer.Start(); }
        w.Place();
        if (vm.Finished) w.ScheduleRemove(vm); // 小文件可能在挂上卡片之前就传完了
    }

    // 注意：“完成”是在网络线程上触发的，计时器必须回到界面线程上创建，否则永远不会走
    void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TransferVM.Finished) || sender is not TransferVM vm || !vm.Finished) return;
        Dispatcher.BeginInvoke(() => ScheduleRemove(vm));
    }

    void ScheduleRemove(TransferVM vm)
    {
        vm.PropertyChanged -= OnItemChanged;
        // 完成的停 3 秒，失败的停 8 秒，再消失
        var t = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher) { Interval = TimeSpan.FromSeconds(vm.Failed ? 8 : 3) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            _items.Remove(vm);
            if (_items.Count == 0) { _timer.Stop(); Hide(); }
        };
        t.Start();
    }

    void Place()
    {
        if (!IsVisible || ActualWidth <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        double right, bottom;
        var win = _anchor?.Invoke() ?? IntPtr.Zero;
        if (win != IntPtr.Zero && !Win.IsIconic(win) && Overlay.BottomRightAnchor is { } a)
        {
            // 远程画面右下角，叠在信息卡片上方、对方任务栏之上（由 Overlay 算好）
            right = a.Right;
            bottom = a.Bottom;
        }
        else if (win != IntPtr.Zero && !Win.IsIconic(win) && Win.ClientRectOnScreen(win, out var x, out var y, out var cw, out var ch))
        {
            // 远程画面窗口客户区右下角（物理像素 → WPF 单位）
            right = (x + cw) / dpi.DpiScaleX - 8;
            bottom = (y + ch) / dpi.DpiScaleY - 8;
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            right = wa.Right - 8;
            bottom = wa.Bottom - 8;
        }
        Left = right - ActualWidth;
        Top = bottom - ActualHeight;
    }
}
