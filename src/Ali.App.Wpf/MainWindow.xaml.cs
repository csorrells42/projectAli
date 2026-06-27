using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ali.App.Wpf.ViewModels;

namespace Ali.App.Wpf;

public partial class MainWindow : Window
{
    private bool _allowClose;
    private bool _closing;
    private Task? _startupTask;
    private IInputElement? _prePushToTalkFocus;

    public MainWindow(MainWindowViewModel viewModel)
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Messages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(new Action(() => MessagesScrollViewer.ScrollToEnd()));
        Loaded += MainWindow_OnLoaded;
        Closing += MainWindow_OnClosing;
        PreviewKeyDown += MainWindow_OnPreviewKeyDown;
        PreviewKeyUp += MainWindow_OnPreviewKeyUp;
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
            viewModel.RequestShutdownCancellation();
            if (_startupTask is { IsCompleted: false })
            {
                await _startupTask.ConfigureAwait(true);
            }

            await viewModel.ShutdownLocalRuntimeAsync().ConfigureAwait(true);
        }

        _allowClose = true;
        Close();
    }

    private async void ComposerTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
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

    private async void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.IsRepeat
            || DataContext is not MainWindowViewModel viewModel
            || !viewModel.IsPushToTalkKey(e.Key))
        {
            return;
        }

        e.Handled = true;
        if (_prePushToTalkFocus is null && TryGetTextEntryFocus(out var focusedElement))
        {
            _prePushToTalkFocus = focusedElement;
            MoveFocusOutOfTextEntry();
        }

        await viewModel.StartPushToTalkAsync().ConfigureAwait(true);
        if (!viewModel.IsPushToTalkActive)
        {
            RestoreFocusAfterPushToTalk();
        }
    }

    private async void MainWindow_OnPreviewKeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsPushToTalkKey(e.Key))
        {
            return;
        }

        e.Handled = true;
        await viewModel.StopPushToTalkAsync().ConfigureAwait(true);
        RestoreFocusAfterPushToTalk();
    }

    private static bool IsTextEntryFocus()
    {
        return TryGetTextEntryFocus(out _);
    }

    private static bool TryGetTextEntryFocus(out IInputElement? focusedElement)
    {
        var current = Keyboard.FocusedElement as DependencyObject;
        while (current is not null)
        {
            if (current is System.Windows.Controls.Primitives.TextBoxBase
                or System.Windows.Controls.PasswordBox
                or System.Windows.Controls.ComboBox)
            {
                focusedElement = Keyboard.FocusedElement;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        focusedElement = null;
        return false;
    }

    private void MoveFocusOutOfTextEntry()
    {
        FocusManager.SetFocusedElement(this, this);
        Keyboard.ClearFocus();
        Focus();
    }

    private void RestoreFocusAfterPushToTalk()
    {
        if (_prePushToTalkFocus is not { } focus)
        {
            return;
        }

        _prePushToTalkFocus = null;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (focus is UIElement { IsVisible: true, IsEnabled: true } element)
            {
                element.Focus();
            }
        }));
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

    private void CommandExplorerTree_OnSelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainWindowViewModel viewModel && e.NewValue is CommandExplorerNodeViewModel node)
        {
            viewModel.SelectedCommandExplorerNode = node;
        }
    }

}
