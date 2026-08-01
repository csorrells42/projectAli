using System.Net.Http;
using Ali.Modules.Feedback;
using Ali.Modules.Identity;
using Ali.Modules.Runtime;
using Ali.Modules.Internet;
using Ali.Modules.RAG;
using Ali.Modules.Voice;
using Ali.Modules.Storage;
using Ali.Modules.Coordinator;
using Ali.Modules.Mcp;
using Ali.Modules.Permissions;
using Ali.Modules.WorkstationFiles;
using Ali.Modules.AgentWorkMemory;
using Ali.Modules.UserMemory;
using Ali.Modules.Coding;
using Ali.Modules.Calendar;
using Ali.Modules.ToolDiscovery;
using Ali.Modules.Orchestration.Evidence;
using Ali.Modules.Orchestration.Observation;

namespace Ali;

public sealed class AliServices
{
    public const string LocalAliRootEnvironmentVariable = "ALI_LOCAL_ROOT";
    private const string LocalAliRootFolderName = "AliFiles";
    private const string LegacyLocalAliRootFolderName = "Ali";

    private readonly HttpClient _runtimeHttpClient;
    private readonly HttpClient _internetHttpClient;

    public AliServices(
        string dataRoot,
        string userDataRoot,
        string profileDataRoot,
        AssistantProfile assistantProfile,
        SafeActivatingLocalRuntime runtimeController,
        ConversationOrchestrator orchestrator,
        HttpClient runtimeHttpClient,
        HttpClient internetHttpClient,
        IVoiceRecorder voiceRecorder,
        ISpeechToTextProvider speechToText,
        ITextToSpeechProvider textToSpeech,
        ISpeechPlayer speechPlayer,
        FileConversationStore conversations,
        FileMemoryStore memories,
        FileReminderStore reminders,
        McpClientManager mcpClients,
        McpServerHost mcpServer,
        QdrantServiceManager qdrant,
        IActiveUserSession activeUsers,
        Mem0UserMemoryService userMemories,
        AgentToolPermissionStore toolPermissions,
        AliWorkstationFileAccess fileAccess,
        AliAgentWorkMemory agentWorkMemory,
        AliCodingModule codingModule,
        IAsyncDisposable? orchestrationObserver = null)
    {
        DataRoot = dataRoot;
        UserDataRoot = userDataRoot;
        ProfileDataRoot = profileDataRoot;
        AssistantProfile = assistantProfile.Normalize();
        RuntimeController = runtimeController;
        Orchestrator = orchestrator;
        _runtimeHttpClient = runtimeHttpClient;
        _internetHttpClient = internetHttpClient;
        VoiceRecorder = voiceRecorder;
        SpeechToText = speechToText;
        TextToSpeech = textToSpeech;
        SpeechPlayer = speechPlayer;
        Conversations = conversations;
        Memories = memories;
        Reminders = reminders;
        McpClients = mcpClients;
        McpServer = mcpServer;
        Qdrant = qdrant;
        ActiveUsers = activeUsers;
        UserMemories = userMemories;
        ToolPermissions = toolPermissions;
        FileAccess = fileAccess;
        AgentWorkMemory = agentWorkMemory;
        CodingModule = codingModule;
        OrchestrationObserver = orchestrationObserver;
        GoogleBillingGuard = new GoogleBillingSettingsGuard(DataRoot);
    }

    public string DataRoot { get; }

    public string UserDataRoot { get; }

    public string ProfileDataRoot { get; }

    public AssistantProfile AssistantProfile { get; }

    public string AssistantProfilePath => AssistantProfileStore.GetProfilePath(DataRoot);

    public string RuntimeSettingsPath => RuntimeSettingsStore.GetSettingsPath(DataRoot);

    public string RuntimeSettingsExamplePath => RuntimeSettingsStore.GetExamplePath(DataRoot);

    public string LocalVectorLibrarySettingsPath => LocalVectorLibrarySettingsStore.GetSettingsPath(DataRoot);

