using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Ali.Core.Identity;
using Ali.App.Wpf.ViewModels;
using Ali.Infrastructure.Bootstrap;
using Ali.Infrastructure.Identity;

namespace Ali.App.Wpf;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var assistantProfile = LoadOrCreateAssistantProfile();
        var services = AliServices.CreateForDesktop(assistantProfile);
        var viewModel = new MainWindowViewModel(services);
        ApplyStartupAutomationArguments(viewModel, e.Args);
        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
        if (e.Args.Any(arg => arg.Equals("--open-programming", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (viewModel.OpenProgrammingDashboardCommand.CanExecute(null))
                {
                    viewModel.OpenProgrammingDashboardCommand.Execute(null);
                }
            }));
        }
    }

    private static void ApplyStartupAutomationArguments(MainWindowViewModel viewModel, string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].Equals("--programming-project", StringComparison.OrdinalIgnoreCase)
                || index + 1 >= args.Length)
            {
                continue;
            }

            viewModel.CodingCurrentSolutionOrProjectPathText = args[index + 1];
            index++;
        }
    }

    private static AssistantProfile LoadOrCreateAssistantProfile()
    {
        var dataRoot = AliServices.DesktopDataRoot;
        var existing = AssistantProfileStore.Load(dataRoot);
        if (existing is not null)
        {
            return existing;
        }

        var setupWindow = new AssistantSetupWindow();
        var result = setupWindow.ShowDialog();
        var selectedProfile = result == true
            ? setupWindow.AssistantProfile
            : AssistantProfile.CreateDefault();
        return AssistantProfileStore.Save(dataRoot, selectedProfile);
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
