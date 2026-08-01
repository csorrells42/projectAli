using System.Diagnostics;
using System.Windows.Input;

namespace Ali.UI.ViewModels;

public sealed class AsyncRelayCommand(
    Func<Task> execute,
    Func<bool>? canExecute = null,
    Action<Exception>? onException = null,
    bool allowExecutionWhileRunning = false) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) =>
        (!_isExecuting || allowExecutionWhileRunning)
        && (canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        if (_isExecuting)
        {
            try
            {
                await execute().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                onException?.Invoke(ex);
            }
            return;
        }

        try
        {
            _isExecuting = true;
            RaiseCanExecuteChanged();
            await execute().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            onException?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
