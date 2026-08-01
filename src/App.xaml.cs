using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Ali.Modules.Identity;
using Ali.UI.ViewModels;
using Ali.UI;

namespace Ali;

public partial class App : System.Windows.Application
{
    private AliServices? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var assistantProfile = LoadOrCreateAssistantProfile();
        var services = AliServices.CreateForDesktop(assistantProfile);
        _services = services;
        var viewModel = new MainWindowViewModel(services);
        var mainWindow = new MainWindow(viewModel);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        TryShutdown(() =>
            _services?.OrchestrationObserver?.DisposeAsync().AsTask().GetAwaiter().GetResult());
        TryShutdown(() =>
            _services?.UserMemories.DisposeAsync().AsTask().GetAwaiter().GetResult());
        TryShutdown(() =>
            _services?.CodingModule.DisposeAsync().AsTask().GetAwaiter().GetResult());
        TryShutdown(() =>
            _services?.Qdrant.StopAsync().GetAwaiter().GetResult());
        base.OnExit(e);
    }

    private static void TryShutdown(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch
        {
            // Every owned component gets its own cleanup attempt during shutdown.
        }
    }

    private static AssistantProfile LoadOrCreateAssistantProfile()
    {
        AliServices.EnsureLocalAliFilesLayout();
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
            var root = Path.Combine(AliServices.DesktopUserDataRoot, "Logs");
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

