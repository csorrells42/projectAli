using System.Windows;
using System.Windows.Input;
using Ali.App.Wpf.ViewModels;

namespace Ali.App.Wpf;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void ComposerTextBox_OnKeyDown(object sender, KeyEventArgs e)
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
}
