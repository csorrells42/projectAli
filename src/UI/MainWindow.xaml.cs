using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Ali.Modules.Interaction;
using Ali.Modules.Storage;
using Ali.UI.ViewModels;
using AvatarBuilder.Modules.Viewports.Diagnostics;
using MessageBox = System.Windows.MessageBox;

namespace Ali.UI;

public partial class MainWindow : Window
{
    private static readonly TimeSpan StartupCloseWait = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ShutdownCloseWait = TimeSpan.FromMilliseconds(1500);
    private bool _allowClose;
    private bool _closing;
    private Task? _startupTask;
    private IInputElement? _prePushToTalkFocus;
    private ExpandedViewportWindow? _expandedViewportWindow;
    private IdentityReviewWindow? _identityReviewWindow;
    private AliIdentityReviewSession? _identityReviewSession;
    private PipelineTimingWindow? _pipelineTimingWindow;

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
            var visionStartup = viewModel.InitializeVisionAsync();
            var mcpServerStartup = viewModel.InitializeMcpServerAsync();
            await Task.WhenAll(_startupTask, visionStartup, mcpServerStartup).ConfigureAwait(true);
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
        Hide();
        try
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.RequestShutdownCancellation();
                if (_startupTask is { IsCompleted: false } startupTask)
                {
                    var startupCompletion = await Task.WhenAny(
                        startupTask,
                        Task.Delay(StartupCloseWait)).ConfigureAwait(true);
                    if (ReferenceEquals(startupCompletion, startupTask))
                    {
                        await startupTask.ConfigureAwait(true);
                    }
                }

                if (_startupTask is not { IsCompleted: false })
                {
                    var shutdownTask = viewModel.ShutdownLocalRuntimeAsync();
                    var shutdownCompletion = await Task.WhenAny(
                        shutdownTask,
                        Task.Delay(ShutdownCloseWait)).ConfigureAwait(true);
                    if (ReferenceEquals(shutdownCompletion, shutdownTask))
                    {
                        await shutdownTask.ConfigureAwait(true);
                    }
                }
            }
        }
        catch
        {
            // Closing must remain reliable even when camera or runtime cleanup faults.
        }
        finally
        {
            _allowClose = true;
            Close();
        }
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

    private async void PushToTalkButton_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is UIElement element)
        {
            element.CaptureMouse();
            element.Focus();
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.StartPushToTalkAsync().ConfigureAwait(true);
        }
    }

    private async void PushToTalkButton_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (sender is UIElement { IsMouseCaptured: true } element)
        {
            element.ReleaseMouseCapture();
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.StopPushToTalkAsync().ConfigureAwait(true);
        }
    }

    private async void PushToTalkButton_OnLostMouseCapture(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            return;
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.StopPushToTalkAsync().ConfigureAwait(true);
        }
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

    private void CloseMenuItem_OnClick(object sender, RoutedEventArgs e) => Close();

    private void OpenIdentityReviewMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_identityReviewWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        if (DataContext is not MainWindowViewModel viewModel
            || viewModel.CreateIdentityReviewSession() is not { } session)
        {
            MessageBox.Show(
                this,
                "The interaction and identity services are not available.",
                "Identity Review",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var window = new IdentityReviewWindow(
                session.Service,
                session.LiveViewport,
                session.Guidance,
                session.SpeakerEnrollment,
                session.MicrophoneInput)
            {
                Owner = this
            };
            _identityReviewSession = session;
            _identityReviewWindow = window;
            window.Closed += IdentityReviewWindow_OnClosed;
            window.Show();
        }
        catch
        {
            session.Guidance?.Dispose();
            viewModel.RestoreIdentityReviewViewport(session);
            throw;
        }
    }

    private void IdentityReviewWindow_OnClosed(object? sender, EventArgs e)
    {
        if (sender is IdentityReviewWindow window)
        {
            window.Closed -= IdentityReviewWindow_OnClosed;
        }

        if (DataContext is MainWindowViewModel viewModel
            && _identityReviewSession is { } session)
        {
            viewModel.RestoreIdentityReviewViewport(session);
        }

        _identityReviewWindow = null;
        _identityReviewSession = null;
    }

    private void OpenDataFolderMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        var currentFolder = AliServices.LocalAliRoot;
        var dialog = new AliDataFolderDialog(currentFolder) { Owner = this };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var selectedFolder = Path.GetFullPath(dialog.SelectedFolder);
        if (string.Equals(
            selectedFolder,
            Path.GetFullPath(currentFolder),
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AliDataFolderSelectionStore.Save(selectedFolder);
        MessageBox.Show(
            this,
            $"Ali's data folder is now set to:{Environment.NewLine}{selectedFolder}"
                + $"{Environment.NewLine}{Environment.NewLine}Restart Ali to use the selected folder.",
            "Ali Data Folder",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OpenPipelineTimingMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (_pipelineTimingWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }

        if (DataContext is not MainWindowViewModel { FramePipelineTiming: { } timing })
        {
            MessageBox.Show(
                this,
                "Turn the camera on before opening frame pipeline timing.",
                "Frame Pipeline Timing",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        var window = new PipelineTimingWindow(timing) { Owner = this };
        _pipelineTimingWindow = window;
        window.Closed += (_, _) => _pipelineTimingWindow = null;
        window.Show();
    }

    private void ExpandWebcam_OnClick(object sender, RoutedEventArgs e)
    {
        if (_expandedViewportWindow is { IsVisible: true } existing)
        {
            existing.Activate();
            return;
        }
        if (DataContext is not MainWindowViewModel viewModel
            || viewModel.DetachVisionViewport() is not { } viewport)
        {
            return;
        }
        var popup = new ExpandedViewportWindow(viewport) { Owner = this };
        _expandedViewportWindow = popup;
        popup.Closed += (_, _) =>
        {
            var returningViewport = popup.DetachViewport();
            _expandedViewportWindow = null;
            if (returningViewport is not null)
            {
                viewModel.RestoreVisionViewport(returningViewport);
            }
        };
        popup.Show();
    }

}

