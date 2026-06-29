using System.Diagnostics;
using System.Windows.Input;

namespace Ali.App.Wpf.ViewModels;

public sealed class RelayCommand(
    Action<object?> execute,
    Predicate<object?>? canExecute = null,
    Action<Exception>? onException = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        try
        {
            execute(parameter);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            onException?.Invoke(ex);
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
