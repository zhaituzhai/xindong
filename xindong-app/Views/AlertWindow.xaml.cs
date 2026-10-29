using System.Windows;
using System.Windows.Input;

namespace XinDong.Views;

/// <summary>
/// 通用置顶弹窗：既用于「下班打卡提醒」这种单按钮通知，也用于「防抹零血亏拦截」这种双按钮强拦截。
/// 单按钮时点击返回 true；双按钮时主按钮返回 false（再苟一会儿），次按钮返回 true（含泪下班）。
/// </summary>
public partial class AlertWindow : Window
{
    private readonly bool _isSingleButton;

    /// <summary>构造弹窗。</summary>
    /// <param name="title">标题。</param>
    /// <param name="message">正文。</param>
    /// <param name="primaryText">主按钮文案。</param>
    /// <param name="secondaryText">次按钮文案；为空表示单按钮通知。</param>
    /// <param name="isDanger">是否使用红色危险主色。</param>
    public AlertWindow(string title, string message, string primaryText, string? secondaryText, bool isDanger)
    {
        InitializeComponent();

        TitleText.Text = title;
        MessageText.Text = message;
        PrimaryButton.Content = primaryText;

        _isSingleButton = string.IsNullOrWhiteSpace(secondaryText);

        if (_isSingleButton)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
            PrimaryButton.Style = (Style)FindResource(isDanger ? "DangerButton" : "PrimaryButton");
        }
        else
        {
            SecondaryButton.Content = secondaryText;
            PrimaryButton.Style = (Style)FindResource("GhostButton");
            SecondaryButton.Style = (Style)FindResource(isDanger ? "DangerButton" : "PrimaryButton");
        }
    }

    /// <summary>主按钮：单按钮直接确认；双按钮表示「再苟一会儿」。</summary>
    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        // 设置 DialogResult 本身就会关闭 ShowDialog 打开的窗口，不能再调用 Close()
        DialogResult = _isSingleButton;
    }

    /// <summary>次按钮：含泪下班。</summary>
    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    /// <summary>按住弹窗任意位置可拖动。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 忽略
        }
    }
}
