using System.Windows.Input;

namespace XinDong.ViewModels;

/// <summary>
/// 轻量 ICommand 实现（不依赖任何 MVVM 框架）。
/// CanExecuteChanged 直接桥接 WPF 的 <see cref="CommandManager.RequerySuggested"/>，
/// 状态机切换后调用 <see cref="CommandManager.InvalidateRequerySuggested"/> 即可刷新按钮可用性。
/// </summary>
public sealed class RelayCommand : ICommand
{
    private readonly Action<object?> _execute;
    private readonly Predicate<object?>? _canExecute;

    /// <summary>构造命令。</summary>
    /// <param name="execute">执行体。</param>
    /// <param name="canExecute">可用性判断，默认恒可用。</param>
    public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute(parameter);

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <summary>手动触发一次可用性重算（当 CommandManager 不活跃时使用）。</summary>
    public void RaiseCanExecuteChanged() => CommandManager.InvalidateRequerySuggested();
}
