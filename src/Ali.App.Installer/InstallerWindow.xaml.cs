using System.IO;
using System.Windows;
using System.Windows.Threading;
using Ali.Infrastructure.Installation;

namespace Ali.App.Installer;

public partial class InstallerWindow : Window
{
    private readonly AliDesktopInstallReadinessService _readinessService = new();
    private readonly DispatcherTimer _readinessRefreshTimer;
    private readonly string[] _stepLabels =
    [
        "Mode",
        "Assistant",
        "Dependencies",
        "Visual Studio",
        "Shortcuts",
        "Review",
        "Finish"
    ];

    private InstallerStep _currentStep = InstallerStep.Mode;
    private bool _isInitialized;
    private bool _isBusy;

    public int ExitCode { get; private set; }

    public InstallerWindow()
    {
        InitializeComponent();

        StepListBox.ItemsSource = _stepLabels;
        var defaults = AliDesktopInstallOptions.CreateDefault();
        InstallRootTextBox.Text = defaults.LocalAliRoot;
        RuntimeModelTextBox.Text = defaults.RuntimeModel;
        VisionModelTextBox.Text = defaults.VisionModel;
        InstallModeRadio.IsChecked = true;
        VoiceResourcesCheckBox.IsChecked = true;

        _readinessRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _readinessRefreshTimer.Tick += async (_, _) =>
        {
            _readinessRefreshTimer.Stop();
            await RefreshReadinessAsync().ConfigureAwait(true);
        };

        _isInitialized = true;
        UpdateComponentState();
        ShowStep(InstallerStep.Mode);
    }

