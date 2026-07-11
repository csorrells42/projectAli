using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

internal static class Program
{
    private const string ProgrammingWindowTitle = "Programming";

    [STAThread]
    private static int Main(string[] args)
    {
        var options = AutomationOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(AutomationOptions.HelpText);
            return 0;
        }

        if (!options.Mode.Equals("programming", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("Only the programming automation mode is supported.");
            Console.WriteLine(AutomationOptions.HelpText);
            return 2;
        }

        Process? launchedProcess = null;
        AutomationElement? window = null;
        var steps = new List<ProgrammingAutomationStep>();
        try
        {
            if (!string.IsNullOrWhiteSpace(options.AppPath))
            {
                launchedProcess = LaunchAli(options);
            }

            window = WaitForProgrammingWindow(launchedProcess?.Id, options.Timeout);
            steps.Add(new ProgrammingAutomationStep("attached", CaptureSnapshot(window)));
            if (!string.IsNullOrWhiteSpace(options.ProjectPath))
            {
                SetValue(FindRequired(window, "ProgrammingProjectPathTextBox"), options.ProjectPath);
                steps.Add(new ProgrammingAutomationStep("project-set", CaptureSnapshot(window)));
            }

            if (options.RequireLiveRuntime)
            {
                WaitForLiveRuntime(window, options.Timeout);
                steps.Add(new ProgrammingAutomationStep("live-runtime-ready", CaptureSnapshot(window)));
            }

            for (var sendIndex = 0; sendIndex < options.SendTexts.Count; sendIndex++)
            {
                var beforeMessages = CountMessages(window);
                SetValue(FindRequired(window, "ProgrammingComposerTextBox"), options.SendTexts[sendIndex]);
                steps.Add(new ProgrammingAutomationStep($"send-{sendIndex + 1}-text-entered", CaptureSnapshot(window)));
                Invoke(FindRequired(window, "ProgrammingSendButton"), options.Timeout);
                WaitForIdle(window, options.Timeout, beforeMessages + 2);
                steps.Add(new ProgrammingAutomationStep($"send-{sendIndex + 1}-complete", CaptureSnapshot(window)));
            }

            for (var index = 0; index < options.NextCount; index++)
            {
                if (options.NextOnlyIfEnabled && !FindRequired(window, "ProgrammingNextButton").Current.IsEnabled)
                {
                    steps.Add(new ProgrammingAutomationStep($"next-{index + 1}-skipped-disabled", CaptureSnapshot(window)));
                    continue;
                }

                var beforeMessages = CountMessages(window);
                Invoke(FindRequired(window, "ProgrammingNextButton"), options.Timeout);
                WaitForIdle(window, options.Timeout, beforeMessages + 2);
                steps.Add(new ProgrammingAutomationStep($"next-{index + 1}-complete", CaptureSnapshot(window)));
            }

            var screenshotPath = CaptureScreenshotIfRequested(window, options);
            var run = new ProgrammingAutomationRun(CaptureSnapshot(window), steps, screenshotPath, string.Empty);
            Console.WriteLine(JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true }));
            if (options.ShutdownLaunchedApp && launchedProcess is not null && !launchedProcess.HasExited)
            {
                launchedProcess.Kill(entireProcessTree: true);
            }

            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            if (window is not null)
            {
                var snapshot = CaptureSnapshot(window);
                steps.Add(new ProgrammingAutomationStep("error", snapshot));
                var screenshotPath = CaptureScreenshotIfRequested(window, options);
                Console.WriteLine(JsonSerializer.Serialize(
                    new ProgrammingAutomationRun(snapshot, steps, screenshotPath, ex.Message),
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            if (options.ShutdownLaunchedApp && launchedProcess is not null && !launchedProcess.HasExited)
            {
                launchedProcess.Kill(entireProcessTree: true);
            }

            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static Process LaunchAli(AutomationOptions options)
    {
        var arguments = new List<string> { "--open-programming" };
        if (!string.IsNullOrWhiteSpace(options.ProjectPath))
        {
            arguments.Add("--programming-project");
            arguments.Add(options.ProjectPath);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = options.AppPath!,
            Arguments = JoinArguments(arguments),
            UseShellExecute = false
        };

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"Could not launch Ali from {options.AppPath}.");
    }

    private static AutomationElement WaitForProgrammingWindow(int? processId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var conditions = new List<Condition>
            {
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window),
                new PropertyCondition(AutomationElement.NameProperty, ProgrammingWindowTitle)
            };
            if (processId is not null)
            {
                conditions.Add(new PropertyCondition(AutomationElement.ProcessIdProperty, processId.Value));
            }

            var window = AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new AndCondition(conditions.ToArray()));
            if (window is not null)
            {
                return window;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for the Programming window.");
    }

    private static AutomationElement FindRequired(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element ?? throw new InvalidOperationException($"Could not find automation element '{automationId}'.");
    }

    private static IReadOnlyList<string> FindTextValues(AutomationElement root, string automationId)
    {
        var elements = root.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return elements
            .Cast<AutomationElement>()
            .Select(ReadName)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToArray();
    }

    private static void SetValue(AutomationElement element, string value)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
        {
            throw new InvalidOperationException($"Element '{element.Current.AutomationId}' does not support ValuePattern.");
        }

        ((ValuePattern)pattern).SetValue(value);
    }

