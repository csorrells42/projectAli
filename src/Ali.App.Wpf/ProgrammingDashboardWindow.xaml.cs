using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Ali.App.Wpf.ViewModels;

namespace Ali.App.Wpf;

public partial class ProgrammingDashboardWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private Window? _hiddenOwner;

    public ProgrammingDashboardWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        Loaded += ProgrammingDashboardWindow_OnLoaded;
        Closed += ProgrammingDashboardWindow_OnClosed;
    }

    private void ProgrammingDashboardWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Owner is { IsVisible: true } owner)
        {
            _hiddenOwner = owner;
            owner.Hide();
        }

        if (DataContext is MainWindowViewModel viewModel)
        {
            _viewModel = viewModel;
            viewModel.Messages.CollectionChanged += Messages_OnCollectionChanged;
            viewModel.SuspendVoiceFeaturesForProgramming();
        }

        MessagesScrollViewer.ScrollToEnd();
        ProgrammingComposerTextBox.Focus();
    }

    private void ProgrammingDashboardWindow_OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.Messages.CollectionChanged -= Messages_OnCollectionChanged;
            _viewModel.RestoreVoiceFeaturesAfterProgramming();
            _viewModel = null;
        }

        if (_hiddenOwner is not null)
        {
            _hiddenOwner.Show();
            _hiddenOwner.Activate();
            _hiddenOwner = null;
        }
    }

    private void Messages_OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() => MessagesScrollViewer.ScrollToEnd()));
    }

    private async void ProgrammingComposerTextBox_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            return;
        }

        e.Handled = true;
        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.SendAsync().ConfigureAwait(true);
        }
    }

    private void CloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
