using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Forms = System.Windows.Forms;

namespace Ali.UI.ViewModels;

public sealed class LocalKnowledgeSettingsViewModel : ObservableObject
{
    private readonly AliServices _services;
    private bool _enabled;
    private bool _managed;
    private bool _autoStart;
    private bool _useTls;
    private bool _enableRipgrep;
    private string _rootDirectory = string.Empty;
    private string _host = "127.0.0.1";
    private string _httpPort = "6333";
    private string _grpcPort = "6334";
    private string _collection = "ali_local_library";
    private string _apiKeyEnvironmentVariable = "ALI_QDRANT_API_KEY";
    private string _embeddingProvider = LocalVectorLibrarySettings.DefaultEmbeddingProvider;
    private string _embeddingEndpoint = LocalVectorLibrarySettings.DefaultEmbeddingEndpoint;
    private string _embeddingModel = LocalVectorLibrarySettings.DefaultEmbeddingModel;
    private string _embeddingDimensions = LocalVectorLibrarySettings.DefaultEmbeddingDimensions.ToString(CultureInfo.InvariantCulture);
    private string _scanInterval = "10";
    private string _maxResults = "4";
    private string _runtimeState = "Not checked";
    private string _statistics = "No collection status yet.";
    private string _statusText = "Local knowledge settings have not been loaded.";
    private bool _isBusy;

