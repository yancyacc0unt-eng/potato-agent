using System.Windows.Input;

namespace GUI.ViewModels;

/// <summary>
/// 同步命令：把一段无参方法包成 <see cref="ICommand"/>，XAML 里直接 <c>Command="{Binding XxxCommand}"</c>。
/// </summary>
/// <remarks>执行体<b>不许往外抛异常</b>（会顺着 WPF 的命令管道炸到界面外）；要报错请写进 ViewModel 的状态文本。</remarks>
public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>建一个同步命令。</summary>
    /// <param name="execute">点下去干什么。</param>
    /// <param name="canExecute">按钮能不能点；null = 永远能点。</param>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>能不能点变了（按钮的禁用/可用由它驱动）。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>现在能不能点。</summary>
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <summary>执行。</summary>
    public void Execute(object? parameter) => _execute();

    /// <summary>手动喊一声"重新算 CanExecute"（WPF 不会自动发现状态变了）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// 异步命令：await 期间自动禁用自己，避免用户连点两下发两次请求。
/// </summary>
/// <remarks>
/// <see cref="ICommand.Execute"/> 是 <c>void</c>，所以这里本质是 <c>async void</c>：
/// 执行体抛出的异常无处可去，因此构造时可以给一个 <c>onError</c> 回调负责把它变成界面上的提示。
/// </remarks>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isRunning;

    /// <summary>建一个异步命令。</summary>
    /// <param name="execute">点下去干什么（返回的 Task 会被 await）。</param>
    /// <param name="canExecute">按钮能不能点；null = 永远能点。</param>
    /// <param name="onError">执行体抛异常时收尾用；null = 静默吞掉（不推荐）。</param>
    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
        _onError = onError;
    }

    /// <summary>能不能点变了。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>true = 这个命令正在跑（界面可以显示"处理中…"）。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>空闲且外部条件允许时才能点。</summary>
    public bool CanExecute(object? parameter) => !_isRunning && (_canExecute?.Invoke() ?? true);

    /// <summary>执行；执行期间 <see cref="CanExecute"/> 返回 false。</summary>
    public async void Execute(object? parameter)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _onError?.Invoke(ex);
        }
        finally
        {
            _isRunning = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>手动喊一声"重新算 CanExecute"。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
