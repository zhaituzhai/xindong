using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using XinDong.Models;

namespace XinDong.Views;

/// <summary>
/// 确认下班时间弹窗：只改时间，不改业务日期；默认当前时间。
/// 时间使用 iOS 风格的双列滚轮选择，支持鼠标滚轮、拖动和点击。
/// </summary>
public partial class ClockOutTimeWindow : Window
{
    private const double ItemHeight = 32;
    private const int SpacerCount = 2;

    private readonly ClockOutTimeContext _context;
    private readonly List<WheelItem> _hours = new();
    private readonly List<WheelItem> _minutes = new();
    private bool _syncingSelection;

    private ListBox? _dragList;
    private double _dragStartY;
    private int _dragStartIndex;
    private bool _draggingWheel;

    /// <summary>用户确认后的下班时间；取消时为 null。</summary>
    public DateTime? SelectedTime { get; private set; }

    /// <summary>构造弹窗。</summary>
    public ClockOutTimeWindow(ClockOutTimeContext context)
    {
        InitializeComponent();
        _context = context;

        DateText.Text = $"业务日：{context.BusinessDate:yyyy-MM-dd}　当前：{context.Now:HH:mm:ss}";
        RangeText.Text = context.HolidayOvertime
            ? $"节假日加班开始：{context.Earliest:HH:mm}　下班时间：{context.WorkEnd:HH:mm}"
            : $"上班：{context.WorkStart:HH:mm}　下班：{context.WorkEnd:HH:mm}";

        BuildWheel(HourList, _hours, 24);
        BuildWheel(MinuteList, _minutes, 60);

        // 还没到正常下班时间时，不允许一键选未来时间
        NormalEndButton.IsEnabled = context.WorkEnd <= context.Now;

        Loaded += (_, _) =>
        {
            SelectTime(context.Now.Hour, context.Now.Minute, center: true);
            HourList.Focus();
        };
    }

    /// <summary>构建带首尾空位的滚轮数据。</summary>
    private static void BuildWheel(ListBox list, List<WheelItem> items, int count)
    {
        for (var i = 0; i < SpacerCount; i++)
        {
            items.Add(new WheelItem(-1, string.Empty));
        }

        for (var i = 0; i < count; i++)
        {
            items.Add(new WheelItem(i, i.ToString("00", CultureInfo.InvariantCulture)));
        }

        for (var i = 0; i < SpacerCount; i++)
        {
            items.Add(new WheelItem(-1, string.Empty));
        }

        list.ItemsSource = items;
    }

    /// <summary>把两个滚轮同时定位到指定时分，并限制不晚于当前时间。</summary>
    private void SelectTime(int hour, int minute, bool center)
    {
        var previousBusinessDay = _context.Now.Date > _context.BusinessDate.Date;
        var maxHour = previousBusinessDay ? 23 : Math.Clamp(_context.Now.Hour, 0, 23);
        hour = Math.Clamp(hour, 0, maxHour);

        var maxMinute = previousBusinessDay || hour < maxHour ? 59 : _context.Now.Minute;
        minute = Math.Clamp(minute, 0, maxMinute);

        _syncingSelection = true;
        HourList.SelectedIndex = hour + SpacerCount;
        MinuteList.SelectedIndex = minute + SpacerCount;
        _syncingSelection = false;

        if (center)
        {
            CenterList(HourList, HourList.SelectedIndex);
            CenterList(MinuteList, MinuteList.SelectedIndex);
        }
    }

    /// <summary>当前选中的小时。</summary>
    private int SelectedHour =>
        (HourList.SelectedItem as WheelItem)?.Value is int value && value >= 0 ? value : 0;

    /// <summary>当前选中的分钟。</summary>
    private int SelectedMinute =>
        (MinuteList.SelectedItem as WheelItem)?.Value is int value && value >= 0 ? value : 0;