    private static void Invoke(AutomationElement element, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (element.Current.IsEnabled)
            {
                if (!element.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern))
                {
                    throw new InvalidOperationException($"Element '{element.Current.AutomationId}' does not support InvokePattern.");
                }

                ((InvokePattern)pattern).Invoke();
                return;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"Timed out waiting for '{element.Current.AutomationId}' to become enabled.");
    }

    private static void WaitForIdle(AutomationElement window, TimeSpan timeout, int minimumMessageCount)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadOptionalName(window, "ProgrammingStatusText");
            var nextButton = FindRequired(window, "ProgrammingNextButton");
            var messageCount = CountMessages(window);
            if (messageCount >= minimumMessageCount
                && (!status.Contains("Streaming", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("Response complete", StringComparison.OrdinalIgnoreCase)
                    || status.Contains("No next programming step", StringComparison.OrdinalIgnoreCase)
                    || nextButton.Current.IsEnabled))
            {
                return;
            }

            Thread.Sleep(300);
        }

        throw new TimeoutException("Timed out waiting for Programming automation to become idle.");
    }

    private static void WaitForLiveRuntime(AutomationElement window, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ProgrammingAutomationSnapshot lastSnapshot;
        do
        {
            lastSnapshot = CaptureSnapshot(window);
            if (IsLiveRuntime(lastSnapshot))
            {
                return;
            }

            if (lastSnapshot.Status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                || lastSnapshot.Status.Contains("not configured", StringComparison.OrdinalIgnoreCase)
                || lastSnapshot.Status.Contains("cancelled", StringComparison.OrdinalIgnoreCase)
                || lastSnapshot.ModelConnectionStatus.Contains("offline", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            Thread.Sleep(500);
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new TimeoutException(
            "Timed out waiting for Ali to connect the configured live runtime. "
            + $"Runtime='{lastSnapshot.ActiveRuntimeStatus}', Connection='{lastSnapshot.ModelConnectionStatus}', Status='{lastSnapshot.Status}'.");
    }

    private static bool IsLiveRuntime(ProgrammingAutomationSnapshot snapshot) =>
        !snapshot.ActiveRuntimeStatus.Contains("deterministic stub", StringComparison.OrdinalIgnoreCase)
        && snapshot.ModelConnectionStatus.Equals("connected to model", StringComparison.OrdinalIgnoreCase);

    private static int CountMessages(AutomationElement window) =>
        FindTextValues(window, "ProgrammingMessageText").Count;

    private static ProgrammingAutomationSnapshot CaptureSnapshot(AutomationElement window)
    {
        var messages = FindTextValues(window, "ProgrammingMessageText");
        return new ProgrammingAutomationSnapshot(
            ReadOptionalName(window, "ProgrammingProjectPathTextBox"),
            ReadOptionalName(window, "ProgrammingCurrentTargetText"),
            ReadOptionalName(window, "ProgrammingCurrentTaskText"),
            ReadOptionalName(window, "ProgrammingStatusText"),
            ReadOptionalName(window, "ProgrammingActiveRuntimeStatusText"),
            ReadOptionalName(window, "ProgrammingModelConnectionStatusText"),
            messages,
            messages.LastOrDefault() ?? string.Empty,
            FindRequired(window, "ProgrammingNextButton").Current.IsEnabled,
            FindRequired(window, "ProgrammingSendButton").Current.IsEnabled);
    }

    private static string ReadOptionalName(AutomationElement root, string automationId)
    {
        var element = root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        return element is null ? string.Empty : ReadName(element);
    }

    private static string ReadName(AutomationElement element)
    {
        try
        {
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
            {
                return ((ValuePattern)pattern).Current.Value ?? string.Empty;
            }
        }
        catch (InvalidOperationException)
        {
        }

        return element.Current.Name ?? string.Empty;
    }

    private static string JoinArguments(IEnumerable<string> arguments)
    {
        static string Quote(string value) =>
            value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('"')
                ? "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
                : value;

        return string.Join(" ", arguments.Select(Quote));
    }

    private static string CaptureScreenshotIfRequested(AutomationElement window, AutomationOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ScreenshotPath))
        {
            return string.Empty;
        }

        var rectangle = window.Current.BoundingRectangle;
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new InvalidOperationException("Programming window bounds are not available for screenshot capture.");
        }

        var path = Path.GetFullPath(options.ScreenshotPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Environment.CurrentDirectory);
        using var bitmap = new Bitmap((int)Math.Ceiling(rectangle.Width), (int)Math.Ceiling(rectangle.Height));
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(
            (int)Math.Floor(rectangle.Left),
            (int)Math.Floor(rectangle.Top),
            0,
            0,
            bitmap.Size);
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}

