using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Ali.UI.ViewModels;

namespace Ali.UI;

public partial class SettingsWindow : Window
{
    private bool syncingGeminiApiKey;

    public SettingsWindow()
    {
        NativeTitleBarTheme.ApplyDarkTitleBar(this);
        InitializeComponent();
        MoveRuntimeTabToEnd();
        MoveInternetTabToEnd();
        PreviewKeyDown += SettingsWindow_OnPreviewKeyDown;
        Loaded += SettingsWindowLoaded;
    }

    private void MoveRuntimeTabToEnd()
    {
        if (!SettingsTabs.Items.Contains(RuntimeTab))
        {
            return;
        }

        SettingsTabs.Items.Remove(RuntimeTab);
        SettingsTabs.Items.Add(RuntimeTab);
        SettingsTabs.SelectedIndex = 0;
    }

    private void MoveInternetTabToEnd()
    {
        if (!SettingsTabs.Items.Contains(InternetTab))
        {
            return;
        }

        SettingsTabs.Items.Remove(InternetTab);
        SettingsTabs.Items.Add(InternetTab);
        SettingsTabs.SelectedIndex = 0;
    }

#if false // Removed legacy in-process webcam path; shared CameraModule now owns this feature.
    private void ConfigureWebcamTab()
    {
        WebcamSourceComboBox.ItemsSource = _webcamDevices;
        WebcamModeComboBox.ItemsSource = _webcamModes;
        ResetWebcamModes();
        UpdateWebcamControlState();
    }

