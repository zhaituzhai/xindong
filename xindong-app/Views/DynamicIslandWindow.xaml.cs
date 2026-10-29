using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using XinDong.Models;
using XinDong.ViewModels;

namespace XinDong.Views;

/// <summary>
/// macOS 灵动岛悬浮窗：
/// 外层窗口固定 520×44，内部胶囊在 64×20 与 520×44 之间缩放；
/// 胶囊外区域通过 WM_NCHITTEST 穿透，不会挡住下层窗口。
/// 左键拖动，中键老板键，🐟 / 🏖 提供摸鱼与休假快捷开关。
/// </summary>
public partial class DynamicIslandWindow : Window
{
    private const double ExpandedWidth = 520;
    private const double ExpandedHeight = 44;
    private const double CollapsedWidth = 64;
    private const double CollapsedHeight = 20;

    /// <summary>左右都至少留出这么多空间时，采用居中缩放。</summary>
    private const double CenterMargin = (ExpandedWidth - CollapsedWidth) / 2.0;

    /// <summary>停靠点上边距：避开最大化窗口右上角的最小化 / 关闭按钮区。</summary>
    private const double DockTopOffset = 40;

    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private enum IslandAnchor
    {
        Left,
        Center,
        Right
    }

    private readonly MainViewModel _vm;
    private readonly DispatcherTimer _noticeTimer;
    private readonly DispatcherTimer _collapseTimer;

    private DispatcherTimer? _animTimer;
    private HwndSource? _hwndSource;
    private Rect _workArea = SystemParameters.WorkArea;
    private IslandAnchor _anchor = IslandAnchor.Right;
    private double _anchorX;
    private double _anchorTop;
    private bool _expanded;
    private bool _dragging;
    private bool _noticeVisible;
    private bool _positioned;
    private bool _lastCamouflage;
    private bool _lastEmergency;