    public string LocalVectorLibraryDataPath => LocalVectorLibrarySettingsStore.GetQdrantDataPath(DataRoot);

    public string InternetBackendSettingsPath => WebSourceBackendSettingsStore.GetSettingsPath(DataRoot);

    public string InternetBackendSettingsExamplePath => WebSourceBackendSettingsStore.GetExamplePath(DataRoot);

    public string McpClientSettingsPath => McpClientSettingsStore.GetSettingsPath(DataRoot);

    public string McpServerSettingsPath => McpServerSettingsStore.GetSettingsPath(DataRoot);

    public string UserMemorySettingsPath => UserMemorySettingsStore.GetPath(DataRoot);

    public string AgentOrchestrationSettingsPath => AgentOrchestrationSettingsStore.GetPath(DataRoot);

    public string WorkflowCheckpointPath => AgentOrchestrationSettingsStore.GetCheckpointPath(UserDataRoot);

    public SafeActivatingLocalRuntime RuntimeController { get; }

    public ConversationOrchestrator Orchestrator { get; }

    public AliCodingModule CodingModule { get; }

    public IVoiceRecorder VoiceRecorder { get; }

    public ISpeechToTextProvider SpeechToText { get; private set; }

    public ITextToSpeechProvider TextToSpeech { get; private set; }

    public ISpeechPlayer SpeechPlayer { get; }

    public FileConversationStore Conversations { get; }

    public FileMemoryStore Memories { get; }

    public FileReminderStore Reminders { get; }

    public McpClientManager McpClients { get; }

    public McpServerHost McpServer { get; }

    public QdrantServiceManager Qdrant { get; }

    public IActiveUserSession ActiveUsers { get; }

    public Mem0UserMemoryService UserMemories { get; }

    public AgentToolPermissionStore ToolPermissions { get; }

    public AliWorkstationFileAccess FileAccess { get; }

    public AliAgentWorkMemory AgentWorkMemory { get; }

    internal IAsyncDisposable? OrchestrationObserver { get; }

    internal ShadowObservationHealthSnapshot? OrchestrationObservationHealth =>
        (OrchestrationObserver as IShadowToolObserver)?.Health;

    public GoogleBillingSettingsGuard GoogleBillingGuard { get; }

    public OpenAiCompatibleRuntimeOptions LoadRuntimeSettings() =>
        RuntimeSettingsStore.LoadOrDefault(DataRoot);

    public void SaveRuntimeSettings(OpenAiCompatibleRuntimeOptions options) =>
        RuntimeSettingsStore.Save(DataRoot, options);

    public LocalVectorLibrarySettings LoadLocalVectorLibrarySettings() =>
        LocalVectorLibrarySettingsStore.LoadOrDefault(DataRoot);

    public void SaveLocalVectorLibrarySettings(LocalVectorLibrarySettings settings) =>
        LocalVectorLibrarySettingsStore.Save(DataRoot, settings);

    public LocalVectorLibraryRetriever CreateLocalVectorLibraryRetriever() =>
        new(DataRoot, _runtimeHttpClient, LoadLocalVectorLibrarySettings(), Qdrant);

    public UserMemorySettings LoadUserMemorySettings() =>
        UserMemorySettingsStore.LoadOrDefault(DataRoot);

    public void SaveUserMemorySettings(UserMemorySettings settings) =>
        UserMemorySettingsStore.Save(DataRoot, settings);

    public AgentOrchestrationSettings LoadAgentOrchestrationSettings() =>
        AgentOrchestrationSettingsStore.LoadOrDefault(DataRoot);

    public void SaveAgentOrchestrationSettings(AgentOrchestrationSettings settings) =>
        AgentOrchestrationSettingsStore.Save(DataRoot, settings);

    public WebSourceBackendSettings LoadWebSourceBackendSettings() =>
        WebSourceBackendSettingsStore.LoadOrDefault(DataRoot);