    public LocalKnowledgeSettingsViewModel(AliServices services)
    {
        _services = services;
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy, HandleError);
        TestCommand = new AsyncRelayCommand(TestAsync, () => !IsBusy, HandleError);
        TestEmbeddingCommand = new AsyncRelayCommand(TestEmbeddingAsync, () => !IsBusy, HandleEmbeddingError);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsBusy, HandleError);
        StopCommand = new AsyncRelayCommand(StopAsync, () => !IsBusy, HandleError);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsBusy, HandleError);
        RebuildCommand = new AsyncRelayCommand(RebuildAsync, () => !IsBusy, HandleError);
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy, HandleError);
        ChooseFolderCommand = new RelayCommand(_ => ChooseFolder(), onException: HandleError);
        OpenFolderCommand = new RelayCommand(_ => OpenFolder(), onException: HandleError);
        OpenDashboardCommand = new RelayCommand(_ => OpenDashboard(), onException: HandleError);
        _services.Qdrant.StatusChanged += QdrantOnStatusChanged;
        Reload();
    }

    public string SettingsPath => _services.LocalVectorLibrarySettingsPath;
    public string QdrantDataPath => _services.LocalVectorLibraryDataPath;
    public bool Enabled { get => _enabled; set => SetProperty(ref _enabled, value); }
    public bool UseManagedLocalQdrant { get => _managed; set => SetProperty(ref _managed, value); }
    public bool AutoStartQdrant { get => _autoStart; set => SetProperty(ref _autoStart, value); }
    public bool QdrantUseTls { get => _useTls; set => SetProperty(ref _useTls, value); }
    public bool EnableRipgrep { get => _enableRipgrep; set => SetProperty(ref _enableRipgrep, value); }
    public string RootDirectory { get => _rootDirectory; set => SetProperty(ref _rootDirectory, value); }
    public string QdrantHost { get => _host; set => SetProperty(ref _host, value); }
    public string QdrantHttpPort { get => _httpPort; set => SetProperty(ref _httpPort, value); }
    public string QdrantGrpcPort { get => _grpcPort; set => SetProperty(ref _grpcPort, value); }
    public string QdrantCollectionName { get => _collection; set => SetProperty(ref _collection, value); }
    public string QdrantApiKeyEnvironmentVariable { get => _apiKeyEnvironmentVariable; set => SetProperty(ref _apiKeyEnvironmentVariable, value); }
    public IReadOnlyList<string> EmbeddingProviderChoices => LocalEmbeddingProviders.Choices;
    public string EmbeddingProvider
    {
        get => _embeddingProvider;
        set
        {
            if (!SetProperty(ref _embeddingProvider, value)
                || !LocalEmbeddingProviders.TryGetPreset(value, out var preset))
            {
                return;
            }

            EmbeddingEndpoint = preset.Endpoint;
            EmbeddingModel = preset.Model;
            EmbeddingDimensions = preset.Dimensions.ToString(CultureInfo.InvariantCulture);
        }
    }
    public string EmbeddingEndpoint { get => _embeddingEndpoint; set => SetProperty(ref _embeddingEndpoint, value); }
    public string EmbeddingModel { get => _embeddingModel; set => SetProperty(ref _embeddingModel, value); }
    public string EmbeddingDimensions { get => _embeddingDimensions; set => SetProperty(ref _embeddingDimensions, value); }
    public string ScanIntervalMinutes { get => _scanInterval; set => SetProperty(ref _scanInterval, value); }
    public string MaxRetrievedChunks { get => _maxResults; set => SetProperty(ref _maxResults, value); }
    public string RuntimeState { get => _runtimeState; private set => SetProperty(ref _runtimeState, value); }
    public string Statistics { get => _statistics; private set => SetProperty(ref _statistics, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandStates(); } }
    public string DashboardEndpoint => $"{(QdrantUseTls ? "https" : "http")}://{QdrantHost}:{QdrantHttpPort}/dashboard";

    public ICommand SaveCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand TestEmbeddingCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ScanCommand { get; }
    public ICommand RebuildCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand ChooseFolderCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenDashboardCommand { get; }

    public void Reload()
    {
        var settings = _services.LoadLocalVectorLibrarySettings();
        Enabled = settings.Enabled;
        UseManagedLocalQdrant = settings.UseManagedLocalQdrant;
        AutoStartQdrant = settings.AutoStartQdrant;
        QdrantUseTls = settings.QdrantUseTls;
        EnableRipgrep = settings.EnableRipgrep;
        RootDirectory = settings.RootDirectory;
        QdrantHost = settings.QdrantHost;
        QdrantHttpPort = settings.QdrantHttpPort.ToString();
        QdrantGrpcPort = settings.QdrantGrpcPort.ToString();
        QdrantCollectionName = settings.QdrantCollectionName;
        QdrantApiKeyEnvironmentVariable = settings.QdrantApiKeyEnvironmentVariable;
        EmbeddingProvider = settings.EmbeddingProvider;
        EmbeddingEndpoint = settings.EmbeddingEndpoint;
        EmbeddingModel = settings.EmbeddingModel;
        EmbeddingDimensions = settings.EmbeddingDimensions.ToString(CultureInfo.InvariantCulture);
        ScanIntervalMinutes = settings.ScanIntervalMinutes.ToString();
        MaxRetrievedChunks = settings.MaxRetrievedChunks.ToString();
        ApplyRuntimeStatus(_services.Qdrant.Status);
        StatusText = "Settings loaded. Mem0, semantic tool retrieval, and local knowledge share this embedding provider.";
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            _services.SaveLocalVectorLibrarySettings(settings);
            Directory.CreateDirectory(settings.RootDirectory);
            StatusText = $"Local knowledge settings saved to {SettingsPath}.";
            if (settings.Enabled && settings.UseManagedLocalQdrant && settings.AutoStartQdrant)
            {
                await _services.Qdrant.EnsureAvailableAsync(settings).ConfigureAwait(true);
            }
        }
        finally { IsBusy = false; }
    }

    private async Task TestAsync()
    {
        IsBusy = true;
        try
        {
            var status = await _services.Qdrant.ProbeAsync(BuildSettings()).ConfigureAwait(true);
            ApplyRuntimeStatus(status);
            StatusText = status.Message;
            if (status.IsReachable) await RefreshStatisticsAsync().ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    private async Task TestEmbeddingAsync()
    {
        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            var configuration = RequireSharedEmbeddingConfiguration(settings);
            using var httpClient = LocalOnlyHttpClientFactory.Create(
                "AliEmbeddingSettings/1.0",
                TimeSpan.FromSeconds(20));
            var result = await new OpenAiCompatibleEmbeddingClient(httpClient)
                .CreateEmbeddingAsync(
                    configuration,
                    "Ali local embedding connectivity test.")
                .ConfigureAwait(true);
            StatusText = result.Success
                ? $"{settings.EmbeddingProvider} returned the configured {settings.EmbeddingDimensions}-dimension embedding from {settings.EmbeddingModel}."
                : result.Message;
        }
        finally { IsBusy = false; }
    }

    private async Task StartAsync()
    {
        IsBusy = true;
        try
        {
            var settings = BuildSettings();
            _services.SaveLocalVectorLibrarySettings(settings);
            ApplyRuntimeStatus(await _services.Qdrant.StartAsync(settings).ConfigureAwait(true));
            await RefreshStatisticsAsync().ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    private async Task StopAsync()
    {
        IsBusy = true;
        try { ApplyRuntimeStatus(await _services.Qdrant.StopAsync().ConfigureAwait(true)); }
        finally { IsBusy = false; }
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        try
        {
            _services.SaveLocalVectorLibrarySettings(BuildSettings());
            StatusText = "Scanning the approved folder with Tree-sitter and text fallbacks...";
            var status = await _services.CreateLocalVectorLibraryRetriever().ScanAsync(force: true).ConfigureAwait(true);
            ApplyKnowledgeStatus(status);
        }
        finally { IsBusy = false; }
    }

    private async Task RebuildAsync()
    {
        if (System.Windows.MessageBox.Show(
                "Rebuild local knowledge? This deletes the Qdrant collection and re-indexes the approved folder. Source files are never deleted.",
                "Rebuild local knowledge",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        IsBusy = true;
        try
        {
            _services.SaveLocalVectorLibrarySettings(BuildSettings());
            StatusText = "Rebuilding the Qdrant collection...";
            await _services.CreateLocalVectorLibraryRetriever().RebuildAsync().ConfigureAwait(true);
            await RefreshStatisticsAsync().ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        try { await RefreshStatisticsAsync().ConfigureAwait(true); }
        finally { IsBusy = false; }
    }

    private async Task RefreshStatisticsAsync()
    {
        var status = await _services.CreateLocalVectorLibraryRetriever().GetStatusAsync().ConfigureAwait(true);
        ApplyKnowledgeStatus(status);
    }

    private void ApplyKnowledgeStatus(LocalKnowledgeStatus status)
    {
        RuntimeState = status.ServerReachable ? "Healthy" : "Unavailable";
        Statistics = $"Documents: {status.DocumentCount}    Chunks: {status.ChunkCount}    Collection: {(status.CollectionExists ? QdrantCollectionName : "not created")}    Last scan: {(status.LastScanUtc == DateTimeOffset.MinValue ? "never" : status.LastScanUtc.ToLocalTime().ToString("g"))}";
        StatusText = status.Message;
    }

    private LocalVectorLibrarySettings BuildSettings()
    {
        if (!int.TryParse(QdrantHttpPort, out var httpPort) || httpPort is < 1024 or > 65535
            || !int.TryParse(QdrantGrpcPort, out var grpcPort) || grpcPort is < 1024 or > 65535)
            throw new InvalidOperationException("Qdrant HTTP and gRPC ports must be numbers from 1024 through 65535.");
        if (!int.TryParse(ScanIntervalMinutes, out var scanMinutes) || scanMinutes is < 1 or > 1440)
            throw new InvalidOperationException("Scan interval must be from 1 through 1440 minutes.");
        if (!int.TryParse(MaxRetrievedChunks, out var maxResults) || maxResults is < 1 or > 20)
            throw new InvalidOperationException("Retrieved chunks must be from 1 through 20.");
        if (!int.TryParse(EmbeddingDimensions, NumberStyles.None, CultureInfo.InvariantCulture, out var embeddingDimensions))
            throw new InvalidOperationException("Embedding dimensions must be a whole number.");
        var collection = QdrantCollectionName.Trim();
        if (collection.Length == 0 || collection.Any(character => !(char.IsLetterOrDigit(character) || character is '_' or '-')))
            throw new InvalidOperationException("The Qdrant collection name may contain letters, numbers, underscores, and hyphens.");
        var root = Path.GetFullPath(RootDirectory.Trim());
        var settings = _services.LoadLocalVectorLibrarySettings() with
        {
            Enabled = Enabled,
            UseManagedLocalQdrant = UseManagedLocalQdrant,
            AutoStartQdrant = AutoStartQdrant,
            QdrantUseTls = QdrantUseTls,
            EnableRipgrep = EnableRipgrep,
            RootDirectory = root,
            QdrantHost = QdrantHost.Trim(),
            QdrantHttpPort = httpPort,
            QdrantGrpcPort = grpcPort,
            QdrantCollectionName = collection,
            QdrantApiKeyEnvironmentVariable = QdrantApiKeyEnvironmentVariable.Trim(),
            EmbeddingProvider = EmbeddingProvider,
            EmbeddingEndpoint = EmbeddingEndpoint,
            EmbeddingModel = EmbeddingModel,
            EmbeddingDimensions = embeddingDimensions,
            ScanIntervalMinutes = scanMinutes,
            MaxRetrievedChunks = maxResults
        };

        _ = RequireSharedEmbeddingConfiguration(settings);

        return settings;
    }

    internal static LocalEmbeddingConfiguration RequireSharedEmbeddingConfiguration(
        LocalVectorLibrarySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!LocalEmbeddingConfiguration.TryCreate(
                settings.EmbeddingProvider,
                settings.EmbeddingEndpoint,
                settings.EmbeddingModel,
                settings.EmbeddingDimensions,
                out var configuration,
                out var embeddingFailure)
            || configuration is null)
        {
            throw new InvalidOperationException(embeddingFailure);
        }

        if (!configuration.TryGetOpenAiApiBaseUri(out _, out var apiBaseFailure))
        {
            throw new InvalidOperationException(
                $"The shared embedding endpoint is not Mem0-compatible: {apiBaseFailure}");
        }

        return configuration;
    }

    private void ChooseFolder()
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "Choose Ali's approved local knowledge folder", UseDescriptionForTitle = true, SelectedPath = Directory.Exists(RootDirectory) ? RootDirectory : LocalVectorLibrarySettings.DefaultRootDirectory() };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) RootDirectory = dialog.SelectedPath;
    }
    private void OpenFolder() { Directory.CreateDirectory(RootDirectory); Process.Start(new ProcessStartInfo { FileName = RootDirectory, UseShellExecute = true }); }
    private void OpenDashboard() => Process.Start(new ProcessStartInfo { FileName = DashboardEndpoint, UseShellExecute = true });
    private void QdrantOnStatusChanged(object? sender, QdrantRuntimeStatus status) { var dispatcher = System.Windows.Application.Current?.Dispatcher; if (dispatcher is not null && !dispatcher.CheckAccess()) dispatcher.BeginInvoke(() => ApplyRuntimeStatus(status)); else ApplyRuntimeStatus(status); }
    private void ApplyRuntimeStatus(QdrantRuntimeStatus status) { RuntimeState = status.State; StatusText = status.Message; }
    private void HandleEmbeddingError(Exception exception) { StatusText = exception.Message.ReplaceLineEndings(" ").Trim(); IsBusy = false; }
    private void HandleError(Exception exception) { RuntimeState = "Error"; StatusText = exception.Message.ReplaceLineEndings(" ").Trim(); IsBusy = false; }
    private void RaiseCommandStates() { foreach (var command in new[] { SaveCommand, TestCommand, TestEmbeddingCommand, StartCommand, StopCommand, ScanCommand, RebuildCommand, RefreshCommand }.OfType<AsyncRelayCommand>()) command.RaiseCanExecuteChanged(); }
}
