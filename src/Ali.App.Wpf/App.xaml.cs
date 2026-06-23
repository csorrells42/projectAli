using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Ali.App.Wpf.ViewModels;
using Ali.Infrastructure.Bootstrap;

namespace Ali.App.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        var services = AliServices.CreateForDesktop();
        var mainWindow = new MainWindow(new MainWindowViewModel(services));
        mainWindow.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteExceptionLog("Dispatcher", e.Exception);
        if (MainWindow?.DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.ReportApplicationFailure("Application", e.Exception);
        e.Handled = true;
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteExceptionLog("Unhandled", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteExceptionLog("Unobserved task", e.Exception);
        e.SetObserved();
    }

    private static void WriteExceptionLog(string source, Exception exception)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ali",
                "Logs");
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "ali-crash.log");
            var text = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] {source}")
                .AppendLine(exception.ToString())
                .AppendLine()
                .ToString();
            File.AppendAllText(path, text);
        }
        catch
        {
            // Last-chance logging must never cause another crash.
        }
    }
}