    private async void SettingsWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_webcamInitialized)
        {
            return;
        }

        _webcamInitialized = true;
        await RefreshWebcamDevicesAsync();
    }

    private void SettingsWindow_OnClosed(object? sender, EventArgs e)
    {
        _webcamModeLoad?.Cancel();
        _webcamModeLoad?.Dispose();
        _webcamModeLoad = null;
        StopWebcamPreview("Camera off.");
    }

    private async void RefreshWebcamClicked(object sender, RoutedEventArgs e)
    {
        await RefreshWebcamDevicesAsync();
    }

    private async void WebcamSourceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webcamChangingSelection)
        {
            return;
        }

        StopWebcamPreview();
        await LoadModesForSelectedWebcamAsync();
    }

    private void WebcamModeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_webcamChangingSelection || _webcamCamera is null)
        {
            return;
        }

        StartWebcamPreview();
    }

    private void ToggleWebcamClicked(object sender, RoutedEventArgs e)
    {
        if (_webcamCamera is null)
        {
            StartWebcamPreview();
            return;
        }

        StopWebcamPreview("Camera off.");
    }

    private async Task RefreshWebcamDevicesAsync()
    {
        if (_webcamLoading)
        {
            return;
        }

        _webcamLoading = true;
        UpdateWebcamControlState();
        StopWebcamPreview("Looking for cameras...");
        _webcamModeLoad?.Cancel();

        try
        {
            var cameras = await Task.Run(() => WebcamModule.GetCameras());

            _webcamChangingSelection = true;
            _webcamDevices.Clear();
            foreach (var camera in cameras)
            {
                _webcamDevices.Add(camera);
            }

            WebcamSourceComboBox.SelectedItem = _webcamDevices.FirstOrDefault();
            _webcamChangingSelection = false;

            if (_webcamDevices.Count == 0)
            {
                ResetWebcamModes();
                SetWebcamStatus("No cameras found.");
                return;
            }

            SetWebcamStatus($"Selected {WebcamSourceComboBox.SelectedItem}.");
            await LoadModesForSelectedWebcamAsync();
        }
        catch (Exception ex)
        {
            _webcamChangingSelection = false;
            ResetWebcamModes();
            SetWebcamStatus($"Could not list cameras: {FormatWebcamError(ex)}");
        }
        finally
        {
            _webcamChangingSelection = false;
            _webcamLoading = false;
            UpdateWebcamControlState();
        }
    }

    private async Task LoadModesForSelectedWebcamAsync()
    {
        var camera = WebcamSourceComboBox.SelectedItem as CameraDevice;
        ResetWebcamModes();
        if (camera is null)
        {
            SetWebcamStatus("No camera selected.");
            UpdateWebcamControlState();
            return;
        }

        _webcamModeLoad?.Cancel();
        _webcamModeLoad?.Dispose();
        var modeLoad = new CancellationTokenSource();
        _webcamModeLoad = modeLoad;
        _webcamLoading = true;
        UpdateWebcamControlState();
        SetWebcamStatus($"Loading modes for {camera}...");

        try
        {
            var modes = await WebcamModule.CreateModeService().GetModesAsync(camera, modeLoad.Token);
            if (modeLoad.IsCancellationRequested)
            {
                return;
            }

            _webcamChangingSelection = true;
            _webcamModes.Clear();
            foreach (var mode in EnsureAutoMode(modes))
            {
                _webcamModes.Add(mode);
            }

            WebcamModeComboBox.SelectedItem = _webcamModes.FirstOrDefault() ?? CameraVideoMode.Auto;
            _webcamChangingSelection = false;
            SetWebcamStatus($"{camera} ready.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _webcamChangingSelection = false;
            ResetWebcamModes();
            SetWebcamStatus($"Could not load camera modes: {FormatWebcamError(ex)}");
        }
        finally
        {
            if (ReferenceEquals(_webcamModeLoad, modeLoad))
            {
                _webcamModeLoad = null;
            }

            modeLoad.Dispose();
            _webcamChangingSelection = false;
            _webcamLoading = false;
            UpdateWebcamControlState();
        }
    }

    private void StartWebcamPreview()
    {
        var camera = WebcamSourceComboBox.SelectedItem as CameraDevice;
        if (camera is null)
        {
            SetWebcamStatus("Choose a camera source first.");
            return;
        }

        var mode = WebcamModeComboBox.SelectedItem as CameraVideoMode ?? CameraVideoMode.Auto;
        _webcamLoading = true;
        UpdateWebcamControlState();
        StopWebcamPreview($"Starting {camera}...");

        try
        {
            var target = new Dx12Camera.PreviewTarget(
                WebcamPreviewPanel,
                placeholder: WebcamPreviewPlaceholder,
                statusText: WebcamStatusText,
                hostInsertIndex: 0,
                name: camera.Name);
            var options = new Dx12CameraOptions
            {
                Camera = camera,
                Mode = mode,
                StatusChanged = (_, status) => SetWebcamStatus(status)
            };

            _webcamCamera = Dx12Camera.Start(target, options);
            WebcamPreviewPlaceholder.Visibility = Visibility.Collapsed;
            WebcamToggleButton.Content = "Camera Off";
            SetWebcamStatus($"Camera on: {camera} ({mode}).");
        }
        catch (Exception ex)
        {
            _webcamCamera = null;
            WebcamPreviewPlaceholder.Visibility = Visibility.Visible;
            WebcamToggleButton.Content = "Camera On";
            SetWebcamStatus($"Camera failed to start: {FormatWebcamError(ex)}");
        }
        finally
        {
            _webcamLoading = false;
            UpdateWebcamControlState();
        }
    }

    private void StopWebcamPreview(string status = "Camera off.")
    {
        var camera = _webcamCamera;
        _webcamCamera = null;
        try
        {
            camera?.Dispose();
        }
        catch
        {
        }

        WebcamPreviewPlaceholder.Visibility = Visibility.Visible;
        WebcamToggleButton.Content = "Camera On";
        SetWebcamStatus(status);
        UpdateWebcamControlState();
    }

    private void ResetWebcamModes()
    {
        _webcamChangingSelection = true;
        _webcamModes.Clear();
        _webcamModes.Add(CameraVideoMode.Auto);
        WebcamModeComboBox.SelectedItem = CameraVideoMode.Auto;
        _webcamChangingSelection = false;
    }

    private void UpdateWebcamControlState()
    {
        var hasCamera = _webcamDevices.Count > 0 && WebcamSourceComboBox.SelectedItem is CameraDevice;
        WebcamSourceComboBox.IsEnabled = !_webcamLoading && _webcamDevices.Count > 0;
        WebcamModeComboBox.IsEnabled = !_webcamLoading && hasCamera;
        WebcamRefreshButton.IsEnabled = !_webcamLoading;
        WebcamToggleButton.IsEnabled = !_webcamLoading && hasCamera;
    }

    private void SetWebcamStatus(string status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => SetWebcamStatus(status));
            return;
        }

        WebcamStatusText.Text = status;
    }

    private static IEnumerable<CameraVideoMode> EnsureAutoMode(IReadOnlyList<CameraVideoMode> modes)
    {
        if (!modes.Any(mode => mode.IsAuto))
        {
            yield return CameraVideoMode.Auto;
        }

        foreach (var mode in modes)
        {
            yield return mode;
        }
    }

    private static string FormatWebcamError(Exception ex)
    {
        return ex.Message
            .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
            .Trim();
    }