internal sealed record ProgrammingAutomationSnapshot(
    string ProjectPath,
    string CurrentTarget,
    string CurrentTask,
    string Status,
    string ActiveRuntimeStatus,
    string ModelConnectionStatus,
    IReadOnlyList<string> Messages,
    string LastMessage,
    bool NextEnabled,
    bool SendEnabled);

internal sealed record ProgrammingAutomationStep(
    string Name,
    ProgrammingAutomationSnapshot Snapshot);

internal sealed record ProgrammingAutomationRun(
    ProgrammingAutomationSnapshot Final,
    IReadOnlyList<ProgrammingAutomationStep> Steps,
    string ScreenshotPath,
    string Error);

internal sealed record AutomationOptions(
    string Mode,
    string? AppPath,
    string? ProjectPath,
    IReadOnlyList<string> SendTexts,
    int NextCount,
    TimeSpan Timeout,
    bool NextOnlyIfEnabled,
    bool RequireLiveRuntime,
    string? ScreenshotPath,
    bool ShutdownLaunchedApp,
    bool ShowHelp)
{
    public const string HelpText = """
    Usage:
      Ali.App.Automation programming --app <Ali.App.Wpf.exe> --project <path.csproj> --send "make a simple WPF window..." [--require-live-runtime] [--next-count 1] [--next-if-enabled] [--screenshot <path.png>] [--timeout-ms 120000] [--shutdown-launched-app]
      Ali.App.Automation programming --send "..." [--send "diagnose last build failure"] [--next-count 1] [--next-if-enabled] [--screenshot <path.png>]

    If --app is supplied, the runner launches Ali with --open-programming and optional --programming-project.
    If --app is omitted, the runner attaches to an existing Programming window.
    --send may be supplied more than once; messages are sent in order in the same Programming window.
    If --require-live-runtime is supplied, the runner waits until Ali's Programming window reports an active non-stub runtime before entering text.
    """;

    public static AutomationOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(arg => arg is "-h" or "--help" or "/?"))
        {
            return new AutomationOptions("help", null, null, Array.Empty<string>(), 0, TimeSpan.FromSeconds(90), false, false, null, false, true);
        }

        var mode = args[0];
        string? appPath = null;
        string? projectPath = null;
        var sendTexts = new List<string>();
        var nextCount = 0;
        var timeout = TimeSpan.FromSeconds(90);
        var nextOnlyIfEnabled = false;
        var requireLiveRuntime = false;
        string? screenshotPath = null;
        var shutdownLaunchedApp = false;

        for (var index = 1; index < args.Count; index++)
        {
            var arg = args[index];
            string ReadValue()
            {
                if (index + 1 >= args.Count)
                {
                    throw new InvalidOperationException($"Missing value for {arg}.");
                }

                index++;
                return args[index];
            }

            if (arg.Equals("--app", StringComparison.OrdinalIgnoreCase))
            {
                appPath = ReadValue();
            }
            else if (arg.Equals("--project", StringComparison.OrdinalIgnoreCase))
            {
                projectPath = ReadValue();
            }
            else if (arg.Equals("--send", StringComparison.OrdinalIgnoreCase))
            {
                sendTexts.Add(ReadValue());
            }
            else if (arg.Equals("--next-count", StringComparison.OrdinalIgnoreCase))
            {
                nextCount = int.Parse(ReadValue(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg.Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase))
            {
                timeout = TimeSpan.FromMilliseconds(int.Parse(ReadValue(), System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (arg.Equals("--next-if-enabled", StringComparison.OrdinalIgnoreCase))
            {
                nextOnlyIfEnabled = true;
            }
            else if (arg.Equals("--require-live-runtime", StringComparison.OrdinalIgnoreCase))
            {
                requireLiveRuntime = true;
            }
            else if (arg.Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                screenshotPath = ReadValue();
            }
            else if (arg.Equals("--shutdown-launched-app", StringComparison.OrdinalIgnoreCase))
            {
                shutdownLaunchedApp = true;
            }
            else
            {
                throw new InvalidOperationException($"Unknown argument: {arg}");
            }
        }

        if (nextOnlyIfEnabled && nextCount == 0)
        {
            nextCount = 1;
        }

        return new AutomationOptions(mode, appPath, projectPath, sendTexts, Math.Max(0, nextCount), timeout, nextOnlyIfEnabled, requireLiveRuntime, screenshotPath, shutdownLaunchedApp, false);
    }
}
