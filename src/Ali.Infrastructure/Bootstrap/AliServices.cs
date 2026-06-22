using Ali.Core.Feedback;
using Ali.Core.Orchestration;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Voice;
using Ali.Infrastructure.Runtime;
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
        ISpeechPlayer speechPlayer)
    {
        DataRoot = dataRoot;
        RuntimeController = runtimeController;
        Orchestrator = orchestrator;
        _httpClient = httpClient;
        VoiceRecorder = voiceRecorder;
        SpeechToText = speechToText;
        TextToSpeech = textToSpeech;
        SpeechPlayer = speechPlayer;
    }

    public string DataRoot { get; }

    public string RuntimeSettingsPath => RuntimeSettingsStore.GetSettingsPath(DataRoot);

    public string RuntimeSettingsExamplePath => RuntimeSettingsStore.GetExamplePath(DataRoot);

    public SafeActivatingLocalRuntime RuntimeController { get; }

    public ConversationOrchestrator Orchestrator { get; }

    public IVoiceRecorder VoiceRecorder { get; }

    public ISpeechToTextProvider SpeechToText { get; }

    public ITextToSpeechProvider TextToSpeech { get; }

    public ISpeechPlayer SpeechPlayer { get; }

    public OpenAiCompatibleRuntimeOptions LoadRuntimeSettings() =>
        RuntimeSettingsStore.LoadOrDefault(DataRoot);

    public void SaveRuntimeSettings(OpenAiCompatibleRuntimeOptions options) =>
        RuntimeSettingsStore.Save(DataRoot, options);

    public void ConfigureRuntimeCandidate(OpenAiCompatibleRuntimeOptions options)
    {
        ILocalModelRuntime? candidateRuntime = options.Enabled
            ? new OpenAiCompatibleLocalModelRuntime(_httpClient, options)
            : null;

        RuntimeController.ConfigureCandidate(candidateRuntime);
    }

    public static AliServices CreateForDesktop()
    {
        var dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ali",
            "BootstrapData");

        var correctionStore = new FileCorrectionQueueStore(dataRoot);
        var correctionQueue = new CorrectionQueueService(correctionStore);
        RuntimeSettingsStore.WriteExample(dataRoot);

        var fallbackRuntime = new DevelopmentLocalModelRuntime();
        var configuredOptions = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(dataRoot);
        var httpClient = new HttpClient();
        var candidateRuntime = configuredOptions is { Enabled: true }
            ? new OpenAiCompatibleLocalModelRuntime(httpClient, configuredOptions)
            : null;

        var runtime = new SafeActivatingLocalRuntime(fallbackRuntime, candidateRuntime);
        var permissions = new PermissionService();
        var orchestrator = new ConversationOrchestrator(runtime, permissions, correctionQueue);

        var voiceRecorder = new MciWaveAudioRecorder();
        var speechToText = new WhisperCliSpeechToTextProvider(WhisperCliSpeechToTextOptions.FromEnvironment());
        var textToSpeech = new PiperCliTextToSpeechProvider(PiperCliTextToSpeechOptions.FromEnvironment(dataRoot));
        var speechPlayer = new MciWaveSpeechPlayer();

        return new AliServices(
            dataRoot,
            runtime,
            orchestrator,
            httpClient,
            voiceRecorder,
            speechToText,
            textToSpeech,
            speechPlayer);
    }
}
