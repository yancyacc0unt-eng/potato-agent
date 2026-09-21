using System.Windows.Input;

namespace GUI.ViewModels;

/// <summary>
/// 带参数版同步命令：把一段"接收 <c>CommandParameter</c>"的方法包成 <see cref="ICommand"/>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么要单独一个类型：现成的 <see cref="RelayCommand"/> 是<b>无参</b>的
/// （XAML 里给它 <c>CommandParameter</c> 会被直接丢掉），而会话列表这种"每条一个按钮"的界面
/// 必须靠 <c>CommandParameter="{Binding}"</c> 把被点的那一项传进来 —— 见
/// <see cref="SessionListViewModel.SelectCommand"/> / <see cref="SessionListViewModel.DeleteCommand"/>。
/// </para>
/// <para>
/// 参数不是 <typeparamref name="T"/> 时（null、或者绑成了别的类型）<see cref="CanExecute"/> 返回 false、
/// <see cref="Execute"/> 什么都不做 —— 命令绝不往外抛异常。
/// </para>
/// </remarks>
/// <typeparam name="T">命令参数期望的类型；用 <c>object</c> 表示"什么参数都收"。</typeparam>
public sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Func<T, bool>? _canExecute;

    /// <summary>建一个带参数的同步命令。</summary>
    /// <param name="execute">点下去干什么，参数就是 XAML 传进来的 <c>CommandParameter</c>。</param>
    /// <param name="canExecute">按钮能不能点；null = 参数类型对就能点。</param>
    public RelayCommand(Action<T> execute, Func<T, bool>? canExecute = null)
    {
        _execute = execute ?? throw new ArgumentNullException(nameof(execute));
        _canExecute = canExecute;
    }

    /// <summary>能不能点变了（按钮的禁用 / 可用由它驱动）。</summary>
    public event EventHandler? CanExecuteChanged;

    /// <summary>现在能不能点：参数类型对，并且 <c>canExecute</c> 说可以。</summary>
    public bool CanExecute(object? parameter) =>
        parameter is T typed && (_canExecute?.Invoke(typed) ?? true);

    /// <summary>执行。参数类型不对（含 null）就什么都不做，不会抛。</summary>
    public void Execute(object? parameter)
    {
        if (parameter is T typed)
        {
            _execute(typed);
        }
    }

    /// <summary>手动喊一声"重新算 CanExecute"（WPF 不会自动发现状态变了）。</summary>
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
