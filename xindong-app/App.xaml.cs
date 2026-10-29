using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using XinDong.Models;
using XinDong.Services;
using XinDong.ViewModels;
using XinDong.Views;

namespace XinDong;

/// <summary>
/// 应用入口：负责单实例、窗口编排、托盘、全局老板键，以及老板键的「隐身 / 伪装」两种模式切换。
/// </summary>
public partial class App : Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _showMainEvent;
    private Thread? _showMainThread;
    private StorageService _storage = null!;
    private MainViewModel _vm = null!;
    private MainWindow _main = null!;
    private DynamicIslandWindow _island = null!;
    private TrayIconService _tray = null!;
    private HotKeyService _hotkey = null!;

    private DeviceIdService? _deviceSvc;
    private ApiService? _api;
    private ReportScheduler? _reportScheduler;

    private bool _bossActive;
    private bool _wasMainVisible;
    private bool _wasIslandVisible;

    /// <summary>是否正在退出（用于放行主窗口的 Closing 拦截）。</summary>
    public bool IsExiting { get; private set; }

    private const string ShowMainEventName = @"Local\XinDong.ShowMain.6F1C2B7A";

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 管理员授权子进程：只写入开机启动项，然后立刻退出，不启动主界面
        if (TryHandleStartupRegistration(e.Args))
        {
            return;
        }

        // 单实例：绿色软件被双击第二次时不要开出第二份计时器
        _mutex = new Mutex(true, "XinDong.SingleInstance.6F1C2B7A", out var createdNew);
        if (!createdNew)
        {
            // 第一个实例可能已经收进托盘 / 被老板键藏起来：通知它把主窗口叫出来
            TrySignalShowMain();
            Shutdown();
            return;
        }

        // 监听“再次双击”的唤出信号
        _showMainEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowMainEventName);
        _showMainThread = new Thread(WaitForShowMainSignal)
        {
            IsBackground = true,
            Name = "XinDong.ShowMain"
        };
        _showMainThread.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            MessageBox.Show("薪动遇到了一点问题：\n" + args.Exception.Message, "薪动 XinDong",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        _storage = new StorageService();
        _storage.EnsureDailyBackup();

        // 首次运行：没有 config 也没有账本时，让新用户自己填薪资类型和金额
        var configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xindong_config.json");
        var isFirstRun = !File.Exists(configPath);

        var config = _storage.LoadConfig();
        var ledger = _storage.LoadLedger();
        isFirstRun = isFirstRun && ledger.Records.Count == 0;

        // 便携目录可能被移动：开启自启动时静默刷新 Run 项路径
        var startupEnabled = StartupService.RefreshPathIfEnabled(config.StartWithWindows);
        if (config.StartWithWindows != startupEnabled)
        {
            config.StartWithWindows = startupEnabled;
            _storage.SaveConfig(config);
        }

        _vm = new MainViewModel(_storage, config, ledger);

        // 后端集成：仅在用户开启「加入排行」时创建 ApiService，否则纯单机零网络
        _deviceSvc = new DeviceIdService(_storage.BaseDirectory);
        if (config.JoinLeaderboard)
        {
            _api = new ApiService(_deviceSvc);
            var identity = _deviceSvc.LoadOrCreate();
            identity.ServerBaseUrl = config.ServerBaseUrl;
            _deviceSvc.Save(identity);
            _reportScheduler = new ReportScheduler(_api, _deviceSvc, _storage.BaseDirectory,
                () => _vm.Config, () => _vm.CurrentLedger);
            _vm.ReportNeeded += () => _ = _reportScheduler.ReportNowAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    await _api.EnsureRegisteredAsync(identity, _vm.Config, _vm.CurrentLedger);
                    _reportScheduler.Start();
                }
                catch
                {
                    // 注册失败静默，本地继续可用
                }
            });
        }
        _vm.Rank = new RankViewModel(_api, _deviceSvc, _storage.BaseDirectory, () => _vm.Config);

        // 记住 B1/B2 的伪装外观：没有激活老板键时也恢复上次选择的展示状态
        if (!config.BossKeyActive && config.BossKeyMode != BossKeyMode.Stealth)
        {
            _vm.SetCamouflage(true);
        }

        // 供自动化截图 / QA：--page=board --view=Weekly --date=2026-10-22 --calendar=expanded --detail
        ApplyStartupArguments(e.Args);

        _main = new MainWindow(_vm);
        MainWindow = _main;
        _island = new DynamicIslandWindow(_vm);

        _tray = new TrayIconService();
        _tray.ShowMainRequested += OnTrayShowMain;
        _tray.ShowIslandRequested += ToggleIsland;
        _tray.BossKeyRequested += ToggleBossKey;
        _tray.ExitRequested += ExitApplication;

        _vm.BossKeyRequested += (_, _) => ToggleBossKey();
        _vm.MinimizeToIslandRequested += (_, _) => MinimizeToIsland();
        _vm.AlertRequested += ShowAlert;
        _vm.BigSmallWeekPromptRequested += ShowBigSmallWeekPrompt;
        _vm.RoundingWarningRequested += ShowRoundingWarning;
        _vm.EmergencyCallOptionsRequested += ShowEmergencyCallOptions;
        _vm.ClockOutTimeRequested += ShowClockOutTime;
        _vm.IslandNoticeRequested += text => _island.ShowNotice(text);

        _main.Show();

        // 记忆老板键状态：上次退出时处于隐身 / 伪装，重启后继续保持，直到主动切换
        if (config.BossKeyActive)
        {
            EnterBossMode();
        }

        if (config.ShowIslandOnStart && !_bossActive)
        {
            ShowIsland();
        }

        // 全局老板键：优先 Alt + Z；被其它软件占用时自动降级到备用组合
        _hotkey = new HotKeyService();
        _hotkey.Pressed += (_, _) => ToggleBossKey();

        var candidates = new (ModifierKeys Modifiers, Key Key)[]
        {
            (ModifierKeys.Alt, Key.Z),
            (ModifierKeys.Control | ModifierKeys.Alt, Key.Z),
            (ModifierKeys.Alt | ModifierKeys.Shift, Key.Z),
            (ModifierKeys.Control | ModifierKeys.Shift, Key.Z)
        };

        (ModifierKeys Modifiers, Key Key)? chosen = null;
        foreach (var candidate in candidates)
        {
            if (_hotkey.Register(_main, candidate.Modifiers, candidate.Key))
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen is { } current)
        {
            var hotkeyText = HotKeyService.Format(current.Modifiers, current.Key);
            config.BossHotKey = hotkeyText;
            _tray.SetBossHotkey(hotkeyText);

            _vm.SetHotkeyStatus(hotkeyText == "Alt + Z"
                ? "✅ 全局老板键已就绪：Alt + Z（灵动岛中键同样可用）"
                : $"⚠️ Alt + Z 已被其它程序占用，已自动改用 {hotkeyText}（灵动岛中键同样可用）");
        }
        else
        {
            _vm.SetHotkeyStatus("⚠️ 老板键组合全部被占用，请改用灵动岛中键或托盘菜单。");
        }

        _vm.Start();

        if (isFirstRun)
        {
            Dispatcher.BeginInvoke(
                new Action(ShowSalarySetup),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>首次使用的薪资设置弹窗。</summary>
    private void ShowSalarySetup()
    {
        var dialog = new SalarySetupWindow { Owner = _main };
        if (dialog.ShowDialog() == true)
        {
            _vm.ApplySalarySetup(dialog.SelectedMode, dialog.SelectedAmount);
        }
    }

    /// <summary>处理 --set-startup=on/off 管理员授权子进程。</summary>
    private bool TryHandleStartupRegistration(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--get-startup", StringComparison.OrdinalIgnoreCase)))
        {
            var read = StartupService.TryGetEnabled(out var actualEnabled, out var readError);
            if (read) Console.WriteLine(actualEnabled ? "enabled" : "disabled");
            else Console.Error.WriteLine(readError);
            Shutdown(read ? 0 : 1);
            return true;
        }

        var argument = args.FirstOrDefault(a =>
            a.StartsWith("--set-startup=", StringComparison.OrdinalIgnoreCase));
        if (argument is null) return false;

        var enabled = string.Equals(argument, "--set-startup=on", StringComparison.OrdinalIgnoreCase);
        if (!enabled && !string.Equals(argument, "--set-startup=off", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("--set-startup must be on or off.");
            Shutdown(2);
            return true;
        }

        var ok = StartupService.TrySet(enabled, out var error);
        if (ok)
        {
            var startupStorage = new StorageService();
            var config = startupStorage.LoadConfig();
            config.StartWithWindows = enabled;
            if (!startupStorage.SaveConfig(config))
            {
                ok = false;
                error = startupStorage.LastError;
            }
        }
        if (!ok)
        {
            Console.Error.WriteLine(error);
        }
        Shutdown(ok ? 0 : 1);
        return true;
    }

    /// <summary>第二个实例通知第一个实例：把主窗口显示出来。</summary>
    private static void TrySignalShowMain()
    {
        try
        {
            using var showEvent = EventWaitHandle.OpenExisting(ShowMainEventName);
            showEvent.Set();
            return;
        }
        catch
        {
            // 第一个实例还没创建事件时，退回到创建同名事件再置位
        }

        try
        {
            using var showEvent = new EventWaitHandle(
                false, EventResetMode.AutoReset, ShowMainEventName);
            showEvent.Set();
        }
        catch
        {
            // 无权限时忽略；第一个实例仍可正常启动
        }
    }

    /// <summary>后台等待第二个实例的唤出信号。</summary>
    private void WaitForShowMainSignal()
    {
        while (true)
        {
            try
            {
                _showMainEvent?.WaitOne();
                Dispatcher.BeginInvoke(new Action(ShowMainFromSecondInstance));
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }
        }
    }

    /// <summary>响应再次双击：退出老板键、恢复主窗口并置前。</summary>
    private void ShowMainFromSecondInstance()
    {
        try
        {
            if (_bossActive)
            {
                ExitBossMode();
            }

            ShowMainWindow();
            if (_main.WindowState == WindowState.Minimized)
            {
                _main.WindowState = WindowState.Normal;
            }

            _main.Activate();
        }
        catch
        {
            // 退出过程中收到信号时忽略
        }
    }

    #region 窗口编排

    /// <summary>显示主控台。</summary>
    public void ShowMainWindow()
    {
        if (!_main.IsVisible)
        {
            _main.Show();
        }

        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    /// <summary>显示灵动岛（贴右上角，先展开 2.6 秒再自动收起）。</summary>
    public void ShowIsland()
    {
        if (!_island.IsVisible)
        {
            _island.Show();
        }

        _island.PlaceTopRight();
        _island.Pulse();
    }

    /// <summary>主窗口收进灵动岛。</summary>
    public void MinimizeToIsland()
    {
        _main.Hide();
        ShowIsland();
    }

    /// <summary>显示 / 隐藏灵动岛。</summary>
    private void ToggleIsland()
    {
        if (_island.IsVisible)
        {
            _island.Hide();
        }
        else
        {
            ShowIsland();
        }
    }

    private void OnTrayShowMain()
    {
        if (_bossActive)
        {
            ExitBossMode();
        }
        else
        {
            ShowMainWindow();
        }
    }

    #endregion

    #region 老板键

    /// <summary>切换老板键：未激活则进入伪装 / 隐身，已激活则恢复。</summary>
    public void ToggleBossKey()
    {
        if (_bossActive)
        {
            ExitBossMode();
        }
        else
        {
            EnterBossMode();
        }
    }

    /// <summary>进入老板键模式。</summary>
    private void EnterBossMode()
    {
        _bossActive = true;
        _wasMainVisible = _main.IsVisible;
        _wasIslandVisible = _island.IsVisible;
        _vm.Config.BossKeyActive = true;
        _storage.SaveConfig(_vm.Config);

        var mode = _vm.Config.BossKeyMode;
        _tray.SetStealth(true);

        if (mode == BossKeyMode.Stealth)
        {
            // 模式 A：主窗口与灵动岛全部消失，只留托盘灰图标
            _vm.SetCamouflage(false);
            _main.Hide();
            _island.Hide();
        }
        else
        {
            // 模式 B：主窗口消失，灵动岛变成工业 / Excel 暗语条
            _vm.SetCamouflage(true);
            _main.Hide();
            ShowIsland();
        }
    }

    /// <summary>退出老板键模式，按进入前的可见性恢复。</summary>
    private void ExitBossMode()
    {
        _bossActive = false;
        _vm.Config.BossKeyActive = false;
        _storage.SaveConfig(_vm.Config);
        _vm.SetCamouflage(_vm.Config.BossKeyMode != BossKeyMode.Stealth);
        _tray.SetStealth(false);

        if (_wasMainVisible)
        {
            ShowMainWindow();
        }

        if (_wasIslandVisible)
        {
            ShowIsland();
        }
        else
        {
            _island.Hide();
        }
    }

    #endregion

    #region 弹窗

    /// <summary>置顶提醒（如下班打卡、15 分钟摸鱼等需要打断的场景）。</summary>
    private void ShowAlert(string title, string message)
    {
        var dialog = new AlertWindow(title, message, "知道了", null, isDanger: true);
        AttachOwner(dialog);
        dialog.ShowDialog();
    }

    /// <summary>大小休询问：返回 true=大周（双休），false=小周（单休），null=取消。</summary>
    private bool? ShowBigSmallWeekPrompt(DateTime weekStart)
    {
        var dialog = new AlertWindow(
            "本周是大周还是小周？",
            $"{weekStart:yyyy-MM-dd}（周一）~ {weekStart.AddDays(6):MM-dd}（周日）\n" +
            "大周：周六日休 2 天；小周：仅周日休 1 天。选择后自动向前后交替，可在设置页修改本周。",
            "大周（双休）",
            "小周（单休）",
            isDanger: false);

        AttachOwner(dialog);
        return dialog.ShowDialog() switch
        {
            true => false,  // 次按钮：小周
            false => true,  // 主按钮：大周
            _ => null
        };
    }

    /// <summary>防抹零强拦截：返回 true 代表用户选择「含泪下班」。</summary>
    private bool ShowRoundingWarning(RoundingWarningInfo info)
    {
        var dialog = new AlertWindow(
            info.Title,
            info.Message,
            "再苟一会儿",
            "含泪下班",
            isDanger: true);

        AttachOwner(dialog);
        return dialog.ShowDialog() == true;
    }

    /// <summary>夺命 Call 报酬选择弹窗；取消返回 null。</summary>
    private EmergencyCallOptions? ShowEmergencyCallOptions()
    {
        var dialog = new EmergencyCallWindow();
        if (_main.IsVisible)
        {
            dialog.Owner = _main;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    /// <summary>确认下班时间弹窗；取消返回 null。</summary>
    private DateTime? ShowClockOutTime(ClockOutTimeContext context)
    {
        var dialog = new ClockOutTimeWindow(context);
        if (_main.IsVisible)
        {
            dialog.Owner = _main;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return dialog.ShowDialog() == true ? dialog.SelectedTime : null;
    }

    /// <summary>给弹窗指定一个合适的 Owner，保证置顶且居中。</summary>
    private void AttachOwner(AlertWindow dialog)
    {
        if (_main.IsVisible)
        {
            dialog.Owner = _main;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            // 主窗口收起时不要以 44px 高的灵动岛为 Owner，否则弹窗会被顶到屏幕外
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    #endregion

    #region 退出

    /// <summary>退出程序：保存账本、释放托盘与热键。</summary>
    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;

        try
        {
            _vm.SaveBeforeExit();
        }
        catch
        {
            // 退出时保存失败不阻塞退出
        }

        try
        {
            _reportScheduler?.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 退出时上报失败忽略
        }

        _hotkey?.Dispose();
        _tray?.Dispose();

        _island?.Close();
        _main?.Close();

        Shutdown();

        try
        {
            _mutex?.ReleaseMutex();
        }
        catch
        {
            // 非持有线程释放会抛异常，忽略
        }

        _mutex?.Dispose();
        _mutex = null;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _vm?.SaveBeforeExit();
        }
        catch
        {
            // 忽略
        }

        try
        {
            _reportScheduler?.FlushAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // 忽略
        }

        _api?.Dispose();
        _reportScheduler?.Dispose();
        _hotkey?.Dispose();
        _tray?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>
    /// 启动参数（便于自动化截图 / 回归）：
    /// --page=board --view=Daily|Weekly|Monthly|Yearly --date=yyyy-MM-dd
    /// --calendar=expanded --detail --analysis
    /// </summary>
    private void ApplyStartupArguments(string[] args)
    {
        string? Get(string key)
        {
            var prefix = "--" + key + "=";
            return args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                ?.Substring(prefix.Length);
        }

        if (DateTime.TryParse(Get("date"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            _vm.SetBoardDate(date);
        }

        var view = Get("view");
        if (!string.IsNullOrWhiteSpace(view))
        {
            _vm.SetViewMode(view);
        }

        if (string.Equals(Get("calendar"), "expanded", StringComparison.OrdinalIgnoreCase))
        {
            _vm.SetCalendarExpanded(true);
        }

        if (args.Any(a => string.Equals(a, "--detail", StringComparison.OrdinalIgnoreCase)))
        {
            _vm.ShowDetail();
        }

        if (args.Any(a => string.Equals(a, "--analysis", StringComparison.OrdinalIgnoreCase)))
        {
            _vm.SetAnalysisExpanded(true);
        }

        if (string.Equals(Get("page"), "board", StringComparison.OrdinalIgnoreCase))
        {
            _vm.SetPage(MainViewModel.PageBoard);
        }
        else if (string.Equals(Get("page"), "settings", StringComparison.OrdinalIgnoreCase))
        {
            _vm.SetPage(MainViewModel.PageSettings);
        }
    }

    /// <summary>把未处理异常写进 exe 目录，方便用户回传。</summary>
    private void WriteCrashLog(Exception ex)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "xindong_error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    #endregion
}