    private async void InstallButtonClick(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        LogTextBox.Clear();
        FinishLogTextBox.Clear();
        AppendLog(UninstallModeRadio.IsChecked == true ? "Starting Ali uninstall." : "Starting Ali setup.");

        try
        {
            if (UninstallModeRadio.IsChecked == true)
            {
                var uninstaller = new AliDesktopUninstaller();
                var uninstallResult = await uninstaller.UninstallAsync(BuildUninstallOptions()).ConfigureAwait(true);

                ExitCode = uninstallResult.Succeeded ? 0 : 1;
                StatusTextBlock.Text = uninstallResult.Message;
                AppendLog(uninstallResult.Message);
                AppendLog($"Local root: {uninstallResult.LocalAliRoot}");
                AppendLog($"Receipt: {uninstallResult.ReceiptPath}");
                AppendLog($"Removed paths: {uninstallResult.RemovedPaths.Count}");
                AppendLog($"Preserved paths: {uninstallResult.PreservedPaths.Count}");

                foreach (var warning in uninstallResult.Warnings)
                {
                    AppendLog($"Warning: {warning}");
                }
            }
            else
            {
                var installer = new AliDesktopInstaller();
                var result = await installer.InstallAsync(BuildOptions()).ConfigureAwait(true);

                ExitCode = result.Succeeded ? 0 : 1;
                StatusTextBlock.Text = result.Message;
                AppendLog(result.Message);
                AppendLog($"Target: {result.TargetDirectory}");
                AppendLog($"Receipt: {result.ReceiptPath}");
                AppendLog($"Installed files: {result.InstalledFiles.Count}");

                foreach (var dependency in result.DependencyMessages)
                {
                    AppendLog($"Dependency: {dependency}");
                }

                foreach (var warning in result.Warnings)
                {
                    AppendLog($"Warning: {warning}");
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ExitCode = 1;
            StatusTextBlock.Text = $"Install failed: {ex.Message}";
            AppendLog(StatusTextBlock.Text);
        }
        finally
        {
            SetBusy(false);
            FinishLogTextBox.Text = LogTextBox.Text;
            FinishLogTextBox.ScrollToEnd();
            ShowStep(InstallerStep.Finish);
        }
    }

    private AliDesktopInstallOptions BuildOptions()
    {
        var installApplication = VsixOnlyModeRadio.IsChecked != true && UninstallModeRadio.IsChecked != true;
        var repair = installApplication && RepairModeRadio.IsChecked == true;
        var installVsix = VisualStudioExtensionCheckBox.IsChecked == true || VsixOnlyModeRadio.IsChecked == true;

        return AliDesktopInstallOptions.CreateDefault() with
        {
            LocalAliRoot = InstallRootTextBox.Text,
            AssistantName = installApplication ? NullIfWhiteSpace(AssistantNameTextBox.Text) : null,
            RepairExistingInstall = repair,
            InstallOllamaIfMissing = installApplication && InstallOllamaCheckBox.IsChecked == true,
            PullRuntimeModel = installApplication && RuntimeModelCheckBox.IsChecked == true,
            RuntimeModel = RuntimeModelTextBox.Text,
            PullVisionModel = installApplication && VisionModelCheckBox.IsChecked == true,
            VisionModel = VisionModelTextBox.Text,
            InstallVoiceResources = installApplication && VoiceResourcesCheckBox.IsChecked == true,
            VoiceResourcesPath = installApplication ? NullIfWhiteSpace(VoiceResourcesPathTextBox.Text) : null,
            LaunchAfterInstall = installApplication && LaunchAfterInstallCheckBox.IsChecked == true,
            InstallApplication = installApplication,
            InstallVisualStudioExtension = installVsix,
            CreateDesktopShortcut = installApplication && DesktopShortcutCheckBox.IsChecked == true,
            CreateStartMenuShortcut = installApplication && StartMenuShortcutCheckBox.IsChecked == true
        };
    }

    private AliDesktopUninstallOptions BuildUninstallOptions() =>
        new(InstallRootTextBox.Text, RemoveUserDataCheckBox.IsChecked == true);

    private void CloseButtonClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void BackButtonClick(object sender, RoutedEventArgs e)
    {
        if (UninstallModeRadio.IsChecked == true && _currentStep == InstallerStep.Review)
        {
            ShowStep(InstallerStep.Assistant);
            return;
        }

        if (_currentStep > InstallerStep.Mode)
        {
            ShowStep(_currentStep - 1);
        }
    }

    private async void NextButtonClick(object sender, RoutedEventArgs e)
    {
        if (UninstallModeRadio.IsChecked == true)
        {
            var nextStep = _currentStep == InstallerStep.Mode
                ? InstallerStep.Assistant
                : InstallerStep.Review;
            ShowStep(nextStep);
            if (_currentStep == InstallerStep.Review)
            {
                await RefreshReadinessAsync().ConfigureAwait(true);
            }

            return;
        }

        if (_currentStep < InstallerStep.Review)
        {
            ShowStep(_currentStep + 1);
            if (_currentStep == InstallerStep.Review)
            {
                await RefreshReadinessAsync().ConfigureAwait(true);
            }
        }
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (VsixOnlyModeRadio.IsChecked == true)
        {
            VisualStudioExtensionCheckBox.IsChecked = true;
        }

        UpdateComponentState();
        QueueReadinessRefresh();
    }

    private void ModelCheckBoxChanged(object sender, RoutedEventArgs e)
    {
        UpdateComponentState();
        QueueReadinessRefresh();
    }

    private void VoiceResourcesCheckBoxChanged(object sender, RoutedEventArgs e)
    {
        UpdateComponentState();
        QueueReadinessRefresh();
    }

    private void InstallerOptionChanged(object sender, RoutedEventArgs e)
    {
        QueueReadinessRefresh();
    }

    private async void RefreshReadinessButtonClick(object sender, RoutedEventArgs e)
    {
        await RefreshReadinessAsync().ConfigureAwait(true);
    }

    private void QueueReadinessRefresh()
    {
        if (!_isInitialized)
        {
            return;
        }

        _readinessRefreshTimer.Stop();
        _readinessRefreshTimer.Start();
    }

    private async Task RefreshReadinessAsync()
    {
        if (!_isInitialized)
        {
            return;
        }

        RefreshReadinessButton.IsEnabled = false;
        try
        {
            if (UninstallModeRadio.IsChecked == true)
            {
                ReadinessListBox.ItemsSource = BuildUninstallReadinessItems();
                StatusTextBlock.Text = "Ready to uninstall selected Ali components.";
                return;
            }

            var readiness = await _readinessService.EvaluateAsync(BuildOptions()).ConfigureAwait(true);
            ReadinessListBox.ItemsSource = readiness.Items.Select(ReadinessDisplayItem.FromReadinessItem).ToList();
            StatusTextBlock.Text = readiness.IsReadyForSelectedActions
                ? "Ready for selected actions."
                : "Some selected actions need attention.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            ReadinessListBox.ItemsSource = new[]
            {
                new ReadinessDisplayItem("Missing - Readiness", $"Readiness check failed: {ex.Message}")
            };
            StatusTextBlock.Text = "Readiness check failed.";
        }
        finally
        {
            RefreshReadinessButton.IsEnabled = !_isBusy;
        }
    }

    private void ShowStep(InstallerStep step)
    {
        _currentStep = step;
        StepListBox.SelectedIndex = (int)step;
        ModeStepPanel.Visibility = step == InstallerStep.Mode ? Visibility.Visible : Visibility.Collapsed;
        AssistantStepPanel.Visibility = step == InstallerStep.Assistant ? Visibility.Visible : Visibility.Collapsed;
        DependenciesStepPanel.Visibility = step == InstallerStep.Dependencies ? Visibility.Visible : Visibility.Collapsed;
        VisualStudioStepPanel.Visibility = step == InstallerStep.VisualStudio ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsStepPanel.Visibility = step == InstallerStep.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
        ReviewStepPanel.Visibility = step == InstallerStep.Review ? Visibility.Visible : Visibility.Collapsed;
        FinishStepPanel.Visibility = step == InstallerStep.Finish ? Visibility.Visible : Visibility.Collapsed;

        StepProgressTextBlock.Text = $"Step {(int)step + 1} of {_stepLabels.Length}";
        StepDescriptionTextBlock.Text = step switch
        {
            InstallerStep.Mode => "Choose a normal install, repair pass, or Visual Studio-only install.",
            InstallerStep.Assistant => UninstallModeRadio.IsChecked == true
                ? "Confirm which Ali root should be uninstalled."
                : "Choose where Ali installs and optionally seed the assistant name.",
            InstallerStep.Dependencies => "Select Ollama, model, and local voice resource actions.",
            InstallerStep.VisualStudio => "Choose whether to install the optional Visual Studio Companion extension.",
            InstallerStep.Shortcuts => "Choose launch and shortcut options.",
            InstallerStep.Review => UninstallModeRadio.IsChecked == true
                ? "Review uninstall choices before setup changes anything."
                : "Review readiness before setup changes anything.",
            _ => "Setup finished."
        };

        BackButton.IsEnabled = !_isBusy && step > InstallerStep.Mode && step < InstallerStep.Finish;
        NextButton.Visibility = step < InstallerStep.Review ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.Visibility = step == InstallerStep.Review ? Visibility.Visible : Visibility.Collapsed;
        InstallButton.IsEnabled = !_isBusy;
        InstallButton.Content = UninstallModeRadio.IsChecked == true ? "Uninstall" : "Install";
        CloseButton.Content = step == InstallerStep.Finish ? "Close" : "Cancel";
    }

    private void UpdateComponentState()
    {
        var uninstall = UninstallModeRadio.IsChecked == true;
        var installApplication = VsixOnlyModeRadio.IsChecked != true && !uninstall;
        var vsixOnly = VsixOnlyModeRadio.IsChecked == true;

        AssistantNameTextBox.IsEnabled = installApplication;
        InstallOllamaCheckBox.IsEnabled = installApplication;
        RuntimeModelCheckBox.IsEnabled = installApplication;
        RuntimeModelTextBox.IsEnabled = installApplication && RuntimeModelCheckBox.IsChecked == true;
        VisionModelCheckBox.IsEnabled = installApplication;
        VisionModelTextBox.IsEnabled = installApplication && VisionModelCheckBox.IsChecked == true;
        VoiceResourcesCheckBox.IsEnabled = installApplication;
        VoiceResourcesPathTextBox.IsEnabled = installApplication && VoiceResourcesCheckBox.IsChecked == true;
        LaunchAfterInstallCheckBox.IsEnabled = installApplication;
        DesktopShortcutCheckBox.IsEnabled = installApplication;
        StartMenuShortcutCheckBox.IsEnabled = installApplication;
        VisualStudioExtensionCheckBox.IsChecked = vsixOnly ? true : VisualStudioExtensionCheckBox.IsChecked;
        VisualStudioExtensionCheckBox.IsEnabled = !vsixOnly && !uninstall;
        RemoveUserDataCheckBox.IsEnabled = uninstall;
        if (!uninstall)
        {
            RemoveUserDataCheckBox.IsChecked = false;
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        BackButton.IsEnabled = !busy && _currentStep > InstallerStep.Mode && _currentStep < InstallerStep.Finish;
        NextButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        CloseButton.IsEnabled = !busy;
        RefreshReadinessButton.IsEnabled = !busy;
        InstallProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText(message + Environment.NewLine);
        LogTextBox.ScrollToEnd();
    }

    private static string? NullIfWhiteSpace(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private IReadOnlyList<ReadinessDisplayItem> BuildUninstallReadinessItems()
    {
        var options = BuildUninstallOptions();
        var localRoot = string.IsNullOrWhiteSpace(options.LocalAliRoot)
            ? AliDesktopUninstallOptions.CreateDefault().LocalAliRoot
            : Path.GetFullPath(options.LocalAliRoot);
        var devRun = Path.Combine(localRoot, "DevRun");
        if (AliDesktopUninstaller.IsUnsafeLocalRoot(localRoot))
        {
            return
            [
                new(
                    "Missing - Ali root",
                    $"Setup will refuse to uninstall from this unsafe root: {localRoot}")
            ];
        }

        var items = new List<ReadinessDisplayItem>
        {
            new(
                Directory.Exists(devRun) ? "Ready - Ali app" : "Warning - Ali app",
                Directory.Exists(devRun)
                    ? $"DevRun app folder will be removed: {devRun}"
                    : $"No DevRun app folder was found at {devRun}."),
            new(
                options.RemoveUserData ? "Warning - User data" : "Ready - User data",
                options.RemoveUserData
                    ? $"User data under {localRoot} will be removed."
                    : $"User data under {localRoot} will be preserved.")
        };

        return items;
    }

    private enum InstallerStep
    {
        Mode,
        Assistant,
        Dependencies,
        VisualStudio,
        Shortcuts,
        Review,
        Finish
    }

    private sealed record ReadinessDisplayItem(string Summary, string Message)
    {
        public static ReadinessDisplayItem FromReadinessItem(AliInstallReadinessItem item) =>
            new($"{item.Status} - {item.Name}", item.Message);
    }
}
