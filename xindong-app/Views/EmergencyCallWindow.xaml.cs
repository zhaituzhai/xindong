using System.Globalization;
using System.Windows;
using System.Windows.Input;
using XinDong.Models;

namespace XinDong.Views;

/// <summary>
/// 老板夺命 Call 报酬选择弹窗：
/// A 纯义务白嫖（默认）、B1 固定单次补贴、B2 按实际加班时薪实时计酬。
/// </summary>
public partial class EmergencyCallWindow : Window
{
    /// <summary>用户确认后的配置；取消时为 null。</summary>
    public EmergencyCallOptions? Result { get; private set; }

    /// <summary>构造弹窗。</summary>
    public EmergencyCallWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (UnpaidRadio.IsChecked == true) UnpaidRadio.Focus();
        };
    }

    /// <summary>拖动无边框弹窗。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已提前松开，忽略
            }
        }
    }

    /// <summary>确认开始，校验金额并返回配置。</summary>
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        if (UnpaidRadio.IsChecked == true)
        {
            Result = new EmergencyCallOptions { Mode = EmergencyCallCompensationMode.Unpaid };
            DialogResult = true;
            return;
        }

        if (FixedRadio.IsChecked == true)
        {
            if (!TryParsePositive(FixedSubsidyBox.Text, out var subsidy))
            {
                ShowError("固定补贴必须是大于 0 的数字，例如 100。");
                return;
            }

            Result = new EmergencyCallOptions
            {
                Mode = EmergencyCallCompensationMode.FixedSubsidy,
                FixedSubsidy = subsidy
            };
            DialogResult = true;
            return;
        }

        if (!TryParsePositive(HourlyRateBox.Text, out var hourlyRate))
        {
            ShowError("实际加班时薪必须是大于 0 的数字，例如 50。");
            return;
        }

        Result = new EmergencyCallOptions
        {
            Mode = EmergencyCallCompensationMode.HourlyRate,
            HourlyRate = hourlyRate
        };
        DialogResult = true;
    }

    /// <summary>取消。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    /// <summary>解析正数金额（兼容当前区域与 Invariant 小数点）。</summary>
    private static bool TryParsePositive(string? text, out decimal value)
    {
        return (decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out value) ||
                decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out value)) &&
               value > 0m;
    }

    /// <summary>显示校验错误。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = "❌ " + message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