    public void SaveWebSourceBackendSettings(WebSourceBackendSettings settings) =>
        WebSourceBackendSettingsStore.Save(DataRoot, settings);

    public string GetGeminiGroundingUsageStatus(WebSourceBackendSettings settings) =>
        new GeminiGroundingUsageLedger(DataRoot).GetStatus(settings, DateTimeOffset.UtcNow);

    public TavilyFirecrawlSourceRetriever CreateWebSourceRetriever() =>
        new(_internetHttpClient, LoadWebSourceBackendSettings, dataRoot: DataRoot);

    public UserDataBackupService CreateUserDataBackupService() =>
        new(DataRoot, UserDataRoot);

    public void ConfigureRuntimeCandidate(OpenAiCompatibleRuntimeOptions options)
    {
        ILocalModelRuntime? candidateRuntime = options.Enabled
            ? new OpenAiCompatibleLocalModelRuntime(_runtimeHttpClient, options, AssistantProfile)
            : null;

        RuntimeController.ConfigureCandidate(candidateRuntime);
    }

    public void ConfigureSpeechTools(
        WhisperCliSpeechToTextOptions speechToTextOptions,
        ITextToSpeechProvider textToSpeechProvider)
    {
        SpeechToText = new WhisperCliSpeechToTextProvider(speechToTextOptions);
        TextToSpeech = textToSpeechProvider;
    }

    public void ConfigureSpeechTools(
        WhisperCliSpeechToTextOptions speechToTextOptions,
        PiperCliTextToSpeechOptions textToSpeechOptions)
    {
        ConfigureSpeechTools(speechToTextOptions, new PiperCliTextToSpeechProvider(textToSpeechOptions));
    }

    public static string LocalAliRoot
    {
        get
        {
            var configuredRoot = Environment.GetEnvironmentVariable(LocalAliRootEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(configuredRoot))
            {
                return Path.GetFullPath(configuredRoot.Trim());
            }

            return AliDataFolderSelectionStore.Load()
                ?? Path.Combine(GetLocalApplicationDataRoot(), LocalAliRootFolderName);
        }
    }

    public static string DesktopDataRoot => DesktopSettingsRoot;

    public static string DesktopSettingsRoot => Path.Combine(LocalAliRoot, "Settings");

    public static string DesktopUserDataRoot => Path.Combine(LocalAliRoot, "Data");

    public static string GetProfileDataRoot(AssistantProfile assistantProfile) =>
        Path.Combine(DesktopUserDataRoot, "Profiles", assistantProfile.Normalize().ProfileId);

