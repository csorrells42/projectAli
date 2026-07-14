using Ali.Core.Feedback;
using Ali.Core.Identity;
using Ali.Core.Orchestration;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Sources;
using Ali.Core.Voice;
using Ali.Core.Coding;
using Ali.Infrastructure.Coding;
using Ali.Infrastructure.Identity;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Sources;
using Ali.Infrastructure.Storage;
using Ali.Infrastructure.Voice;

namespace Ali.Infrastructure.Bootstrap;

public sealed class AliServices
{
    public const string LocalAliRootEnvironmentVariable = "ALI_LOCAL_ROOT";

    private readonly HttpClient _httpClient;

    public AliServices(
        string dataRoot,
        string profileDataRoot,
        AssistantProfile assistantProfile,
        SafeActivatingLocalRuntime runtimeController,
        ConversationOrchestrator orchestrator,
        HttpClient httpClient,
        IVoiceRecorder voiceRecorder,
        ISpeechToTextProvider speechToText,
        ITextToSpeechProvider textToSpeech,
        ISpeechPlayer speechPlayer,
        FileConversationStore conversations,
        FileMemoryStore memories,
        FileReminderStore reminders,
        ILocalCodingTool localCodingTool)
    {
        DataRoot = dataRoot;
        ProfileDataRoot = profileDataRoot;
        AssistantProfile = assistantProfile.Normalize();
        RuntimeController = runtimeController;
        Orchestrator = orchestrator;
        _httpClient = httpClient;
        VoiceRecorder = voiceRecorder;
        SpeechToText = speechToText;
        TextToSpeech = textToSpeech;
        SpeechPlayer = speechPlayer;
        Conversations = conversations;
        Memories = memories;
        Reminders = reminders;
        LocalCodingTool = localCodingTool;
    }

    public string DataRoot { get; }

    public string ProfileDataRoot { get; }

    public AssistantProfile AssistantProfile { get; }

    public string AssistantProfilePath => AssistantProfileStore.GetProfilePath(DataRoot);

    public string RuntimeSettingsPath => RuntimeSettingsStore.GetSettingsPath(DataRoot);

    public string RuntimeSettingsExamplePath => RuntimeSettingsStore.GetExamplePath(DataRoot);

    public string LocalVectorLibrarySettingsPath => LocalVectorLibrarySettingsStore.GetSettingsPath(DataRoot);

    public string LocalVectorLibraryIndexPath => LocalVectorLibrarySettingsStore.GetIndexPath(DataRoot);

    public string InternetBackendSettingsPath => WebSourceBackendSettingsStore.GetSettingsPath(DataRoot);

    public string InternetBackendSettingsExamplePath => WebSourceBackendSettingsStore.GetExamplePath(DataRoot);

    public string CodingToolSettingsPath => CodingToolSettingsStore.GetSettingsPath(DataRoot);

    public SafeActivatingLocalRuntime RuntimeController { get; }

    public ConversationOrchestrator Orchestrator { get; }

    public IVoiceRecorder VoiceRecorder { get; }

    public ISpeechToTextProvider SpeechToText { get; private set; }

    public ITextToSpeechProvider TextToSpeech { get; private set; }

    public ISpeechPlayer SpeechPlayer { get; }

    public FileConversationStore Conversations { get; }

    public FileMemoryStore Memories { get; }

    public FileReminderStore Reminders { get; }

    public ILocalCodingTool LocalCodingTool { get; }

    public OpenAiCompatibleRuntimeOptions LoadRuntimeSettings() =>
        RuntimeSettingsStore.LoadOrDefault(DataRoot);

    public void SaveRuntimeSettings(OpenAiCompatibleRuntimeOptions options) =>
        RuntimeSettingsStore.Save(DataRoot, options);

    public LocalVectorLibrarySettings LoadLocalVectorLibrarySettings() =>
        LocalVectorLibrarySettingsStore.LoadOrDefault(DataRoot);

    public void SaveLocalVectorLibrarySettings(LocalVectorLibrarySettings settings) =>
        LocalVectorLibrarySettingsStore.Save(DataRoot, settings);

    public CodingToolSettings LoadCodingToolSettings() =>
        CodingToolSettingsStore.LoadOrDefault(DataRoot);

    public void SaveCodingToolSettings(CodingToolSettings settings)
    {
        CodingToolSettingsStore.Save(DataRoot, settings);
        if (LocalCodingTool is LocalCodingToolService codingToolService)
        {
            codingToolService.UpdateSettings(settings);
        }
    }

    public LocalVectorLibraryRetriever CreateLocalVectorLibraryRetriever() =>
        new(DataRoot, _httpClient, LoadLocalVectorLibrarySettings());

    public WebSourceBackendSettings LoadWebSourceBackendSettings() =>
        WebSourceBackendSettingsStore.LoadOrDefault(DataRoot);

    public void SaveWebSourceBackendSettings(WebSourceBackendSettings settings) =>
        WebSourceBackendSettingsStore.Save(DataRoot, settings);

    public TavilyFirecrawlSourceRetriever CreateWebSourceRetriever() =>
        new(_httpClient, LoadWebSourceBackendSettings);

    public UserDataBackupService CreateUserDataBackupService() =>
        new(DataRoot, ProfileDataRoot);