    /// <summary>列表选择变化时，保证选中项始终是有效数字并居中。</summary>
    private void Wheel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection || sender is not ListBox list) return;

        var item = list.SelectedItem as WheelItem;
        if (item is null || item.Value < 0)
        {
            // 滑动到首尾空位时，回弹到最近的有效值
            _syncingSelection = true;
            var fallback = item is null
                ? SpacerCount
                : Math.Clamp(list.SelectedIndex, SpacerCount, list.Items.Count - SpacerCount - 1);
            list.SelectedIndex = fallback;
            _syncingSelection = false;
            item = list.SelectedItem as WheelItem;
        }

        if (item is null || item.Value < 0) return;
        ClampToNow();
        CenterList(HourList, HourList.SelectedIndex);
        CenterList(MinuteList, MinuteList.SelectedIndex);
    }

    /// <summary>超过当前时间时，自动回弹到当前时间。</summary>
    private void ClampToNow()
    {
        var previousBusinessDay = _context.Now.Date > _context.BusinessDate.Date;
        var maxHour = previousBusinessDay ? 23 : Math.Clamp(_context.Now.Hour, 0, 23);

        if (SelectedHour > maxHour)
        {
            SetIndexSilent(HourList, maxHour + SpacerCount);
        }

        var maxMinute = previousBusinessDay || SelectedHour < maxHour
            ? 59
            : _context.Now.Minute;
        if (SelectedHour == maxHour && SelectedMinute > maxMinute)
        {
            SetIndexSilent(MinuteList, maxMinute + SpacerCount);
        }
    }

    /// <summary>静默设置列表索引，不触发选择回调。</summary>
    private void SetIndexSilent(ListBox list, int index)
    {
        _syncingSelection = true;
        list.SelectedIndex = Math.Clamp(index, SpacerCount,
            list.Items.Count - SpacerCount - 1);
        _syncingSelection = false;
    }

    /// <summary>鼠标滚轮滚动一格。</summary>
    private void Wheel_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ListBox list) return;

        var direction = e.Delta > 0 ? -1 : 1;
        SetListIndex(list, list.SelectedIndex + direction);
        e.Handled = true;
    }

    /// <summary>按下开始拖动滚轮。</summary>
    private void Wheel_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;

        _dragList = list;
        _dragStartY = e.GetPosition(list).Y;
        _dragStartIndex = list.SelectedIndex;
        _draggingWheel = false;
    }

    /// <summary>上下拖动滚轮。</summary>
    private void Wheel_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox list || _dragList != list ||
            e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var delta = e.GetPosition(list).Y - _dragStartY;
        if (!_draggingWheel && Math.Abs(delta) < 6) return;

        if (!_draggingWheel)
        {
            _draggingWheel = true;
            list.CaptureMouse();
        }

        var steps = (int)(delta / ItemHeight);
        if (steps == 0) return;

        SetListIndex(list, _dragStartIndex - steps);
        _dragStartY += steps * ItemHeight;
    }

    /// <summary>结束拖动。</summary>
    private void Wheel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;

        if (_draggingWheel)
        {
            list.ReleaseMouseCapture();
        }

        _dragList = null;
        _draggingWheel = false;
    }

    /// <summary>设置有效索引并居中。</summary>
    private void SetListIndex(ListBox list, int index)
    {
        var min = SpacerCount;
        var max = list.Items.Count - SpacerCount - 1;
        index = Math.Clamp(index, min, max);

        _syncingSelection = true;
        list.SelectedIndex = index;
        _syncingSelection = false;

        ClampToNow();
        CenterList(HourList, HourList.SelectedIndex);
        CenterList(MinuteList, MinuteList.SelectedIndex);
    }

    /// <summary>把选中项滚动到滚轮正中间。</summary>
    private static void CenterList(ListBox list, int index)
    {
        if (index < 0) return;

        list.Dispatcher.BeginInvoke(new Action(() =>
        {
            list.UpdateLayout();
            var viewer = FindVisualChild<ScrollViewer>(list);
            var item = list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
            if (viewer is null || item is null || viewer.ViewportHeight <= 0) return;

            var itemTop = item.TransformToAncestor(viewer)
                .Transform(new Point(0, 0)).Y;
            var target = viewer.VerticalOffset + itemTop -
                         (viewer.ViewportHeight - item.ActualHeight) / 2.0;
            viewer.ScrollToVerticalOffset(Math.Max(0, target));
        }), DispatcherPriority.Loaded);
    }

    /// <summary>找可视化子元素。</summary>
    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;

            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }

    /// <summary>拖动无边框窗口；点在滚轮 / 按钮上时不拖动。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject source &&
            (FindAncestor<ListBox>(source) is not null || FindAncestor<Button>(source) is not null))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已提前松开
        }
    }

    /// <summary>向上查找可视父级。</summary>
    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>使用当前时间。</summary>
    private void Now_Click(object sender, RoutedEventArgs e)
    {
        SelectTime(_context.Now.Hour, _context.Now.Minute, center: true);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>使用正常下班时间。</summary>
    private void NormalEnd_Click(object sender, RoutedEventArgs e)
    {
        SelectTime(_context.WorkEnd.Hour, _context.WorkEnd.Minute, center: true);
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>确认选中的下班时间。</summary>
    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var time = new TimeSpan(SelectedHour, SelectedMinute, 0);
        var candidate = _context.BusinessDate.Date + time;

        // 业务日已过午夜时，凌晨时间自动归到次日；傍晚时间仍归业务日
        if (candidate < _context.Earliest && _context.Now.Date > _context.BusinessDate.Date)
        {
            candidate = _context.Now.Date + time;
        }

        if (candidate > _context.Now.AddSeconds(1))
        {
            ShowError("下班时间不能晚于现在。");
            return;
        }

        if (candidate < _context.Earliest)
        {
            ShowError(_context.HolidayOvertime
                ? "下班时间不能早于节假日加班开始时间。"
                : "下班时间不能早于上班时间。");
            return;
        }

        SelectedTime = candidate;
        DialogResult = true;
    }

    /// <summary>取消。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        SelectedTime = null;
        DialogResult = false;
    }

    /// <summary>显示错误。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = "❌ " + message;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>滚轮条目。</summary>
    private sealed class WheelItem
    {
        public WheelItem(int value, string text)
        {
            Value = value;
            Text = text;
        }

        public int Value { get; }
        public string Text { get; }
    }
}