    internal static string GetOrchestrationEvidenceRoot(string profileDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileDataRoot);
        return Path.Combine(
            Path.GetFullPath(profileDataRoot),
            "Orchestration",
            "Evidence");
    }

    public static void EnsureLocalAliFilesLayout()
    {
        Directory.CreateDirectory(LocalAliRoot);
        Directory.CreateDirectory(DesktopSettingsRoot);
        Directory.CreateDirectory(DesktopUserDataRoot);
        Directory.CreateDirectory(Path.Combine(LocalAliRoot, "Backups"));
        Directory.CreateDirectory(Path.Combine(DesktopUserDataRoot, "Logs"));

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LocalAliRootEnvironmentVariable)))
        {
            return;
        }

        var legacyRoot = Path.Combine(GetLocalApplicationDataRoot(), LegacyLocalAliRootFolderName);
        if (!Directory.Exists(legacyRoot)
            || Path.GetFullPath(legacyRoot).Equals(Path.GetFullPath(LocalAliRoot), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CopyMissingFiles(Path.Combine(legacyRoot, "BootstrapData"), DesktopSettingsRoot);
        CopyMissingFiles(Path.Combine(legacyRoot, "Profiles"), Path.Combine(DesktopUserDataRoot, "Profiles"));
        CopyMissingFiles(Path.Combine(legacyRoot, "Backups"), Path.Combine(LocalAliRoot, "Backups"));
        CopyMissingFiles(Path.Combine(legacyRoot, "Logs"), Path.Combine(DesktopUserDataRoot, "Logs"));
        CopyMissingFiles(LocalVectorLibrarySettings.LegacyDefaultRootDirectory(), Path.Combine(DesktopUserDataRoot, "RAG", "Library"));
    }

    public static AliServices CreateForDesktop(AssistantProfile? assistantProfile = null)
    {
        EnsureLocalAliFilesLayout();

        var dataRoot = DesktopDataRoot;
        var userDataRoot = DesktopUserDataRoot;
        var profile = (assistantProfile ?? AssistantProfileStore.LoadOrDefault(dataRoot)).Normalize();
        var profileDataRoot = GetProfileDataRoot(profile);

        var correctionStore = new FileCorrectionQueueStore(profileDataRoot);
        var correctionQueue = new CorrectionQueueService(correctionStore);
        var conversations = new FileConversationStore(profileDataRoot);
        var memories = new FileMemoryStore(profileDataRoot);
        var reminders = new FileReminderStore(
            profileDataRoot,
            new WindowsCalendarEventPublisher(profileDataRoot));
        PersistentUserDataBootstrapper.EnsureCreated(
            dataRoot,
            profileDataRoot,
            profile,
            conversations,
            memories,
            reminders,
            correctionStore);

        var fallbackRuntime = new DevelopmentLocalModelRuntime();
        var configuredOptions = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot);
        var runtimeHttpClient = new HttpClient();
        runtimeHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AliLocalDesktop/1.0");
        var internetHttpClient = InternetHttpClientFactory.CreateClient();
        var qdrant = new QdrantServiceManager(dataRoot);
        var activeUsers = new ActiveUserSession(
            dataRoot,
            Path.Combine(userDataRoot, "Vision"));
        var mem0Client = new Mem0ProcessClient(
            dataRoot,
            qdrant,
            () => LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot),
            () => UserMemorySettingsStore.LoadOrDefault(dataRoot),
            () => RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot));
        var userMemories = new Mem0UserMemoryService(
            mem0Client,
            () => UserMemorySettingsStore.LoadOrDefault(dataRoot));
        var initialUserSelection = activeUsers.CaptureSelectionSnapshot();
        if (initialUserSelection.IsResolved)
        {
            userMemories.BeginWarmup(initialUserSelection.SelectedUser!);
        }
        var toolPermissions = new AgentToolPermissionStore(dataRoot);
        var fileAccess = AliWorkstationFileAccess.CreateDefault(
            userDataRoot,
            profileDataRoot,
            toolPermissions,
            activeUsers);
        var agentWorkMemory = new AliAgentWorkMemory(userDataRoot);
        var codingModule = new AliCodingModule(
            fileAccess,
            () => AgentOrchestrationSettingsStore.LoadOrDefault(dataRoot),
            () => RuntimeSettingsStore.LoadOrDefault(dataRoot),
            AppContext.BaseDirectory);
        var localLibrary = new LocalVectorLibraryRetriever(dataRoot, runtimeHttpClient, qdrant: qdrant);
        localLibrary.WriteExample();
        var semanticToolCatalog = new QdrantSemanticToolCatalog(
            runtimeHttpClient,
            qdrant,
            () => LocalVectorLibrarySettingsStore.LoadOrDefault(dataRoot));
        var candidateRuntime = configuredOptions is { Enabled: true }
            ? new OpenAiCompatibleLocalModelRuntime(runtimeHttpClient, configuredOptions, profile)
            : null;

        var runtime = new SafeActivatingLocalRuntime(fallbackRuntime, candidateRuntime);
        var webSources = new TavilyFirecrawlSourceRetriever(
            internetHttpClient,
            () => WebSourceBackendSettingsStore.LoadOrDefault(dataRoot),
            dataRoot: dataRoot);
        var webResearch = new McpWebResearchClient(
            () => WebSourceBackendSettingsStore.LoadOrDefault(dataRoot));
        var mcpClients = new McpClientManager(dataRoot);
        var mcpServer = new McpServerHost(
            dataRoot,
            new AliMcpServerToolFactory(
                localLibrary,
                webSources,
                webResearch,
                memories,
                reminders,
                profile,
                userMemories,
                activeUsers,
                () => UserMemorySettingsStore.LoadOrDefault(dataRoot),
                codingModule));
        var voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(dataRoot);
        var voiceRecorder = new NAudioVoiceRecorder();
        var speechToText = new WhisperCliSpeechToTextProvider(CreateSpeechToTextOptions(dataRoot, voiceSettings));
        var textToSpeech = CreateTextToSpeechProvider(userDataRoot, voiceSettings);
        var speechPlayer = new NAudioWaveSpeechPlayer();
        var shadowObserver = new ShadowToolObservationService(
            new EvidenceLedger(
                GetOrchestrationEvidenceRoot(profileDataRoot),
                profile.ProfileId));
        try
        {
            var coordinator = new AliToolCoordinator(
                runtime,
                runtime,
                localLibrary,
                webSources,
                webResearch,
                memories,
                reminders,
                profile,
                mcpClients,
                toolPermissions,
                fileAccess,
                agentWorkMemory,
                codingModule,
                userMemories,
                activeUsers,
                () => UserMemorySettingsStore.LoadOrDefault(dataRoot),
                AgentOrchestrationSettingsStore.GetCheckpointPath(userDataRoot),
                () => AgentOrchestrationSettingsStore.LoadOrDefault(dataRoot),
                semanticToolCatalog,
                shadowObserver);
            var orchestrator = new ConversationOrchestrator(
                runtime,
                correctionQueue,
                coordinator);

            return new AliServices(
                dataRoot,
                userDataRoot,
                profileDataRoot,
                profile,
                runtime,
                orchestrator,
                runtimeHttpClient,
                internetHttpClient,
                voiceRecorder,
                speechToText,
                textToSpeech,
                speechPlayer,
                conversations,
                memories,
                reminders,
                mcpClients,
                mcpServer,
                qdrant,
                activeUsers,
                userMemories,
                toolPermissions,
                fileAccess,
                agentWorkMemory,
                codingModule,
                shadowObserver);
        }
        catch
        {
            try
            {
                shadowObserver.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Preserve the original composition failure.
            }

            throw;
        }
    }

    private static ITextToSpeechProvider CreateTextToSpeechProvider(
        string userDataRoot,
        VoiceRuntimeSettings voiceSettings)
    {
        var engine = TextToSpeechEngines.Normalize(voiceSettings.TextToSpeechEngine);
        if (engine == TextToSpeechEngines.Kitten)
        {
            var defaults = KittenCliTextToSpeechOptions.FromEnvironment(userDataRoot);
            return new KittenCliTextToSpeechProvider(new KittenCliTextToSpeechOptions(
                PreferInstalledPath(voiceSettings.KittenExecutablePath, LocalVoiceResourceLocator.FindKittenPythonExecutable(AppContext.BaseDirectory), defaults.ExecutablePath),
                PreferInstalledPath(voiceSettings.KittenModelPath, LocalVoiceResourceLocator.FindKittenModelRoot(AppContext.BaseDirectory), defaults.ModelPath),
                KittenVoiceCatalog.Normalize(voiceSettings.KittenVoiceId ?? defaults.VoiceId),
                PreferInstalledKittenArguments(voiceSettings.KittenArgumentsTemplate, defaults.ArgumentsTemplate),
                defaults.OutputDirectory));
        }

        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(userDataRoot);
        return new PiperCliTextToSpeechProvider(new PiperCliTextToSpeechOptions(
            PreferInstalledPath(voiceSettings.PiperExecutablePath, LocalVoiceResourceLocator.FindPythonExecutable(AppContext.BaseDirectory), piperDefaults.ExecutablePath),
            PreferInstalledPath(voiceSettings.PiperModelPath, PreferredInstalledPiperModelPath(), piperDefaults.ModelPath),
            voiceSettings.PiperVoiceId ?? piperDefaults.VoiceId,
            string.IsNullOrWhiteSpace(voiceSettings.PiperArgumentsTemplate) ? "-m piper --model \"{model}\" --output_file \"{output}\"" : voiceSettings.PiperArgumentsTemplate,
            piperDefaults.OutputDirectory));
    }

    private static WhisperCliSpeechToTextOptions CreateSpeechToTextOptions(
        string dataRoot,
        VoiceRuntimeSettings voiceSettings)
    {
        var defaults = WhisperCliSpeechToTextOptions.FromEnvironment();
        var script = LocalVoiceResourceLocator.FindWhisperScript(AppContext.BaseDirectory);
        var localArguments = File.Exists(script)
            ? $"\"{script}\" --audio \"{{audio}}\" --model-root \"{{model}}\" --model-id small.en --output-base \"{{outputBase}}\" --vad-filter"
            : null;
        return new WhisperCliSpeechToTextOptions(
            PreferInstalledPath(voiceSettings.WhisperExecutablePath, LocalVoiceResourceLocator.FindWhisperPythonExecutable(AppContext.BaseDirectory), defaults.ExecutablePath),
            PreferInstalledPath(voiceSettings.WhisperModelPath, LocalVoiceResourceLocator.FindWhisperModelRoot(AppContext.BaseDirectory), defaults.ModelPath),
            PreferInstalledScriptArguments(voiceSettings.WhisperArgumentsTemplate, localArguments, defaults.ArgumentsTemplate, "local_whisper_stt.py"),
            defaults.OutputTextSuffix);
    }

    private static string? PreferInstalledPath(string? configured, string? installed, string? fallback)
    {
        if (LocalPathExists(installed))
        {
            return installed;
        }

        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }

    private static string PreferInstalledKittenArguments(string? configured, string fallback)
    {
        var script = LocalVoiceResourceLocator.FindKittenScript(AppContext.BaseDirectory);
        var localArguments = File.Exists(script)
            ? "\"{script}\" --model \"{model}\" --voice \"{voice}\" --output \"{output}\" --rate \"{rate}\""
            : null;
        return PreferInstalledScriptArguments(configured, localArguments, fallback, "local_kitten_tts.py");
    }

    private static string PreferInstalledScriptArguments(
        string? configured,
        string? installedArguments,
        string fallback,
        string scriptName)
    {
        if (!string.IsNullOrWhiteSpace(installedArguments)
            && (string.IsNullOrWhiteSpace(configured)
                || configured.Contains(scriptName, StringComparison.OrdinalIgnoreCase)
                || configured.Contains("{script}", StringComparison.OrdinalIgnoreCase)))
        {
            return installedArguments;
        }

        return string.IsNullOrWhiteSpace(configured) ? fallback : configured;
    }

    private static string? PreferredInstalledPiperModelPath()
    {
        var directory = LocalVoiceResourceLocator.FindPiperVoiceDirectory(AppContext.BaseDirectory);
        if (directory is null)
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, "en_US-*.onnx", SearchOption.TopDirectoryOnly)
            .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Equals("en_US-hfc_female-medium", StringComparison.OrdinalIgnoreCase))
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static bool LocalPathExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private static string GetLocalApplicationDataRoot() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static void CopyMissingFiles(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            return;
        }

        try
        {
            foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
                var targetPath = Path.Combine(targetRoot, relativePath);
                if (File.Exists(targetPath))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(sourcePath, targetPath, overwrite: false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Startup must preserve existing data and continue even if an old file cannot be migrated.
        }
    }

    private static void CopyMissingFile(string sourcePath, string targetPath)
    {
        try
        {
            if (!File.Exists(sourcePath) || File.Exists(targetPath))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }
}