    public void ConfigureRuntimeCandidate(OpenAiCompatibleRuntimeOptions options)
    {
        ILocalModelRuntime? candidateRuntime = options.Enabled
            ? new OpenAiCompatibleLocalModelRuntime(_httpClient, options, AssistantProfile)
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
            return string.IsNullOrWhiteSpace(configuredRoot)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ali")
                : Path.GetFullPath(configuredRoot.Trim());
        }
    }

    public static string DesktopDataRoot => Path.Combine(LocalAliRoot, "BootstrapData");

    public static string GetProfileDataRoot(AssistantProfile assistantProfile) =>
        Path.Combine(LocalAliRoot, "Profiles", assistantProfile.Normalize().ProfileId);

    public static AliServices CreateForDesktop(AssistantProfile? assistantProfile = null)
    {
        var dataRoot = DesktopDataRoot;
        var profile = (assistantProfile ?? AssistantProfileStore.LoadOrDefault(dataRoot)).Normalize();
        var profileDataRoot = GetProfileDataRoot(profile);

        var correctionStore = new FileCorrectionQueueStore(profileDataRoot);
        var correctionQueue = new CorrectionQueueService(correctionStore);
        var conversations = new FileConversationStore(profileDataRoot);
        var memories = new FileMemoryStore(profileDataRoot);
        var reminders = new FileReminderStore(profileDataRoot);
        RuntimeSettingsStore.WriteExample(dataRoot);

        var fallbackRuntime = new DevelopmentLocalModelRuntime();
        var configuredOptions = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot);
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AliLocalDesktop/1.0");
        WebSourceBackendSettingsStore.WriteExample(dataRoot);
        WebSourceBackendSettingsStore.WriteDefaultIfMissing(dataRoot);
        LocalVectorLibrarySettingsStore.WriteExample(dataRoot);
        CodingToolSettingsStore.WriteExample(dataRoot);
        var localLibrary = new LocalVectorLibraryRetriever(dataRoot, httpClient);
        localLibrary.WriteExample();
        var candidateRuntime = configuredOptions is { Enabled: true }
            ? new OpenAiCompatibleLocalModelRuntime(httpClient, configuredOptions, profile)
            : null;

        var runtime = new SafeActivatingLocalRuntime(fallbackRuntime, candidateRuntime);
        var permissions = new PermissionService();
        var codingSettings = CodingToolSettingsStore.LoadOrDefault(dataRoot);
        var localCodingTool = new LocalCodingToolService(
            codingSettings.ToPolicy(),
            dataRoot,
            configuredNotepadPlusPlusPath: codingSettings.NotepadPlusPlusPath,
            configuredVisualStudioPath: codingSettings.VisualStudioPath,
            configuredCurrentSolutionOrProjectPath: codingSettings.CurrentSolutionOrProjectPath,
            configuredRecentSolutionOrProjectPaths: codingSettings.RecentSolutionOrProjectPaths,
            pdfWorkspaceRoot: codingSettings.ResolvePdfWorkspaceRoot(dataRoot));
        var orchestrator = new ConversationOrchestrator(
            runtime,
            permissions,
            correctionQueue,
            new CompositeSourceRetriever(
                localLibrary,
                new TavilyFirecrawlSourceRetriever(
                    httpClient,
                    () => WebSourceBackendSettingsStore.LoadOrDefault(dataRoot))),
            memoryStore: memories,
            localCodingTool: localCodingTool);

        var voiceSettings = VoiceRuntimeSettingsStore.LoadOrDefault(dataRoot);
        var voiceRecorder = new NAudioVoiceRecorder();
        var speechToText = new WhisperCliSpeechToTextProvider(CreateSpeechToTextOptions(dataRoot, voiceSettings));
        var textToSpeech = CreateTextToSpeechProvider(dataRoot, voiceSettings);
        var speechPlayer = new NAudioWaveSpeechPlayer();

        return new AliServices(
            dataRoot,
            profileDataRoot,
            profile,
            runtime,
            orchestrator,
            httpClient,
            voiceRecorder,
            speechToText,
            textToSpeech,
            speechPlayer,
            conversations,
            memories,
            reminders,
            localCodingTool);
    }

    private static ITextToSpeechProvider CreateTextToSpeechProvider(
        string dataRoot,
        VoiceRuntimeSettings voiceSettings)
    {
        var engine = TextToSpeechEngines.Normalize(voiceSettings.TextToSpeechEngine);
        if (engine == TextToSpeechEngines.Kitten)
        {
            var defaults = KittenCliTextToSpeechOptions.FromEnvironment(dataRoot);
            return new KittenCliTextToSpeechProvider(new KittenCliTextToSpeechOptions(
                PreferInstalledPath(voiceSettings.KittenExecutablePath, LocalVoiceResourceLocator.FindKittenPythonExecutable(AppContext.BaseDirectory), defaults.ExecutablePath),
                PreferInstalledPath(voiceSettings.KittenModelPath, LocalVoiceResourceLocator.FindKittenModelRoot(AppContext.BaseDirectory), defaults.ModelPath),
                KittenVoiceCatalog.Normalize(voiceSettings.KittenVoiceId ?? defaults.VoiceId),
                PreferInstalledKittenArguments(voiceSettings.KittenArgumentsTemplate, defaults.ArgumentsTemplate),
                defaults.OutputDirectory));
        }

        var piperDefaults = PiperCliTextToSpeechOptions.FromEnvironment(dataRoot);
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
}