#endif

    private void RefreshWebcamClicked(object sender, RoutedEventArgs e) { }
    private void WebcamSourceSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void WebcamModeSelectionChanged(object sender, SelectionChangedEventArgs e) { }
    private void ToggleWebcamClicked(object sender, RoutedEventArgs e) { }

    private void SettingsWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel || !viewModel.IsAssigningPushToTalkKey)
        {
            return;
        }

        e.Handled = true;
        viewModel.AssignPushToTalkKey(e.Key);
    }

    private void SettingsWindowLoaded(object sender, RoutedEventArgs e)
    {
        SyncGeminiApiKeyFromViewModel();
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RefreshGeminiUsageStatus();
        }
    }

    private void RefreshGeminiUsageClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.RefreshGeminiUsageStatus();
        }
    }

    private void GeminiApiKeyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (syncingGeminiApiKey || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        viewModel.InternetGeminiApiKeyText = GeminiApiKeyPasswordBox.Password;
    }

    private void GoogleBillingProtectionClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (!viewModel.IsGoogleBillingProtectionConfigured)
        {
            var password = OwnerPasswordDialog.Prompt(
                this,
                "Protect Google billing",
                "Create an owner password. It stays on this computer and is never stored in plain text or copied into the release folder.",
                confirm: true);
            if (password is not null)
            {
                try
                {
                    viewModel.SetGoogleBillingOwnerPassword(password);
                    SyncGeminiApiKeyFromViewModel();
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(this, ex.Message, "Google billing protection", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            return;
        }

        if (viewModel.IsGoogleBillingSettingsUnlocked)
        {
            viewModel.LockGoogleBillingSettings();
            SyncGeminiApiKeyFromViewModel();
            return;
        }

        var entered = OwnerPasswordDialog.Prompt(
            this,
            "Unlock Google billing",
            "Enter the owner password to edit the API key or spending limits.");
        if (entered is not null && viewModel.TryUnlockGoogleBillingSettings(entered))
        {
            SyncGeminiApiKeyFromViewModel();
        }
    }

    private void ChangeGoogleBillingPasswordClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || !viewModel.IsGoogleBillingProtectionConfigured
            || !viewModel.IsGoogleBillingSettingsUnlocked)
        {
            return;
        }

        var current = OwnerPasswordDialog.Prompt(
            this,
            "Verify owner password",
            "Enter the current owner password.");
        if (current is null) return;
        var replacement = OwnerPasswordDialog.Prompt(
            this,
            "Change owner password",
            "Enter the new owner password (at least eight characters).",
            confirm: true);
        if (replacement is null) return;
        try
        {
            viewModel.ChangeGoogleBillingOwnerPassword(current, replacement);
            SyncGeminiApiKeyFromViewModel();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Google billing protection", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SyncGeminiApiKeyFromViewModel()
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        syncingGeminiApiKey = true;
        try
        {
            GeminiApiKeyPasswordBox.Password = viewModel.InternetGeminiApiKeyText;
        }
        finally
        {
            syncingGeminiApiKey = false;
        }
    }
}
