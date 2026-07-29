using System.Windows.Input;
using Ali.Modules.ConversationBridge;

namespace Ali.UI.ViewModels;

public sealed class ConversationBridgeSettingsViewModel : ObservableObject
{
    private readonly ConversationBridgeHost _host;
    private bool _enabled;
    private string _portText = "8772";
    private string _authenticationToken = string.Empty;
    private bool _isRunning;
    private string _runtimeState = "Disabled";
    private string _statusText = "The live conversation bridge is off.";
    private string _endpoint = "http://127.0.0.1:8772";

    public ConversationBridgeSettingsViewModel(ConversationBridgeHost host)
    {
        _host = host;
        SaveAndApplyCommand = new AsyncRelayCommand(SaveAndApplyAsync, onException: HandleError);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, HandleError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, HandleError);
        RegenerateTokenCommand = new RelayCommand(_ => RegenerateToken(), onException: HandleError);
        ReloadCommand = new RelayCommand(_ => Reload(), onException: HandleError);
        _host.StatusChanged += HostOnStatusChanged;
        Reload();
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                RefreshEndpointPreview();
            }
        }
    }

    public string AuthenticationToken
    {
        get => _authenticationToken;
        private set => SetProperty(ref _authenticationToken, value);
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string RuntimeState
    {
        get => _runtimeState;
        private set => SetProperty(ref _runtimeState, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string Endpoint
    {
        get => _endpoint;
        private set => SetProperty(ref _endpoint, value);
    }

    public string SettingsPath => _host.SettingsPath;

    public ICommand SaveAndApplyCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RegenerateTokenCommand { get; }

    public ICommand ReloadCommand { get; }

    public async Task StartIfEnabledAsync()
    {
        try
        {
            await _host.StartIfEnabledAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            HandleError(ex);
        }
    }

    public void Reload()
    {
        var settings = _host.LoadSettings();
        Enabled = settings.Enabled;
        PortText = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AuthenticationToken = settings.AuthenticationToken;
        Endpoint = settings.Endpoint;
        ApplyStatus(_host.Status);
    }

    private async Task SaveAndApplyAsync()
    {
        var settings = _host.SaveSettings(BuildSettings());
        AuthenticationToken = settings.AuthenticationToken;
        Endpoint = settings.Endpoint;
        if (!settings.Enabled)
        {
            await _host.StopAsync().ConfigureAwait(true);
            return;
        }

        if (_host.IsRunning)
        {
            await _host.RestartAsync().ConfigureAwait(true);
        }
        else
        {
            await _host.StartAsync().ConfigureAwait(true);
        }
    }

    private async Task StartAsync()
    {
        Enabled = true;
        _host.SaveSettings(BuildSettings());
        await _host.StartAsync().ConfigureAwait(true);
    }

    private async Task StopAsync()
    {
        Enabled = false;
        _host.SaveSettings(BuildSettings());
        await _host.StopAsync().ConfigureAwait(true);
    }

    private void RegenerateToken()
    {
        AuthenticationToken = new ConversationBridgeSettings().AuthenticationToken;
        StatusText = "Generated a new token. Select Save and apply before using it.";
    }

    private ConversationBridgeSettings BuildSettings()
    {
        if (!int.TryParse(PortText, out var port))
        {
            throw new InvalidOperationException("Enter a numeric bridge port between 1024 and 65535.");
        }

        return new ConversationBridgeSettings
        {
            Enabled = Enabled,
            Port = port,
            AuthenticationToken = AuthenticationToken
        }.Normalize();
    }

    private void HostOnStatusChanged(object? sender, ConversationBridgeRuntimeStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ApplyStatus(status)));
            return;
        }

        ApplyStatus(status);
    }

    private void ApplyStatus(ConversationBridgeRuntimeStatus status)
    {
        IsRunning = status.IsRunning;
        RuntimeState = status.State;
        StatusText = status.Message;
        Endpoint = status.Endpoint;
    }

    private void RefreshEndpointPreview()
    {
        if (int.TryParse(PortText, out var port) && port is >= 1024 and <= 65535)
        {
            Endpoint = $"http://127.0.0.1:{port}";
        }
    }

    private void HandleError(Exception ex)
    {
        RuntimeState = "Failed";
        StatusText = $"Conversation bridge failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}";
    }

    private void RaiseCommandStates()
    {
        if (StartCommand is AsyncRelayCommand start)
        {
            start.RaiseCanExecuteChanged();
        }

        if (StopCommand is AsyncRelayCommand stop)
        {
            stop.RaiseCanExecuteChanged();
        }
    }
}
