using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using XinDong.Models;

namespace XinDong.Views;

/// <summary>首次使用薪资初始化：只问计薪方式和金额，不预填测试数据。</summary>
public partial class SalarySetupWindow : Window
{
    /// <summary>用户选择的计薪方式。</summary>
    public SalaryMode SelectedMode { get; private set; } = SalaryMode.Monthly;

    /// <summary>用户填写的金额：月薪元 / 月，或时薪元 / 小时。</summary>
    public decimal SelectedAmount { get; private set; }

    /// <summary>构造窗口。</summary>
    public SalarySetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => AmountBox.Focus();
    }

    /// <summary>拖动无边框窗口。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已提前松开
        }
    }

    /// <summary>切换月薪 / 时薪时更新输入提示。</summary>
    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AmountLabel is null) return;
        AmountLabel.Text = ModeCombo.SelectedIndex == 1
            ? "时薪金额（元 / 小时）"
            : "月薪金额（元 / 月）";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    /// <summary>校验并保存。</summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Visibility = Visibility.Collapsed;

        var text = AmountBox.Text.Trim();
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var amount) &&
            !decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out amount))
        {
            ShowError("请输入数字金额，例如 10000 或 50。");
            return;
        }

        if (amount <= 0m)
        {
            ShowError("金额必须大于 0。");
            return;
        }

        SelectedMode = ModeCombo.SelectedIndex == 1 ? SalaryMode.Hourly : SalaryMode.Monthly;
        SelectedAmount = amount;
        DialogResult = true;
    }

    /// <summary>稍后设置。</summary>
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    /// <summary>显示错误。</summary>
    private void ShowError(string message)
    {
        ErrorText.Text = "❌ " + message;
        ErrorText.Visibility = Visibility.Visible;
        AmountBox.Focus();
    }
}
