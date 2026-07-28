using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Ali.Modules.Mcp;

namespace Ali.UI.ViewModels;

public sealed class McpServerSettingsViewModel : ObservableObject
{
    private readonly McpServerHost _host;
    private readonly McpClientManager _clientManager;
    private bool _enabled;
    private string _portText = "8771";
    private string _path = "/mcp";
    private bool _stateless;
    private bool _requireAuthentication = true;
    private string _authenticationEnvironmentVariable = "ALI_MCP_SERVER_TOKEN";
    private string _runtimeState = "Disabled";
    private string _statusText = "The MCP server is off.";
    private string _endpoint = string.Empty;
    private bool _isRunning;

    public McpServerSettingsViewModel(McpServerHost host, McpClientManager clientManager)
    {
        _host = host;
        _clientManager = clientManager;
        SaveAndApplyCommand = new AsyncRelayCommand(SaveAndApplyAsync, onException: HandleError);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning, HandleError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsRunning, HandleError);
        RestartCommand = new AsyncRelayCommand(RestartAsync, () => IsRunning, HandleError);
        TestCommand = new AsyncRelayCommand(TestAsync, () => IsRunning, HandleError);
        ReloadCommand = new RelayCommand(_ => Reload(), onException: HandleError);
        _host.StatusChanged += HostOnStatusChanged;
        Reload();
    }

    public ObservableCollection<McpServerToolPolicyViewModel> Tools { get; } = [];

    public string SettingsPath => _host.SettingsPath;

    public string Host => "127.0.0.1";

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

    public string Path
    {
        get => _path;
        set
        {
            if (SetProperty(ref _path, value))
            {
                RefreshEndpointPreview();
            }
        }
    }

    public bool Stateless
    {
        get => _stateless;
        set => SetProperty(ref _stateless, value);
    }

    public bool RequireAuthentication
    {
        get => _requireAuthentication;
        set => SetProperty(ref _requireAuthentication, value);
    }

    public string AuthenticationEnvironmentVariable
    {
        get => _authenticationEnvironmentVariable;
        set => SetProperty(ref _authenticationEnvironmentVariable, value);
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

    public ICommand SaveAndApplyCommand { get; }

    public ICommand StartCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand RestartCommand { get; }

    public ICommand TestCommand { get; }

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
        Path = settings.Path;
        Stateless = settings.Stateless;
        RequireAuthentication = settings.RequireAuthentication;
        AuthenticationEnvironmentVariable = settings.AuthenticationEnvironmentVariable;
        Tools.Clear();
        foreach (var policy in settings.Tools)
        {
            Tools.Add(new McpServerToolPolicyViewModel(policy));
        }

        ApplyStatus(_host.Status);
        StatusText = settings.Enabled
            ? StatusText
            : "The server and every exported capability are off by default. Save applies the master switch.";
    }

    private async Task SaveAndApplyAsync()
    {
        var saved = _host.SaveSettings(BuildSettings());
        Endpoint = saved.Endpoint;
        if (!saved.Enabled)
        {
            await _host.StopAsync().ConfigureAwait(true);
            StatusText = "MCP server settings saved. The server is off.";
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

        StatusText = "MCP server settings saved and applied.";
    }

    private async Task StartAsync()
    {
        _host.SaveSettings(BuildSettings());
        await _host.StartAsync().ConfigureAwait(true);
    }

    private async Task StopAsync() => await _host.StopAsync().ConfigureAwait(true);

    private async Task RestartAsync()
    {
        _host.SaveSettings(BuildSettings());
        await _host.RestartAsync().ConfigureAwait(true);
    }

    private async Task TestAsync()
    {
        var settings = BuildSettings().Normalize();
        var probe = await _clientManager.ProbeAsync(new McpServerProfile
        {
            Id = "ali-local-server-test",
            Name = "Ali local MCP server",
            Enabled = true,
            Transport = McpTransportKinds.Http,
            Endpoint = settings.Endpoint,
            AuthenticationHeaderName = settings.RequireAuthentication ? "Authorization" : string.Empty,
            AuthenticationPrefix = settings.RequireAuthentication ? "Bearer " : string.Empty,
            AuthenticationEnvironmentVariable = settings.RequireAuthentication
                ? settings.AuthenticationEnvironmentVariable
                : string.Empty,
            ConnectionTimeoutSeconds = 10
        }).ConfigureAwait(true);

        StatusText = probe.Succeeded
            ? $"Protocol test passed. A fresh MCP client discovered {probe.Tools.Count} tool(s): "
                + string.Join(", ", probe.Tools.Select(tool => tool.Name))
            : $"Protocol test failed: {probe.Status}";
    }

    private McpServerSettings BuildSettings()
    {
        if (!int.TryParse(PortText, out var port))
        {
            throw new InvalidOperationException("Enter a numeric MCP server port between 1024 and 65535.");
        }

        return new McpServerSettings
        {
            Enabled = Enabled,
            Host = Host,
            Port = port,
            Path = Path,
            Stateless = Stateless,
            RequireAuthentication = RequireAuthentication,
            AuthenticationEnvironmentVariable = AuthenticationEnvironmentVariable,
            Tools = Tools.Select(tool => tool.ToModel()).ToArray()
        };
    }

    private void HostOnStatusChanged(object? sender, McpServerRuntimeStatus status)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => ApplyStatus(status)));
            return;
        }

        ApplyStatus(status);
    }

    private void ApplyStatus(McpServerRuntimeStatus status)
    {
        IsRunning = status.IsRunning;
        RuntimeState = status.State;
        StatusText = status.Message;
        Endpoint = status.Endpoint;
    }

    private void RefreshEndpointPreview()
    {
        if (int.TryParse(PortText, out var port))
        {
            Endpoint = $"http://127.0.0.1:{port}{McpServerSettings.NormalizePath(Path)}";
        }
    }

    private void HandleError(Exception exception)
    {
        RuntimeState = "Error";
        StatusText = exception.Message;
        ApplyStatus(_host.Status);
        StatusText = exception.Message;
    }

    private void RaiseCommandStates()
    {
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (RestartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (TestCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
}

public sealed class McpServerToolPolicyViewModel : ObservableObject
{
    private bool _enabled;

    public McpServerToolPolicyViewModel(McpServerToolPolicy policy)
    {
        Name = policy.Name;
        Description = policy.Description;
        _enabled = policy.Enabled;
        WritesLocalData = policy.WritesLocalData;
        UsesNetwork = policy.UsesNetwork;
        ReadsPrivateData = policy.ReadsPrivateData;
    }

    public string Name { get; }

    public string Description { get; }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool WritesLocalData { get; }

    public bool UsesNetwork { get; }

    public bool ReadsPrivateData { get; }

    public string SafetySummary
    {
        get
        {
            var traits = new List<string>();
            if (WritesLocalData)
            {
                traits.Add("writes local data");
            }

            if (ReadsPrivateData)
            {
                traits.Add("can access private local data");
            }

            if (UsesNetwork)
            {
                traits.Add("uses the internet");
            }

            return traits.Count == 0 ? "Read-only local utility." : string.Join("; ", traits) + ".";
        }
    }

    public McpServerToolPolicy ToModel() => new()
    {
        Name = Name,
        Description = Description,
        Enabled = Enabled,
        WritesLocalData = WritesLocalData,
        UsesNetwork = UsesNetwork,
        ReadsPrivateData = ReadsPrivateData
    };
}
