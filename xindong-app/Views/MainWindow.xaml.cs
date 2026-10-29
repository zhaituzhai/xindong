using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using XinDong.Models;
using XinDong.Services;
using XinDong.ViewModels;

namespace XinDong.Views;

/// <summary>
/// macOS Sonoma 风格主控制台：
/// 无边框圆角 + 弥散阴影 + 红黄绿交通灯 + 左侧磨砂导航 + 右侧白色卡片区。
/// 所有业务状态都在 <see cref="MainViewModel"/> 中，本窗口只负责交互与弹窗。
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    /// <summary>构造主窗口。</summary>
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;
    }

    /// <summary>标题栏拖拽移动窗口。</summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 极少数情况下鼠标已松开，DragMove 会抛异常，忽略即可
        }
    }

    /// <summary>红灯：老板键隐身 / 伪装。</summary>
    private void BossLight_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.ToggleBossKey();
    }

    /// <summary>黄灯：主窗口收进灵动岛。</summary>
    private void MinimizeLight_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.MinimizeToIsland();
    }

    /// <summary>绿灯：开始 / 结束带薪摸鱼。</summary>
    private void SlackLight_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CanStopSlack)
        {
            _vm.StopSlacking();
        }
        else
        {
            _vm.StartSlacking(SlackingType.Phone);
        }
    }

    /// <summary>
    /// 时间 / 薪资输入框失焦后，先把绑定写回 Config，再让 VM 重算 H 与日/时/分/秒薪。
    /// </summary>
    private void TimeField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        // 让 WPF 先完成 LostFocus 绑定提交，再刷新派生数字
        Dispatcher.BeginInvoke(new Action(_vm.RefreshDerivedSettings), DispatcherPriority.Background);
    }

    /// <summary>薪资输入失焦时先提交绑定，再自动保存薪资，避免改完金额忘记点保存。</summary>
    private void SalaryField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _vm.RefreshDerivedSettings();
            _vm.SaveSalaryOnly();
        }), DispatcherPriority.Background);
    }

    /// <summary>导出当前看板周期的 CSV。</summary>
    private void ExportRangeCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出当前周期明细",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"薪动明细_{_vm.BoardDate:yyyyMMdd}.csv",
            InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            _vm.ExportCurrentRangeCsv(dialog.FileName);
        }
    }

    /// <summary>导出 CSV 报表。</summary>
    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出薪动账本",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"薪动报表_{DateTime.Now:yyyyMMdd_HHmm}.csv",
            InitialDirectory = AppDomain.CurrentDomain.BaseDirectory
        };

        if (dialog.ShowDialog(this) == true)
        {
            _vm.ExportCsv(dialog.FileName);
        }
    }

    /// <summary>打开 exe 同级数据目录。</summary>
    private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true
            });
        }
        catch
        {
            // 资源管理器打不开不影响程序
        }
    }

    /// <summary>生成手动启用的 .reg 文件，用于系统禁止程序写启动项的环境。</summary>
    private void GenerateStartupScripts_Click(object sender, RoutedEventArgs e)
    {
        if (!StartupService.CreateManualRegistrationScripts(
                out var enablePath, out var disablePath, out var error))
        {
            MessageBox.Show("生成启动项文件失败：\n" + error,
                "薪动 · 开机自启动", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        MessageBox.Show(
            "已生成两个注册表文件（就在 exe 同级目录）：\n\n" +
            enablePath + "\n" +
            disablePath + "\n\n" +
            "启用：直接打开这个文件夹，双击「启用开机自启动.reg」，确认导入。\n" +
            "关闭：双击「关闭开机自启动.reg」。",
            "薪动 · 开机自启动",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    /// <summary>退出程序。</summary>
    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        (Application.Current as App)?.ExitApplication();
    }

    /// <summary>关闭按钮：默认收进托盘 / 灵动岛，而不是直接杀进程。</summary>
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (Application.Current is not App app || app.IsExiting)
        {
            return;
        }

        e.Cancel = true;

        if (_vm.Config.CloseToTray)
        {
            Hide();
            app.ShowIsland();
        }
        else
        {
            // 正在 Closing 事件里再次 Close 会重入，推迟到消息循环下一拍
            Dispatcher.BeginInvoke(new Action(app.ExitApplication));
        }
    }
}
