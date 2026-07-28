using System.Collections.ObjectModel;
using System.Windows.Input;
using Ali.Modules.Mcp;

namespace Ali.UI.ViewModels;

public sealed class McpSettingsViewModel : ObservableObject
{
    private readonly McpClientManager _manager;
    private McpServerProfileViewModel? _selectedServer;
    private bool _enabled;
    private string _statusText = "MCP client settings have not been loaded yet.";

    public McpSettingsViewModel(McpClientManager manager)
    {
        _manager = manager;
        AddServerCommand = new RelayCommand(_ => AddServer());
        RemoveServerCommand = new RelayCommand(
            _ => RemoveSelectedServer(),
            _ => SelectedServer is not null);
        SaveCommand = new RelayCommand(_ => Save(), onException: HandleError);
        ReloadCommand = new RelayCommand(_ => Reload(), onException: HandleError);
        TestAndDiscoverCommand = new AsyncRelayCommand(
            TestAndDiscoverAsync,
            () => SelectedServer is not null,
            HandleError);
        Reload();
    }

    public ObservableCollection<McpServerProfileViewModel> Servers { get; } = [];

    public IReadOnlyList<string> TransportChoices => McpTransportKinds.All;

    public string SettingsPath => _manager.SettingsPath;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public McpServerProfileViewModel? SelectedServer
    {
        get => _selectedServer;
        set
        {
            if (SetProperty(ref _selectedServer, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public ICommand AddServerCommand { get; }

    public ICommand RemoveServerCommand { get; }

    public ICommand SaveCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand TestAndDiscoverCommand { get; }

    public void Reload()
    {
        var settings = _manager.LoadSettings();
        Enabled = settings.Enabled;
        Servers.Clear();
        foreach (var server in settings.Servers)
        {
            Servers.Add(new McpServerProfileViewModel(server));
        }

        SelectedServer = Servers.FirstOrDefault();
        StatusText = Servers.Count == 0
            ? "MCP is ready. Add a server to begin; nothing is enabled by default."
            : $"Loaded {Servers.Count} MCP server profile(s).";
    }

    private void AddServer()
    {
        var server = new McpServerProfileViewModel(new McpServerProfile());
        Servers.Add(server);
        SelectedServer = server;
        StatusText = "New MCP server added locally. Configure it, test discovery, then save when ready.";
    }

    private void RemoveSelectedServer()
    {
        if (SelectedServer is null)
        {
            return;
        }

        var removedName = SelectedServer.Name;
        var index = Servers.IndexOf(SelectedServer);
        Servers.Remove(SelectedServer);
        SelectedServer = Servers.Count == 0
            ? null
            : Servers[Math.Clamp(index, 0, Servers.Count - 1)];
        StatusText = $"Removed {removedName} from the draft. Choose Save to make the removal permanent.";
    }

    private void Save()
    {
        var settings = new McpClientSettings
        {
            Enabled = Enabled,
            Servers = Servers.Select(server => server.ToModel()).ToList()
        };
        _manager.SaveSettings(settings);
        StatusText = Enabled
            ? $"Saved {Servers.Count} MCP server profile(s). Enabled tools become available on Ali's next turn."
            : $"Saved {Servers.Count} MCP server profile(s). MCP remains globally disabled.";
    }

    private async Task TestAndDiscoverAsync()
    {
        if (SelectedServer is null)
        {
            return;
        }

        StatusText = $"Connecting to {SelectedServer.Name} and discovering tools...";
        var result = await _manager.ProbeAsync(SelectedServer.ToModel()).ConfigureAwait(true);
        StatusText = result.Status;
        if (!result.Succeeded)
        {
            return;
        }

        SelectedServer.MergeDiscoveredTools(result.Tools);
        Save();
        StatusText = result.Status
            + " New tools are disabled and require approval until you explicitly change them.";
    }

    private void RaiseCommandStates()
    {
        if (RemoveServerCommand is RelayCommand remove)
        {
            remove.RaiseCanExecuteChanged();
        }

        if (TestAndDiscoverCommand is AsyncRelayCommand test)
        {
            test.RaiseCanExecuteChanged();
        }
    }

    private void HandleError(Exception ex) =>
        StatusText = $"MCP settings failed safely: {ex.Message.ReplaceLineEndings(" ").Trim()}";
}

public sealed class McpServerProfileViewModel : ObservableObject
{
    private readonly string _id;
    private string _name;
    private bool _enabled;
    private string _transport;
    private string _endpoint;
    private string _command;
    private string _argumentsText;
    private string _workingDirectory;
    private bool _inheritEnvironmentVariables;
    private string _environmentVariableBindingsText;
    private string _authenticationHeaderName;
    private string _authenticationPrefix;
    private string _authenticationEnvironmentVariable;
    private string _connectionTimeoutText;

    public McpServerProfileViewModel(McpServerProfile profile)
    {
        _id = profile.Id;
        _name = profile.Name;
        _enabled = profile.Enabled;
        _transport = McpTransportKinds.Normalize(profile.Transport);
        _endpoint = profile.Endpoint;
        _command = profile.Command;
        _argumentsText = string.Join(Environment.NewLine, profile.Arguments);
        _workingDirectory = profile.WorkingDirectory;
        _inheritEnvironmentVariables = profile.InheritEnvironmentVariables;
        _environmentVariableBindingsText = string.Join(
            Environment.NewLine,
            profile.EnvironmentVariables.Select(binding =>
                $"{binding.Name}={binding.SourceEnvironmentVariable}"));
        _authenticationHeaderName = profile.AuthenticationHeaderName;
        _authenticationPrefix = profile.AuthenticationPrefix;
        _authenticationEnvironmentVariable = profile.AuthenticationEnvironmentVariable;
        _connectionTimeoutText = profile.ConnectionTimeoutSeconds.ToString();
        foreach (var tool in profile.Tools.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            Tools.Add(new McpToolPolicyViewModel(tool));
        }
    }

    public ObservableCollection<McpToolPolicyViewModel> Tools { get; } = [];

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Transport
    {
        get => _transport;
        set
        {
            if (SetProperty(ref _transport, McpTransportKinds.Normalize(value)))
            {
                OnPropertyChanged(nameof(IsHttp));
                OnPropertyChanged(nameof(IsStdio));
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    public bool IsHttp => Transport == McpTransportKinds.Http;

    public bool IsStdio => Transport == McpTransportKinds.Stdio;

    public string Endpoint
    {
        get => _endpoint;
        set
        {
            if (SetProperty(ref _endpoint, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    public string Command
    {
        get => _command;
        set
        {
            if (SetProperty(ref _command, value))
            {
                OnPropertyChanged(nameof(ConnectionSummary));
            }
        }
    }

    public string ArgumentsText
    {
        get => _argumentsText;
        set => SetProperty(ref _argumentsText, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool InheritEnvironmentVariables
    {
        get => _inheritEnvironmentVariables;
        set => SetProperty(ref _inheritEnvironmentVariables, value);
    }

    public string EnvironmentVariableBindingsText
    {
        get => _environmentVariableBindingsText;
        set => SetProperty(ref _environmentVariableBindingsText, value);
    }

    public string AuthenticationHeaderName
    {
        get => _authenticationHeaderName;
        set => SetProperty(ref _authenticationHeaderName, value);
    }

    public string AuthenticationPrefix
    {
        get => _authenticationPrefix;
        set => SetProperty(ref _authenticationPrefix, value);
    }

    public string AuthenticationEnvironmentVariable
    {
        get => _authenticationEnvironmentVariable;
        set => SetProperty(ref _authenticationEnvironmentVariable, value);
    }

    public string ConnectionTimeoutText
    {
        get => _connectionTimeoutText;
        set => SetProperty(ref _connectionTimeoutText, value);
    }

    public string ConnectionSummary => IsHttp
        ? (string.IsNullOrWhiteSpace(Endpoint) ? "HTTP endpoint not configured" : Endpoint)
        : (string.IsNullOrWhiteSpace(Command) ? "stdio command not configured" : Command);

    public McpServerProfile ToModel() => new()
    {
        Id = _id,
        Name = Name,
        Enabled = Enabled,
        Transport = Transport,
        Endpoint = Endpoint,
        Command = Command,
        Arguments = SplitLines(ArgumentsText),
        WorkingDirectory = WorkingDirectory,
        InheritEnvironmentVariables = InheritEnvironmentVariables,
        EnvironmentVariables = ParseEnvironmentBindings(EnvironmentVariableBindingsText),
        AuthenticationHeaderName = AuthenticationHeaderName,
        AuthenticationPrefix = AuthenticationPrefix,
        AuthenticationEnvironmentVariable = AuthenticationEnvironmentVariable,
        ConnectionTimeoutSeconds = int.TryParse(ConnectionTimeoutText, out var timeout) ? timeout : 30,
        Tools = Tools.Select(tool => tool.ToModel()).ToList()
    };

    public void MergeDiscoveredTools(IReadOnlyList<McpDiscoveredTool> discovered)
    {
        var existing = Tools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        Tools.Clear();
        foreach (var tool in discovered.OrderBy(tool => tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (existing.TryGetValue(tool.Name, out var saved))
            {
                saved.UpdateDescription(tool.Description, tool.ReadOnlyHint, tool.DestructiveHint);
                Tools.Add(saved);
            }
            else
            {
                Tools.Add(new McpToolPolicyViewModel(new McpToolPolicy
                {
                    Name = tool.Name,
                    Description = tool.Description,
                    Enabled = false,
                    RequiresApproval = true,
                    ReadOnlyHint = tool.ReadOnlyHint,
                    DestructiveHint = tool.DestructiveHint
                }));
            }
        }
    }

    private static List<string> SplitLines(string value) =>
        (value ?? string.Empty)
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !string.IsNullOrWhiteSpace(line))
        .ToList();

    private static List<McpEnvironmentVariableBinding> ParseEnvironmentBindings(string value)
    {
        var bindings = new List<McpEnvironmentVariableBinding>();
        foreach (var line in SplitLines(value))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                continue;
            }

            bindings.Add(new McpEnvironmentVariableBinding
            {
                Name = line[..separator].Trim(),
                SourceEnvironmentVariable = line[(separator + 1)..].Trim()
            });
        }

        return bindings;
    }
}

public sealed class McpToolPolicyViewModel : ObservableObject
{
    private string _description;
    private bool _enabled;
    private bool _requiresApproval;
    private bool _readOnlyHint;
    private bool _destructiveHint;

    public McpToolPolicyViewModel(McpToolPolicy tool)
    {
        Name = tool.Name;
        _description = tool.Description;
        _enabled = tool.Enabled;
        _requiresApproval = tool.RequiresApproval;
        _readOnlyHint = tool.ReadOnlyHint;
        _destructiveHint = tool.DestructiveHint;
    }

    public string Name { get; }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public bool RequiresApproval
    {
        get => _requiresApproval;
        set => SetProperty(ref _requiresApproval, value);
    }

    public bool ReadOnlyHint
    {
        get => _readOnlyHint;
        private set => SetProperty(ref _readOnlyHint, value);
    }

    public bool DestructiveHint
    {
        get => _destructiveHint;
        private set => SetProperty(ref _destructiveHint, value);
    }

    public string SafetySummary => DestructiveHint
        ? "Server marks this tool as potentially destructive."
        : ReadOnlyHint
            ? "Server describes this tool as read-only."
            : "Server did not provide a reliable safety classification.";

    public McpToolPolicy ToModel() => new()
    {
        Name = Name,
        Description = Description,
        Enabled = Enabled,
        RequiresApproval = RequiresApproval,
        ReadOnlyHint = ReadOnlyHint,
        DestructiveHint = DestructiveHint
    };

    public void UpdateDescription(string description, bool readOnlyHint, bool destructiveHint)
    {
        Description = description;
        ReadOnlyHint = readOnlyHint;
        DestructiveHint = destructiveHint;
        OnPropertyChanged(nameof(SafetySummary));
    }
}
