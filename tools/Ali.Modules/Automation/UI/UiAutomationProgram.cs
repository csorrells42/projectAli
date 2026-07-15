using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

namespace Ali.Modules.Automation.UI;

internal static class UiAutomationProgram
{
    [STAThread]
    public static int Run(string[] args)
    {
        var options = AutomationOptions.Parse(args);
        if (options.ShowHelp)
        {
            Console.WriteLine(AutomationOptions.HelpText);
            return 0;
        }

        return options.Mode.Equals("chat", StringComparison.OrdinalIgnoreCase)
            ? RunChatAutomation(options)
            : FailWithHelp($"Unknown automation mode: {options.Mode}");
    }

    private static int RunChatAutomation(AutomationOptions options)
    {
        Process? launchedProcess = null;
        AutomationElement? window = null;
        AutomationElement? settingsWindow = null;
        var steps = new List<ChatAutomationStep>();
        var preLaunchProcessIds = new HashSet<int>();
        try
        {
            if (!string.IsNullOrWhiteSpace(options.AppPath))
            {
                preLaunchProcessIds = CaptureProcessIdsForApp(options.AppPath);
                launchedProcess = LaunchAli(options);
            }

            window = WaitForMainChatWindow(launchedProcess, options.AppPath, preLaunchProcessIds, options.Timeout);
            steps.Add(new ChatAutomationStep("attached", CaptureChatSnapshot(window)));

            if (options.RequireLiveRuntime)
            {
                WaitForChatLiveRuntime(window, options.Timeout);
                steps.Add(new ChatAutomationStep("live-runtime-ready", CaptureChatSnapshot(window)));
            }

            if (!string.IsNullOrWhiteSpace(options.VoiceSampleEngine))
            {
                settingsWindow = VerifyVoiceSample(window, options.VoiceSampleEngine, options.Timeout, steps);
            }

            if (options.CheckInternetSettings || options.TestConfiguredInternetBackends)
            {
                settingsWindow = VerifyInternetSettings(window, options.TestConfiguredInternetBackends, options.Timeout, steps);
            }

            if (options.PushToTalkHoldMilliseconds > 0)
            {
                HoldPushToTalk(window, options.PushToTalkHoldMilliseconds, options.Timeout, steps);
            }

            for (var sendIndex = 0; sendIndex < options.SendTexts.Count; sendIndex++)
            {
                var beforeMessages = CountChatMessages(window);
                SetValue(FindRequired(window, "MainChatComposerTextBox"), options.SendTexts[sendIndex]);
                steps.Add(new ChatAutomationStep($"send-{sendIndex + 1}-text-entered", CaptureChatSnapshot(window)));
                Invoke(FindRequired(window, "MainChatSendButton"), options.Timeout);
                WaitForChatIdle(window, options.Timeout, beforeMessages + 2);
                steps.Add(new ChatAutomationStep($"send-{sendIndex + 1}-complete", CaptureChatSnapshot(window)));
            }

            var screenshotPath = CaptureScreenshotIfRequested(settingsWindow ?? window, options);
            Console.WriteLine(JsonSerializer.Serialize(
                new ChatAutomationRun(CaptureChatSnapshot(window, settingsWindow), steps, screenshotPath, string.Empty),
                new JsonSerializerOptions { WriteIndented = true }));
            ShutdownIfRequested(options, launchedProcess);
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or COMException)
        {
            if (window is not null)
            {
                var snapshot = CaptureChatSnapshot(window);
                steps.Add(new ChatAutomationStep("error", snapshot));
                var screenshotPath = CaptureScreenshotIfRequested(window, options);
                Console.WriteLine(JsonSerializer.Serialize(
                    new ChatAutomationRun(snapshot, steps, screenshotPath, ex.Message),
                    new JsonSerializerOptions { WriteIndented = true }));
            }

            ShutdownIfRequested(options, launchedProcess);
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static int FailWithHelp(string message)
    {
        Console.Error.WriteLine(message);
        Console.WriteLine(AutomationOptions.HelpText);
        return 2;
    }

    private static Process LaunchAli(AutomationOptions options)
    {
        var appPath = Path.GetFullPath(options.AppPath!);
        var startInfo = new ProcessStartInfo
        {
            FileName = appPath,
            Arguments = string.Empty,
            WorkingDirectory = Path.GetDirectoryName(appPath) ?? Environment.CurrentDirectory,
            UseShellExecute = false
        };
        if (!string.IsNullOrWhiteSpace(options.LocalRoot))
        {
            startInfo.Environment["ALI_LOCAL_ROOT"] = Path.GetFullPath(options.LocalRoot);
        }

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"Could not launch Ali from {options.AppPath}.");
    }

    private static AutomationElement WaitForMainChatWindow(
        Process? launchedProcess,
        string? launchedAppPath,
        IReadOnlySet<int> preLaunchProcessIds,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var window in FindTopLevelWindows())
            {
                if (FindOptional(window, "MainChatComposerTextBox") is not null
                    && FindOptional(window, "MainChatSendButton") is not null
                    && IsExpectedChatWindow(window, launchedProcess, launchedAppPath, preLaunchProcessIds))
                {
                    return window;
                }
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for the main Ali chat window.");
    }

    private static IEnumerable<AutomationElement> FindTopLevelWindows()
    {
        try
        {
            return AutomationElement.RootElement
                .FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window))
                .Cast<AutomationElement>()
                .ToArray();
        }
        catch (COMException)
        {
            return Array.Empty<AutomationElement>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<AutomationElement>();
        }
    }

    private static bool IsExpectedChatWindow(
        AutomationElement window,
        Process? launchedProcess,
        string? launchedAppPath,
        IReadOnlySet<int> preLaunchProcessIds)
    {
        if (launchedProcess is null || string.IsNullOrWhiteSpace(launchedAppPath))
        {
            return true;
        }

        int windowProcessId;
        try
        {
            windowProcessId = window.Current.ProcessId;
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (windowProcessId == launchedProcess.Id)
        {
            return true;
        }

        if (!IsAliAppProcess(windowProcessId, launchedAppPath))
        {
            return false;
        }

        if (!preLaunchProcessIds.Contains(windowProcessId))
        {
            return true;
        }

        return launchedProcess.HasExited;
    }

    private static HashSet<int> CaptureProcessIdsForApp(string? appPath)
    {
        var processName = Path.GetFileNameWithoutExtension(appPath);
        var processIds = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return processIds;
        }

        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                processIds.Add(process.Id);
            }
        }

        return processIds;
    }

    private static bool IsAliAppProcess(int processId, string appPath)
    {
        var expectedProcessName = Path.GetFileNameWithoutExtension(appPath);
        if (string.IsNullOrWhiteSpace(expectedProcessName))
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName.Equals(expectedProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static AutomationElement FindRequired(AutomationElement root, string automationId) =>
        FindOptional(root, automationId)
        ?? throw new InvalidOperationException($"Could not find automation element '{automationId}'.");

    private static AutomationElement? FindOptional(AutomationElement root, string automationId)
    {
        try
        {
            return root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, automationId));
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FindTextValues(AutomationElement root, string automationId)
    {
        try
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
        catch (COMException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
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

    private static void HoldPushToTalk(
        AutomationElement window,
        int holdMilliseconds,
        TimeSpan timeout,
        List<ChatAutomationStep> steps)
    {
        var button = FindRequired(window, "MainChatPushToTalkButton");
        WaitForEnabled(button, timeout);
        var rectangle = button.Current.BoundingRectangle;
        if (rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            throw new InvalidOperationException("Push-to-talk button bounds are not available.");
        }

        Invoke(button, timeout);
        Thread.Sleep(Math.Min(500, Math.Max(150, holdMilliseconds / 3)));
        steps.Add(new ChatAutomationStep("ptt-held", CaptureChatSnapshot(window)));
        Thread.Sleep(Math.Max(0, holdMilliseconds - 500));
        Invoke(button, timeout);
        WaitForVoiceNotRecording(window, timeout);
        steps.Add(new ChatAutomationStep("ptt-released", CaptureChatSnapshot(window)));
    }

    private static AutomationElement VerifyVoiceSample(
        AutomationElement window,
        string engineName,
        TimeSpan timeout,
        List<ChatAutomationStep> steps)
    {
        var settingsWindow = OpenSettingsWindow(window, timeout);
        SelectElement(FindRequired(settingsWindow, "SettingsVoiceMicTab"), timeout);
        SelectComboBoxItem(FindRequired(settingsWindow, "SettingsTtsEngineComboBox"), engineName, timeout);
        steps.Add(new ChatAutomationStep("voice-sample-engine-selected", CaptureChatSnapshot(window, settingsWindow)));

        var initialStatus = ReadOptionalName(settingsWindow, "SettingsTtsStatusText");
        Invoke(FindRequired(settingsWindow, "SettingsHearVoiceSampleButton"), timeout);
        WaitForVoiceSampleTerminalStatus(settingsWindow, initialStatus, timeout);
        steps.Add(new ChatAutomationStep("voice-sample-complete", CaptureChatSnapshot(window, settingsWindow)));
        return settingsWindow;
    }

    private static AutomationElement VerifyInternetSettings(
        AutomationElement window,
        bool testConfiguredProviders,
        TimeSpan timeout,
        List<ChatAutomationStep> steps)
    {
        var settingsWindow = OpenSettingsWindow(window, timeout);
        SelectElement(FindRequired(settingsWindow, "SettingsInternetTab"), timeout);
        steps.Add(new ChatAutomationStep("internet-settings-opened", CaptureChatSnapshot(window, settingsWindow)));

        if (testConfiguredProviders)
        {
            var initialStatus = ReadOptionalName(settingsWindow, "SettingsInternetBackendStatusText");
            Invoke(FindRequired(settingsWindow, "SettingsInternetTestConfiguredButton"), timeout);
            WaitForInternetProviderTestIdle(settingsWindow, initialStatus, timeout);
            steps.Add(new ChatAutomationStep("internet-test-configured-complete", CaptureChatSnapshot(window, settingsWindow)));
        }

        return settingsWindow;
    }

    private static AutomationElement OpenSettingsWindow(AutomationElement window, TimeSpan timeout)
    {
        Invoke(FindRequired(window, "MainChatSettingsButton"), timeout);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            foreach (var topLevelWindow in FindTopLevelWindows())
            {
                if (FindOptional(topLevelWindow, "SettingsVoiceMicTab") is not null)
                {
                    return topLevelWindow;
                }
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for Ali Settings window.");
    }

    private static void SelectElement(AutomationElement element, TimeSpan timeout)
    {
        WaitForEnabled(element, timeout);
        if (element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selectionItemPattern))
        {
            ((SelectionItemPattern)selectionItemPattern).Select();
            return;
        }

        Invoke(element, timeout);
    }

    private static void SelectComboBoxItem(AutomationElement comboBox, string itemName, TimeSpan timeout)
    {
        WaitForEnabled(comboBox, timeout);
        if (ReadSelectedComboBoxItem(comboBox).Equals(itemName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (comboBox.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expandPattern))
        {
            ((ExpandCollapsePattern)expandPattern).Expand();
        }

        var item = FindComboBoxItem(comboBox, itemName) ?? FindComboBoxItem(AutomationElement.RootElement, itemName);
        if (item is null)
        {
            throw new InvalidOperationException($"Could not find combo box item '{itemName}'.");
        }

        SelectElement(item, timeout);
        if (comboBox.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out expandPattern))
        {
            ((ExpandCollapsePattern)expandPattern).Collapse();
        }

        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (ReadSelectedComboBoxItem(comboBox).Equals(itemName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(150);
        }

        throw new TimeoutException($"Timed out waiting for combo box selection '{itemName}'.");
    }

    private static AutomationElement? FindComboBoxItem(AutomationElement root, string itemName)
    {
        try
        {
            var items = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
            return items
                .Cast<AutomationElement>()
                .FirstOrDefault(item => ReadName(item).Equals(itemName, StringComparison.OrdinalIgnoreCase));
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReadSelectedComboBoxItem(AutomationElement comboBox)
    {
        try
        {
            if (comboBox.TryGetCurrentPattern(SelectionPattern.Pattern, out var selectionPattern))
            {
                return ((SelectionPattern)selectionPattern).Current.GetSelection()
                    .Select(ReadName)
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text))
                    ?? string.Empty;
            }
        }
        catch (COMException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        return ReadName(comboBox);
    }

    private static void WaitForVoiceSampleTerminalStatus(
        AutomationElement settingsWindow,
        string initialStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var sawActiveStatus = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadOptionalName(settingsWindow, "SettingsTtsStatusText");
            if (status.Contains("Testing", StringComparison.OrdinalIgnoreCase)
                || !status.Equals(initialStatus, StringComparison.Ordinal))
            {
                sawActiveStatus = true;
            }

            if (status.Contains("Voice sample failed", StringComparison.OrdinalIgnoreCase)
                || status.Contains("not configured", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(status);
            }

            if (status.Contains("Voice sample complete", StringComparison.OrdinalIgnoreCase)
                && (sawActiveStatus || !status.Equals(initialStatus, StringComparison.Ordinal)))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for voice sample playback to complete.");
    }

    private static void WaitForInternetProviderTestIdle(
        AutomationElement settingsWindow,
        string initialStatus,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var sawChange = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadOptionalName(settingsWindow, "SettingsInternetBackendStatusText");
            if (!status.Equals(initialStatus, StringComparison.Ordinal))
            {
                sawChange = true;
            }

            if (sawChange && !status.Contains("Testing", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(300);
        }

        throw new TimeoutException("Timed out waiting for configured internet provider tests to complete.");
    }

    private static void WaitForEnabled(AutomationElement element, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (element.Current.IsEnabled)
            {
                return;
            }

            Thread.Sleep(200);
        }

        throw new TimeoutException($"Timed out waiting for '{element.Current.AutomationId}' to become enabled.");
    }

    private static void WaitForVoiceNotRecording(AutomationElement window, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var voiceStatus = ReadOptionalName(window, "MainChatVoiceStatusText");
            if (!voiceStatus.Contains("Recording from", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new TimeoutException("Timed out waiting for push-to-talk recording to stop.");
    }

    private static void WaitForChatIdle(AutomationElement window, TimeSpan timeout, int minimumMessageCount)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = ReadOptionalName(window, "MainChatStatusText");
            var messageCount = CountChatMessages(window);
            if (messageCount >= minimumMessageCount
                && (status.Contains("Response complete", StringComparison.OrdinalIgnoreCase)
                    || !status.Contains("Streaming", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            Thread.Sleep(300);
        }

        throw new TimeoutException("Timed out waiting for main chat automation to become idle.");
    }

    private static void WaitForChatLiveRuntime(AutomationElement window, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        ChatAutomationSnapshot lastSnapshot;
        do
        {
            lastSnapshot = CaptureChatSnapshot(window);
            if (IsLiveRuntime(lastSnapshot.ActiveRuntimeStatus, lastSnapshot.ModelConnectionStatus))
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
            + $"Connection='{lastSnapshot.ModelConnectionStatus}', Status='{lastSnapshot.Status}'.");
    }

    private static bool IsLiveRuntime(string activeRuntimeStatus, string modelConnectionStatus) =>
        !activeRuntimeStatus.Contains("deterministic stub", StringComparison.OrdinalIgnoreCase)
        && modelConnectionStatus.Equals("connected to model", StringComparison.OrdinalIgnoreCase);

    private static int CountChatMessages(AutomationElement window) =>
        FindTextValues(window, "MainChatMessageText").Count;

    private static ChatAutomationSnapshot CaptureChatSnapshot(AutomationElement window, AutomationElement? settingsWindow = null)
    {
        var messages = FindTextValues(window, "MainChatMessageText");
        return new ChatAutomationSnapshot(
            ReadOptionalName(window, "MainChatStatusText"),
            ReadOptionalName(window, "MainChatModelConnectionStatusText"),
            ReadOptionalName(window, "MainChatVoiceStatusText"),
            ReadOptionalName(window, "MainChatPushToTalkButton"),
            settingsWindow is null ? Array.Empty<string>() : FindTabNames(settingsWindow),
            settingsWindow is null ? false : IsLastTab(settingsWindow, "Internet"),
            settingsWindow is null ? string.Empty : ReadOptionalSelectedComboBoxItem(settingsWindow, "SettingsTtsEngineComboBox"),
            settingsWindow is null ? string.Empty : ReadOptionalSelectedComboBoxItem(settingsWindow, "SettingsTtsVoiceComboBox"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsTtsStatusText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsVoiceSettingsStatusText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsInternetBackendStatusText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsInternetTavilyUsageText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsInternetFirecrawlUsageText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsInternetBraveSearchUsageText"),
            settingsWindow is null ? string.Empty : ReadOptionalName(settingsWindow, "SettingsInternetSerperUsageText"),
            messages,
            messages.LastOrDefault() ?? string.Empty,
            FindRequired(window, "MainChatSendButton").Current.IsEnabled,
            FindRequired(window, "MainChatPushToTalkButton").Current.IsEnabled);
    }

    private static string ReadOptionalSelectedComboBoxItem(AutomationElement root, string automationId)
    {
        var element = FindOptional(root, automationId);
        return element is null ? string.Empty : ReadSelectedComboBoxItem(element);
    }

    private static IReadOnlyList<string> FindTabNames(AutomationElement root)
    {
        try
        {
            var items = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
            return items
                .Cast<AutomationElement>()
                .Select(element => new
                {
                    Name = ReadName(element),
                    Bounds = element.Current.BoundingRectangle
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Name))
                .OrderBy(item => item.Bounds.Top <= 0 ? double.MaxValue : item.Bounds.Top)
                .ThenBy(item => item.Bounds.Left <= 0 ? double.MaxValue : item.Bounds.Left)
                .Select(item => item.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
        }
        catch (COMException)
        {
            return Array.Empty<string>();
        }
        catch (InvalidOperationException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsLastTab(AutomationElement root, string tabName)
    {
        var tabs = FindTabNames(root);
        return tabs.Count > 0 && tabs[^1].Equals(tabName, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadOptionalName(AutomationElement root, string automationId)
    {
        var element = FindOptional(root, automationId);
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

        try
        {
            return element.Current.Name ?? string.Empty;
        }
        catch (COMException)
        {
            return string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
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
            throw new InvalidOperationException("Window bounds are not available for screenshot capture.");
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

    private static void ShutdownIfRequested(AutomationOptions options, Process? launchedProcess)
    {
        if (options.ShutdownLaunchedApp && launchedProcess is not null && !launchedProcess.HasExited)
        {
            launchedProcess.Kill(entireProcessTree: true);
        }
    }
}

internal sealed record ChatAutomationSnapshot(
    string Status,
    string ModelConnectionStatus,
    string VoiceStatus,
    string PushToTalkButtonText,
    IReadOnlyList<string> SettingsTabNames,
    bool InternetTabIsLast,
    string TextToSpeechEngine,
    string TextToSpeechVoice,
    string TextToSpeechStatus,
    string VoiceSettingsStatus,
    string InternetBackendStatus,
    string InternetTavilyUsage,
    string InternetFirecrawlUsage,
    string InternetBraveSearchUsage,
    string InternetSerperUsage,
    IReadOnlyList<string> Messages,
    string LastMessage,
    bool SendEnabled,
    bool PushToTalkEnabled)
{
    public string ActiveRuntimeStatus => ModelConnectionStatus;
}

internal sealed record ChatAutomationStep(
    string Name,
    ChatAutomationSnapshot Snapshot);

internal sealed record ChatAutomationRun(
    ChatAutomationSnapshot Final,
    IReadOnlyList<ChatAutomationStep> Steps,
    string ScreenshotPath,
    string Error);

internal sealed record AutomationOptions(
    string Mode,
    string? AppPath,
    IReadOnlyList<string> SendTexts,
    TimeSpan Timeout,
    bool RequireLiveRuntime,
    string? ScreenshotPath,
    string? LocalRoot,
    int PushToTalkHoldMilliseconds,
    string? VoiceSampleEngine,
    bool CheckInternetSettings,
    bool TestConfiguredInternetBackends,
    bool ShutdownLaunchedApp,
    bool ShowHelp)
{
    public const string HelpText = """
    Usage:
      Ali.Modules.Automation chat --app <Ali.exe> --send "what happened today?" [--local-root <isolated-root>] [--require-live-runtime] [--screenshot <path.png>] [--timeout-ms 120000] [--shutdown-launched-app]
      Ali.Modules.Automation chat --send "..." [--screenshot <path.png>]
      Ali.Modules.Automation chat --app <Ali.exe> --ptt-hold-ms 2500 [--screenshot <path.png>]
      Ali.Modules.Automation chat --app <Ali.exe> --voice-sample-engine KittenTTS [--screenshot <path.png>]
      Ali.Modules.Automation chat --app <Ali.exe> --check-internet-settings [--test-configured-internet] [--screenshot <path.png>]

    If --app is supplied, the runner launches Ali.
    If --app is omitted, the runner attaches to an existing matching Ali window.
    --send may be supplied more than once; messages are sent in order in the same window.
    --local-root sets ALI_LOCAL_ROOT for a launched Ali process so validation can use isolated app data.
    --ptt-hold-ms holds Ali's visible push-to-talk button for the requested duration and reports voice status.
    --voice-sample-engine opens Settings, selects the requested TTS engine, clicks Hear Sample, and reports voice status.
    --check-internet-settings opens Settings, selects Internet, and reports provider status and tab order.
    --test-configured-internet clicks the Internet tab's Test configured button and waits for completion.
    If --require-live-runtime is supplied, the runner waits until Ali reports an active non-stub runtime before entering text.
    """;

    public static AutomationOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args.Any(arg => arg is "-h" or "--help" or "/?"))
        {
            return new AutomationOptions("help", null, Array.Empty<string>(), TimeSpan.FromSeconds(90), false, null, null, 0, null, false, false, false, true);
        }

        var mode = args[0];
        string? appPath = null;
        var sendTexts = new List<string>();
        var timeout = TimeSpan.FromSeconds(90);
        var requireLiveRuntime = false;
        string? screenshotPath = null;
        string? localRoot = null;
        var pushToTalkHoldMilliseconds = 0;
        string? voiceSampleEngine = null;
        var checkInternetSettings = false;
        var testConfiguredInternet = false;
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
            else if (arg.Equals("--send", StringComparison.OrdinalIgnoreCase))
            {
                sendTexts.Add(ReadValue());
            }
            else if (arg.Equals("--timeout-ms", StringComparison.OrdinalIgnoreCase))
            {
                timeout = TimeSpan.FromMilliseconds(int.Parse(ReadValue(), System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (arg.Equals("--require-live-runtime", StringComparison.OrdinalIgnoreCase))
            {
                requireLiveRuntime = true;
            }
            else if (arg.Equals("--screenshot", StringComparison.OrdinalIgnoreCase))
            {
                screenshotPath = ReadValue();
            }
            else if (arg.Equals("--local-root", StringComparison.OrdinalIgnoreCase))
            {
                localRoot = ReadValue();
            }
            else if (arg.Equals("--ptt-hold-ms", StringComparison.OrdinalIgnoreCase))
            {
                pushToTalkHoldMilliseconds = int.Parse(ReadValue(), System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (arg.Equals("--voice-sample-engine", StringComparison.OrdinalIgnoreCase))
            {
                voiceSampleEngine = ReadValue();
            }
            else if (arg.Equals("--check-internet-settings", StringComparison.OrdinalIgnoreCase))
            {
                checkInternetSettings = true;
            }
            else if (arg.Equals("--test-configured-internet", StringComparison.OrdinalIgnoreCase))
            {
                checkInternetSettings = true;
                testConfiguredInternet = true;
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

        return new AutomationOptions(mode, appPath, sendTexts, timeout, requireLiveRuntime, screenshotPath, localRoot, pushToTalkHoldMilliseconds, voiceSampleEngine, checkInternetSettings, testConfiguredInternet, shutdownLaunchedApp, false);
    }
}
