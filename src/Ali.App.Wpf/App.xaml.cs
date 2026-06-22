using System.Windows;
using Ali.App.Wpf.ViewModels;
using Ali.Infrastructure.Bootstrap;

namespace Ali.App.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = AliServices.CreateForDesktop();
        var mainWindow = new MainWindow(new MainWindowViewModel(services));
        mainWindow.Show();
    }
}