    /// <summary>构造灵动岛。</summary>
    public DynamicIslandWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _noticeTimer.Tick += (_, _) => HideNotice();

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            Collapse();
        };

        // 先把窗口摆到右上角，避免首帧出现在屏幕左上角
        var area = SystemParameters.WorkArea;
        _workArea = area;
        Left = Math.Max(area.Left, area.Right - ExpandedWidth);
        Top = area.Top + DockTopOffset;

        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _hwndSource = HwndSource.FromHwnd(handle);
            _hwndSource?.AddHook(WndProc);
        };

        _vm.PropertyChanged += Vm_PropertyChanged;
        _lastCamouflage = _vm.IsCamouflage;
        _lastEmergency = _vm.IsEmergencyCall;
        Loaded += (_, _) => PlaceTopRight();
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= Vm_PropertyChanged;
            _noticeTimer.Stop();
            _collapseTimer.Stop();
            _animTimer?.Stop();

            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }
        };
    }

    #region 位置、锚点与展开 / 收起

    /// <summary>首次停靠到主屏右上角；之后保留用户拖动后的位置与锚点。</summary>
    public void PlaceTopRight()
    {
        var area = SystemParameters.WorkArea;
        _workArea = area;

        if (!_positioned)
        {
            _anchor = IslandAnchor.Right;
            _anchorX = area.Right;
            _anchorTop = area.Top + DockTopOffset;
            _positioned = true;
        }
        else
        {
            _anchorX = Math.Clamp(_anchorX, area.Left, area.Right);
            _anchorTop = Math.Clamp(_anchorTop, area.Top,
                Math.Max(area.Top, area.Bottom - ExpandedHeight));
        }

        ApplyAnchor();
        PositionWindow();

        if (_expanded)
        {
            AnimatePill(ExpandedWidth, ExpandedHeight, 22);
        }
        else
        {
            SnapCollapsed();
        }
    }

    /// <summary>显示后先展开 2.6 秒，再自动缩回，避免"收进灵动岛后找不到"。</summary>
    public void Pulse()
    {
        if (!_positioned) PlaceTopRight();

        Expand();
        ScheduleCollapse(2600);
    }

    /// <summary>展开成完整胶囊。</summary>
    public void Expand()
    {
        if (!_positioned) PlaceTopRight();

        _collapseTimer.Stop();

        if (_expanded && _animTimer is null &&
            Math.Abs(IslandBorder.Width - ExpandedWidth) < 0.5 &&
            Math.Abs(IslandBorder.Height - ExpandedHeight) < 0.5)
        {
            return;
        }

        _expanded = true;
        FullLayer.IsHitTestVisible = true;

        AnimatePill(ExpandedWidth, ExpandedHeight, 22);
        Fade(FullLayer, 1);
        Fade(MiniLayer, 0);
    }

    /// <summary>收起成贴边小胶囊。鼠标仍悬停、拖动中、提醒中都不收。</summary>
    private void Collapse()
    {
        // 夺命 Call 警报期间保持展开，避免红色警报被自动收起
        if (_vm.IsEmergencyCall)
        {
            ScheduleCollapse(1500);
            return;
        }

        // 用 Win32 光标位置兜底：动画时 WPF 可能补发 MouseLeave
        if (IsCursorInsidePill())
        {
            ScheduleCollapse(500);
            return;
        }

        if (!_expanded || _noticeVisible || _dragging)
        {
            return;
        }

        _expanded = false;
        FullLayer.IsHitTestVisible = false;

        AnimatePill(CollapsedWidth, CollapsedHeight, 11);
        Fade(FullLayer, 0);
        Fade(MiniLayer, 1);
    }

    /// <summary>立即收起（无动画，用于启动与重新停靠）。</summary>
    private void SnapCollapsed()
    {
        _animTimer?.Stop();
        _animTimer = null;
        _expanded = false;

        IslandBorder.Width = CollapsedWidth;
        IslandBorder.Height = CollapsedHeight;
        ApplyChrome(11);
        FullLayer.IsHitTestVisible = false;

        FullLayer.BeginAnimation(OpacityProperty, null);
        MiniLayer.BeginAnimation(OpacityProperty, null);
        FullLayer.Opacity = 0;
        MiniLayer.Opacity = 1;
    }

    /// <summary>根据锚点决定胶囊在固定窗口中的水平对齐方式。</summary>
    private void ApplyAnchor()
    {
        var alignment = _anchor switch
        {
            IslandAnchor.Left => HorizontalAlignment.Left,
            IslandAnchor.Center => HorizontalAlignment.Center,
            _ => HorizontalAlignment.Right
        };

        IslandBorder.HorizontalAlignment = alignment;
        FullLayer.HorizontalAlignment = alignment;
        NoticeLayer.HorizontalAlignment = alignment;
    }

    /// <summary>把固定 520×44 的透明窗口摆到锚点位置。</summary>
    private void PositionWindow()
    {
        if (!_positioned) return;

        var area = _workArea;
        var left = _anchor switch
        {
            IslandAnchor.Right => _anchorX - ExpandedWidth,
            IslandAnchor.Left => _anchorX,
            _ => _anchorX - ExpandedWidth / 2.0
        };

        left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - ExpandedWidth));
        var top = Math.Clamp(_anchorTop, area.Top,
            Math.Max(area.Top, area.Bottom - ExpandedHeight));

        Width = ExpandedWidth;
        Height = ExpandedHeight;
        Left = left;
        Top = top;
    }

    /// <summary>拖动结束后，以展开胶囊的边界重新判定左 / 中 / 右锚点。</summary>
    private void UpdateAnchorAfterDrag()
    {
        var rect = GetPillScreenRect();
        var dpi = VisualTreeHelper.GetDpi(this);
        var left = rect.Left / dpi.DpiScaleX;
        var right = rect.Right / dpi.DpiScaleX;
        var top = rect.Top / dpi.DpiScaleY;

        var leftMargin = left - _workArea.Left;
        var rightMargin = _workArea.Right - right;

        _anchor = ChooseAnchor(leftMargin, rightMargin);
        _anchorX = _anchor switch
        {
            IslandAnchor.Right => Math.Min(_workArea.Right, right),
            IslandAnchor.Left => Math.Max(_workArea.Left, left),
            _ => (left + right) / 2.0
        };
        _anchorTop = Math.Clamp(top, _workArea.Top,
            Math.Max(_workArea.Top, _workArea.Bottom - ExpandedHeight));

        ApplyAnchor();
        PositionWindow();
    }

    /// <summary>左右都够就居中；否则靠哪边就近哪边。</summary>
    private static IslandAnchor ChooseAnchor(double leftMargin, double rightMargin)
    {
        if (leftMargin >= CenterMargin && rightMargin >= CenterMargin)
        {
            return IslandAnchor.Center;
        }

        if (rightMargin < CenterMargin && leftMargin >= CenterMargin)
        {
            return IslandAnchor.Right;
        }

        if (leftMargin < CenterMargin && rightMargin >= CenterMargin)
        {
            return IslandAnchor.Left;
        }

        return leftMargin >= rightMargin ? IslandAnchor.Left : IslandAnchor.Right;
    }

    /// <summary>
    /// 220ms EaseOutCubic 逐帧补间：只改内部胶囊，不动窗口尺寸；
    /// 文字层固定 520×44，不会在缩放过程中重排闪烁。
    /// </summary>
    private void AnimatePill(double targetWidth, double targetHeight, double targetRadius)
    {
        if (!_positioned) return;

        _animTimer?.Stop();

        var startWidth = IslandBorder.Width;
        var startHeight = IslandBorder.Height;
        if (double.IsNaN(startWidth) || startWidth <= 0) startWidth = CollapsedWidth;
        if (double.IsNaN(startHeight) || startHeight <= 0) startHeight = CollapsedHeight;

        var startRadius = IslandBorder.CornerRadius.TopLeft;
        if (startRadius <= 0) startRadius = 11;

        var startedAt = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(15)
        };

        _animTimer = timer;
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp(
                (DateTime.UtcNow - startedAt).TotalMilliseconds / 220.0, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);

            IslandBorder.Width = startWidth + (targetWidth - startWidth) * eased;
            IslandBorder.Height = startHeight + (targetHeight - startHeight) * eased;
            ApplyChrome(startRadius + (targetRadius - startRadius) * eased);

            if (progress < 1) return;

            timer.Stop();
            if (ReferenceEquals(_animTimer, timer))
            {
                _animTimer = null;
            }

            IslandBorder.Width = targetWidth;
            IslandBorder.Height = targetHeight;
            ApplyChrome(targetRadius);
        };

        timer.Start();
    }

    /// <summary>圆角随胶囊尺寸同步变化。</summary>
    private void ApplyChrome(double radius)
    {
        var corner = new CornerRadius(Math.Clamp(radius, 10, 22));

        IslandBorder.CornerRadius = corner;
        OvertimeGlow.CornerRadius = corner;
        FlashBorder.CornerRadius = corner;
        NoticeLayer.CornerRadius = corner;
    }

    /// <summary>淡入 / 淡出某层内容。</summary>
    private static void Fade(UIElement element, double target)
    {
        var animation = new DoubleAnimation(target, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        element.BeginAnimation(OpacityProperty, animation);
    }

    /// <summary>延时收起，给鼠标在胶囊内部移动留缓冲。</summary>
    private void ScheduleCollapse(int delayMs)
    {
        _collapseTimer.Stop();
        _collapseTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(200, delayMs));
        _collapseTimer.Start();
    }

    /// <summary>当前胶囊（不含透明窗口区域）的屏幕矩形，单位是物理像素。</summary>
    private Rect GetPillScreenRect()
    {
        var width = IslandBorder.ActualWidth > 0.5
            ? IslandBorder.ActualWidth
            : IslandBorder.Width;
        var height = IslandBorder.ActualHeight > 0.5
            ? IslandBorder.ActualHeight
            : IslandBorder.Height;

        if (double.IsNaN(width) || width <= 0) width = CollapsedWidth;
        if (double.IsNaN(height) || height <= 0) height = CollapsedHeight;

        var dpi = VisualTreeHelper.GetDpi(this);
        var position = IslandBorder.PointToScreen(new Point(0, 0));
        return new Rect(
            position.X,
            position.Y,
            width * dpi.DpiScaleX,
            height * dpi.DpiScaleY);
    }

    /// <summary>用 Win32 光标位置判断是否仍在胶囊内。</summary>
    private bool IsCursorInsidePill()
    {
        if (!_positioned) return false;
        if (!GetCursorPos(out var point)) return false;
        return IsPointInsidePill(point.X, point.Y);
    }

    /// <summary>判断屏幕物理坐标是否落在胶囊内。</summary>
    private bool IsPointInsidePill(int x, int y)
    {
        var rect = GetPillScreenRect();
        return x >= rect.Left && x < rect.Right &&
               y >= rect.Top && y < rect.Bottom;
    }

    /// <summary>胶囊外的透明区域返回 HTTRANSPARENT，让鼠标穿透到下层窗口。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest && _positioned)
        {
            var value = lParam.ToInt64();
            var x = unchecked((short)(value & 0xFFFF));
            var y = unchecked((short)((value >> 16) & 0xFFFF));

            if (!IsPointInsidePill(x, y))
            {
                return new IntPtr(HtTransparent);
            }
        }

        return IntPtr.Zero;
    }

    #endregion

    #region 提醒

    /// <summary>显示一条临时提醒：自动展开、闪烁三下，8 秒后收回。伪装态直接忽略，避免露馅。</summary>
    public void ShowNotice(string text)
    {
        if (_vm.IsCamouflage) return;

        NoticeText.Text = text;
        NoticeLayer.Visibility = Visibility.Visible;
        _noticeVisible = true;

        Expand();

        _noticeTimer.Stop();
        _noticeTimer.Start();

        var isWarning = text.Contains("⚠️", StringComparison.Ordinal);
        FlashBorder.BorderBrush = isWarning
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A))
            : new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));

        var flash = new DoubleAnimation(0.0, 0.85, TimeSpan.FromMilliseconds(240))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3)
        };
        FlashBorder.BeginAnimation(OpacityProperty, flash);
    }

    /// <summary>收起提醒层，并安排一次自动收回。</summary>
    private void HideNotice()
    {
        _noticeTimer.Stop();
        _noticeVisible = false;
        NoticeLayer.Visibility = Visibility.Collapsed;
        ScheduleCollapse(900);
    }

    #endregion

    #region 交互

    /// <summary>鼠标移入：立即展开。</summary>
    private void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        Expand();
    }

    /// <summary>鼠标移出：0.8 秒后收起；伪装态同样允许缩回。</summary>
    private void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_vm.IsEmergencyCall) return;

        if (!_noticeVisible && !_dragging)
        {
            ScheduleCollapse(800);
        }
    }

    /// <summary>展开态点击摸鱼按钮：在隧道阶段直接处理，避免模板按钮偶发收不到鼠标。</summary>
    private void Island_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_expanded) return;
        if (!IsClickInsideFish(e)) return;

        ToggleFish();
        e.Handled = true;
    }

    /// <summary>左键拖动；中键触发老板键；点在按钮上不拖动窗口。</summary>
    private void Island_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle)
        {
            _vm.ToggleBossKey();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        // 点在快捷按钮上时不要拖动窗口
        if (e.OriginalSource is DependencyObject source && FindAncestor<Button>(source) is not null)
        {
            return;
        }

        // 收起态点一下先展开，再允许拖动
        if (!_expanded)
        {
            Expand();
            return;
        }

        _dragging = true;
        _collapseTimer.Stop();
        _animTimer?.Stop();

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已提前松开，忽略
        }
        finally
        {
            _dragging = false;
        }

        UpdateAnchorAfterDrag();
        ScheduleCollapse(1100);
    }

    /// <summary>🐟 快捷摸鱼开关。</summary>
    private void Fish_Click(object sender, RoutedEventArgs e)
    {
        ToggleFish();
    }

    /// <summary>切换摸鱼状态。</summary>
    private void ToggleFish()
    {
        if (_vm.CanStopSlack)
        {
            _vm.StopSlacking();
        }
        else if (_vm.CanStartSlack)
        {
            _vm.StartSlacking(SlackingType.Phone);
        }
    }

    /// <summary>鼠标是否落在当前可见的摸鱼按钮上。</summary>
    private bool IsClickInsideFish(MouseButtonEventArgs e)
    {
        return IsInsideButton(NormalFishButton, e) || IsInsideButton(CamoFishButton, e);
    }

    /// <summary>判断鼠标坐标是否在按钮矩形内。</summary>
    private static bool IsInsideButton(Button button, MouseButtonEventArgs e)
    {
        if (!button.IsVisible || button.ActualWidth <= 0 || button.ActualHeight <= 0) return false;

        var point = e.GetPosition(button);
        return point.X >= 0 && point.X <= button.ActualWidth &&
               point.Y >= 0 && point.Y <= button.ActualHeight;
    }

    /// <summary>🏖 快捷休假开关。</summary>
    private void Beach_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleTodayOff();
    }

    /// <summary>↩️ 手滑了 / 继续搬砖。</summary>
    private void UndoOffDuty_Click(object sender, RoutedEventArgs e)
    {
        _vm.UndoOffDuty();
    }

    /// <summary>🚨 老板夺命 Call。</summary>
    private void TriggerEmergencyCall_Click(object sender, RoutedEventArgs e)
    {
        _vm.RequestEmergencyCall();
    }

    /// <summary>🛑 终于放我走了。</summary>
    private void EndEmergencyCall_Click(object sender, RoutedEventArgs e)
    {
        _vm.EndEmergencyCall();
    }

    /// <summary>伪装模式切换时先展开 2.6 秒展示新模式，随后允许自动缩回。</summary>
    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsEmergencyCall))
        {
            if (_vm.IsEmergencyCall == _lastEmergency) return;
            _lastEmergency = _vm.IsEmergencyCall;

            _collapseTimer.Stop();
            if (_vm.IsEmergencyCall)
            {
                _noticeTimer.Stop();
                _noticeVisible = false;
                NoticeLayer.Visibility = Visibility.Collapsed;
                Expand();
            }
            else
            {
                ScheduleCollapse(1200);
            }

            return;
        }

        if (!string.IsNullOrEmpty(e.PropertyName) &&
            e.PropertyName != nameof(MainViewModel.IsCamouflage))
        {
            return;
        }

        // IsCamouflage 会被每秒的属性广播带上，只有真正变化时才处理
        if (_vm.IsCamouflage == _lastCamouflage)
        {
            return;
        }

        _lastCamouflage = _vm.IsCamouflage;

        if (_vm.IsCamouflage)
        {
            // 进入伪装时清掉可能正在展示的中文提醒，避免盖在 PLC / Excel 文案上
            _noticeTimer.Stop();
            _noticeVisible = false;
            NoticeLayer.Visibility = Visibility.Collapsed;
            FlashBorder.BeginAnimation(OpacityProperty, null);
            FlashBorder.Opacity = 0;

            Expand();
            ScheduleCollapse(2600);
        }
        else
        {
            ScheduleCollapse(600);
        }
    }

    /// <summary>向上查找指定类型的可视父级。</summary>
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

    #region Win32 光标检测

    [StructLayout(LayoutKind.Sequential)]
    private struct Point32
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point32 point);

    #endregion

    #endregion
}
