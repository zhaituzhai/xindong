using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace XinDong.Services;

/// <summary>
/// 全局热键服务：封装 Win32 <c>RegisterHotKey</c> / <c>UnregisterHotKey</c>，
/// 用于老板键（默认 Alt+Z）在任何窗口前台时都能 0.1 秒内响应。
/// </summary>
public sealed class HotKeyService : IDisposable
{
    private const int WmHotKey = 0x0312;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private HwndSource? _source;
    private IntPtr _hwnd = IntPtr.Zero;
    private bool _disposed;

    /// <summary>热键触发时在 UI 线程回调。</summary>
    public event EventHandler? Pressed;

    /// <summary>是否注册成功。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>注册失败原因（例如组合键已被其它软件占用）。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>实际注册成功的修饰键。</summary>
    public ModifierKeys RegisteredModifiers { get; private set; }

    /// <summary>实际注册成功的按键。</summary>
    public Key RegisteredKey { get; private set; }

    /// <summary>把热键格式化成 "Ctrl + Alt + Z"。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// 在指定窗口上注册全局热键。窗口句柄必须已创建（Show 之后调用）。
    /// </summary>
    public bool Register(Window window, ModifierKeys modifiers, Key key)
    {
        Unregister();

        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero)
        {
            LastError = "窗口句柄尚未创建，无法注册全局热键。";
            return false;
        }

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        // MOD_NOREPEAT：按住不放只触发一次，避免老板键被连续触发导致闪烁
        uint mods = ModNoRepeat;
        if ((modifiers & ModifierKeys.Alt) != 0) mods |= ModAlt;
        if ((modifiers & ModifierKeys.Control) != 0) mods |= ModControl;
        if ((modifiers & ModifierKeys.Shift) != 0) mods |= ModShift;
        if ((modifiers & ModifierKeys.Windows) != 0) mods |= ModWin;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);

        // 注册到当前窗口线程的消息队列，WM_HOTKEY 由 WndProc 接收
        IsRegistered = RegisterHotKey(_hwnd, 0x5844, mods, vk);
        if (!IsRegistered)
        {
            var code = Marshal.GetLastWin32Error();
            LastError = code != 0
                ? $"全局热键注册失败（Win32 错误 {code}）：{new Win32Exception(code).Message}"
                : "全局热键注册失败，可能已被其它程序占用。";
        }
        else
        {
            LastError = string.Empty;
            RegisteredModifiers = modifiers;
            RegisteredKey = key;
        }

        return IsRegistered;
    }

    /// <summary>取消注册并移除消息钩子。</summary>
    public void Unregister()
    {
        if (IsRegistered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, 0x5844);
        }

        IsRegistered = false;
        RegisteredModifiers = ModifierKeys.None;
        RegisteredKey = Key.None;

        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }

    /// <summary>窗口消息钩子：只处理 WM_HOTKEY。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotKey && wParam.ToInt32() == 0x5844)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }

    #region Win32

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    #endregion
}

/// <summary>
/// 系统托盘服务：
/// 托盘图标保持完全透明（不显示品牌图标），提供四个菜单项与双击恢复；
/// 老板键隐身时 ToolTip 伪装成系统服务（System Host Service: Normal）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showMainItem;
    private readonly Forms.ToolStripMenuItem _showIslandItem;
    private readonly Forms.ToolStripMenuItem _bossItem;

    private Drawing.Icon? _icon;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _disposed;

    /// <summary>请求显示主控台。</summary>
    public event Action? ShowMainRequested;

    /// <summary>请求显示 / 隐藏灵动岛。</summary>
    public event Action? ShowIslandRequested;

    /// <summary>请求切换老板键模式。</summary>
    public event Action? BossKeyRequested;

    /// <summary>请求退出程序。</summary>
    public event Action? ExitRequested;

    /// <summary>构造托盘图标并挂载菜单。</summary>
    public TrayIconService()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "薪动 XinDong · 正在计算你的血汗钱"
        };

        var menu = new Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            Font = new Drawing.Font("Microsoft YaHei UI", 9f)
        };

        _showMainItem = new Forms.ToolStripMenuItem("显示主控台");
        _showMainItem.Click += (_, _) => ShowMainRequested?.Invoke();
        menu.Items.Add(_showMainItem);

        _showIslandItem = new Forms.ToolStripMenuItem("显示 / 隐藏灵动岛");
        _showIslandItem.Click += (_, _) => ShowIslandRequested?.Invoke();
        menu.Items.Add(_showIslandItem);

        _bossItem = new Forms.ToolStripMenuItem("老板键（Alt + Z）");
        _bossItem.Click += (_, _) => BossKeyRequested?.Invoke();
        menu.Items.Add(_bossItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = new Forms.ToolStripMenuItem("退出薪动");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();
        menu.Items.Add(exitItem);

        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowMainRequested?.Invoke();

        ApplyIcon(stealth: false);
    }

    /// <summary>更新托盘菜单里的老板键文案（冲突回退时使用）。</summary>
    public void SetBossHotkey(string hotkeyText)
    {
        _bossItem.Text = "老板键（" + hotkeyText + "）";
    }

    /// <summary>
    /// 切换隐身态：透明图标 + 系统服务文案，或恢复品牌提示文案。
    /// </summary>
    public void SetStealth(bool stealth)
    {
        _notifyIcon.Text = stealth
            ? "System Host Service: Normal"
            : "薪动 XinDong · 正在计算你的血汗钱";
        ApplyIcon(stealth);

        _showMainItem.Text = stealth ? "恢复主控台" : "显示主控台";
        _showIslandItem.Enabled = !stealth;
    }

    /// <summary>重新绘制并应用图标（旧的 HICON 必须销毁，避免 GDI 句柄泄漏）。</summary>
    private void ApplyIcon(bool stealth)
    {
        try
        {
            var newIcon = CreateIcon(stealth);

            if (_hIcon != IntPtr.Zero)
            {
                DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            _icon?.Dispose();
            _icon = newIcon;
            _notifyIcon.Icon = _icon;
        }
        catch
        {
            // 托盘图标绘制失败不应该影响主程序运行
        }
    }

    /// <summary>
    /// 托盘图标保持完全透明：不显示品牌图标，避免在通知区域被一眼看到。
    /// </summary>
    private Drawing.Icon CreateIcon(bool stealth)
    {
        _ = stealth;

        var bitmap = new Drawing.Bitmap(16, 16, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bitmap))
        {
            g.Clear(Drawing.Color.Transparent);
        }

        _hIcon = bitmap.GetHicon();
        bitmap.Dispose();
        return Drawing.Icon.FromHandle(_hIcon);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon?.Dispose();

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
