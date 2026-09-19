using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GUI.ViewModels;

/// <summary>
/// 所有 ViewModel 的基类：只实现 <see cref="INotifyPropertyChanged"/>，不引任何 MVVM 框架。
/// </summary>
/// <remarks>
/// 界面重画约定：XAML 只负责 <c>{Binding 属性名}</c> 与 <c>Command="{Binding 命令名}"</c>，
/// 属性变化通知、命令状态、业务规则全部留在 ViewModel 里，换界面不需要改这一层。
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>某个可绑定属性变了。XAML 的 <c>{Binding}</c> 靠这条通知刷新。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>发一条属性变化通知；不传名字时自动取调用方的属性/方法名。</summary>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    /// <summary>
    /// 赋值并在"值真的变了"时发通知。<b>派生类的每个 setter 都应该走这里</b>，
    /// 否则界面要么不刷新，要么被同值写入刷成死循环。
    /// </summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">属性背后的字段（<c>ref</c> 传入）。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名，默认由编译器填。</param>
    /// <returns>true = 值变了并已发通知；false = 新旧相同，什么都没做。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
