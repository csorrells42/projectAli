using Ali.Core.Feedback;
using Ali.Core.Orchestration;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Sources;
using Ali.Core.Voice;
using Ali.Core.Coding;
using Ali.Infrastructure.Coding;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Sources;
using Ali.Infrastructure.Storage;
using Ali.Infrastructure.Voice;

namespace Ali.Infrastructure.Bootstrap;

public sealed class AliServices
{
    private readonly HttpClient _httpClient;

    public AliServices(
        string dataRoot,
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

    public string RuntimeSettingsPath => RuntimeSettingsStore.GetSettingsPath(DataRoot);

    public string RuntimeSettingsExamplePath => RuntimeSettingsStore.GetExamplePath(DataRoot);

    public string LocalVectorLibrarySettingsPath => LocalVectorLibrarySettingsStore.GetSettingsPath(DataRoot);

    public string LocalVectorLibraryIndexPath => LocalVectorLibrarySettingsStore.GetIndexPath(DataRoot);

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

    public void ConfigureRuntimeCandidate(OpenAiCompatibleRuntimeOptions options)
    {
        ILocalModelRuntime? candidateRuntime = options.Enabled
            ? new OpenAiCompatibleLocalModelRuntime(_httpClient, options)
            : null;

        RuntimeController.ConfigureCandidate(candidateRuntime);
    }

    public void ConfigureSpeechTools(
        WhisperCliSpeechToTextOptions speechToTextOptions,
        PiperCliTextToSpeechOptions textToSpeechOptions)
    {
        SpeechToText = new WhisperCliSpeechToTextProvider(speechToTextOptions);
        TextToSpeech = new PiperCliTextToSpeechProvider(textToSpeechOptions);
    }

    public static AliServices CreateForDesktop()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ali",
            "BootstrapData");

        var correctionStore = new FileCorrectionQueueStore(dataRoot);
        var correctionQueue = new CorrectionQueueService(correctionStore);
        var localAliRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ali");
        var conversations = new FileConversationStore(localAliRoot);
        var memories = new FileMemoryStore(localAliRoot);
        var reminders = new FileReminderStore(localAliRoot);
        RuntimeSettingsStore.WriteExample(dataRoot);

        var fallbackRuntime = new DevelopmentLocalModelRuntime();
        var configuredOptions = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot);
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AliLocalDesktop/1.0");
        var sourceStore = new FileSourceRetriever(dataRoot, httpClient);
        sourceStore.WriteExample();
        LocalVectorLibrarySettingsStore.WriteExample(dataRoot);
        CodingToolSettingsStore.WriteExample(dataRoot);
        var localLibrary = new LocalVectorLibraryRetriever(dataRoot, httpClient);
        localLibrary.WriteExample();
        var candidateRuntime = configuredOptions is { Enabled: true }
            ? new OpenAiCompatibleLocalModelRuntime(httpClient, configuredOptions)
            : null;

        var runtime = new SafeActivatingLocalRuntime(fallbackRuntime, candidateRuntime);
        var permissions = new PermissionService();
        var codingSettings = CodingToolSettingsStore.LoadOrDefault(dataRoot);
        var localCodingTool = new LocalCodingToolService(
            codingSettings.ToPolicy(),
            dataRoot,
            configuredNotepadPlusPlusPath: codingSettings.NotepadPlusPlusPath,
            configuredVisualStudioPath: codingSettings.VisualStudioPath);
        var orchestrator = new ConversationOrchestrator(
            runtime,
            permissions,
            correctionQueue,
            new CompositeSourceRetriever(localLibrary, sourceStore.CreateRetriever()),
            memoryStore: memories,
            localCodingTool: localCodingTool);

        var voiceRecorder = new NAudioVoiceRecorder();
        var speechToText = new WhisperCliSpeechToTextProvider(WhisperCliSpeechToTextOptions.FromEnvironment());
        var textToSpeech = new PiperCliTextToSpeechProvider(PiperCliTextToSpeechOptions.FromEnvironment(dataRoot));
        var speechPlayer = new NAudioWaveSpeechPlayer();

        return new AliServices(
            dataRoot,
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
}
