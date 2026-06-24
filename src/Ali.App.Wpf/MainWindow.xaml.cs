using System.Windows;
using System.Windows.Input;
using Ali.App.Wpf.ViewModels;

namespace Ali.App.Wpf;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closing;
    private Task? _startupTask;

    public MainWindow(MainWindowViewModel viewModel)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Messages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => MessagesScrollViewer.ScrollToEnd()));
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            _startupTask = viewModel.StartLocalRuntimeAsync();
            await _startupTask.ConfigureAwait(true);
        }
    }

    private async void MainWindow_OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        if (_closing)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        _closing = true;
        if (DataContext is MainWindowViewModel viewModel)
        {
            if (_startupTask is { IsCompleted: false })
            {
                await _startupTask.ConfigureAwait(true);
            }

            await viewModel.ShutdownLocalRuntimeAsync().ConfigureAwait(true);
        }

        _allowClose = true;
        Close();
    }

    private async void ComposerTextBox_OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (DataContext is MainWindowViewModel currentViewModel)
            {
                await currentViewModel.AddClipboardImageAsync().ConfigureAwait(true);
                if (System.Windows.Clipboard.ContainsImage())
                {
                    e.Handled = true;
                }
            }

            return;
        }

        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;

        if (DataContext is MainWindowViewModel sendViewModel)
        {
            await sendViewModel.SendAsync().ConfigureAwait(true);
        }
    }

    private void HistoryMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { ContextMenu: { } menu } button)
        {
            return;
        }

        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

}
