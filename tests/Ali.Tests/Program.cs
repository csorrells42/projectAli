using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Models;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Truthfulness;
using Ali.Core.Voice;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Storage;
using Ali.Infrastructure.Voice;

if (args.Contains("--real-runtime", StringComparer.OrdinalIgnoreCase))
{
    await RunRealRuntimeValidationAsync();
    return;
}

if (args.Contains("--real-vision", StringComparer.OrdinalIgnoreCase))
{
    await RunRealVisionValidationAsync();
    return;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("truthfulness reports unknown without receipt", TestTruthfulnessUnknownWithoutReceipt),
    ("permission service requires confirmation for package restore", TestPermissionRequiresPackageConfirmation),
    ("permission service allows confirmed local build", TestPermissionAllowsConfirmedBuild),
    ("correction queue preserves exact question and answer", TestCorrectionQueuePreservesExactQuestionAndAnswer),
    ("endpoint policy allows loopback runtime", TestEndpointPolicyAllowsLoopback),
    ("endpoint policy refuses public runtime", TestEndpointPolicyRefusesPublicEndpoint),
    ("runtime settings save and load", TestRuntimeSettingsSaveAndLoad),
    ("failed health check does not activate real runtime", TestFailedHealthCheckDoesNotActivateRuntime),
    ("successful health check can activate real runtime", TestSuccessfulHealthCheckCanActivateRuntime),
    ("vision health check sends image content", TestVisionHealthCheckSendsImageContent),
    ("OpenAI stream parser extracts content delta", TestOpenAiStreamParserExtractsContentDelta),
    ("OpenAI response parser extracts message content", TestOpenAiResponseParserExtractsMessageContent),
    ("runtime cancellation path throws OperationCanceledException", TestRuntimeCancellationPath),
    ("correction queue stores runtime snapshot", TestCorrectionQueueStoresRuntimeSnapshot),
    ("voice audio input is temporary by default", TestVoiceAudioInputIsTemporaryByDefault),
    ("voice transcript becomes user chat text", TestVoiceTranscriptBecomesUserChatText),
    ("speech tool policy refuses cloud STT endpoint", TestSpeechPolicyRefusesCloudSttEndpoint),
    ("speech tool policy refuses cloud TTS endpoint", TestSpeechPolicyRefusesCloudTtsEndpoint),
    ("local STT fake success path", TestLocalSttFakeSuccessPath),
    ("local STT fake failure path", TestLocalSttFakeFailurePath),
    ("local TTS fake success path", TestLocalTtsFakeSuccessPath),
    ("speech player stop cancels playback", TestSpeechPlayerStopCancelsPlayback),
    ("spoken response cleaner strips clutter", TestSpokenResponseCleanerStripsClutter),
    ("voice risky command requires visible confirmation", TestVoiceRiskyCommandRequiresVisibleConfirmation),
    ("voice origin correction queue metadata", TestVoiceOriginCorrectionQueueMetadata)
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex.Message);
    }
}

if (failed > 0)
{
    Environment.ExitCode = 1;
}

static Task TestTruthfulnessUnknownWithoutReceipt()
{
    Equal(EvidenceStatus.Unknown, TruthfulnessPolicy.EvidenceFromReceipt(null));
    Contains("no action receipt", TruthfulnessPolicy.DescribeActionStatus(null));
    return Task.CompletedTask;
}

static Task TestPermissionRequiresPackageConfirmation()
{
    var service = new PermissionService();
    var request = PermissionRequest.Create(
        "dotnet restore",
        PermissionRisk.PackageRestore,
        "Restore packages for a project.");

    var decision = service.Evaluate(request);

    Equal(PermissionDecisionKind.RequireConfirmation, decision.Kind);
    Contains("require explicit confirmation", decision.Reason);
    return Task.CompletedTask;
}

static Task TestPermissionAllowsConfirmedBuild()
{
    var service = new PermissionService();
    var request = PermissionRequest.Create(
        "dotnet build",
        PermissionRisk.LocalBuild,
        "Build the current solution.",
        userConfirmed: true);

    var decision = service.Evaluate(request);

    Equal(PermissionDecisionKind.Allow, decision.Kind);
    return Task.CompletedTask;
}

static async Task TestCorrectionQueuePreservesExactQuestionAndAnswer()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var store = new FileCorrectionQueueStore(directory);
    var queue = new CorrectionQueueService(store);

    var report = await queue.FlagIncorrectAsync(
        conversationId: "conv_test",
        userMessageId: "msg_user",
        assistantMessageId: "msg_assistant",
        question: "What command ran?",
        answer: "The command succeeded.",
        modelProfile: ModelProfile.UnconfiguredFactorySafe(),
        answerEvidenceStatus: EvidenceStatus.Unknown,
        category: CorrectionCategory.ClaimedActionSucceededWhenItDidNot,
        userNote: "No receipt existed.",
        cancellationToken: CancellationToken.None);

    var reports = await store.ListAsync(CancellationToken.None);

    Equal(1, reports.Count);
    Equal(report.Id, reports[0].Id);
    Equal("What command ran?", reports[0].Question);
    Equal("The command succeeded.", reports[0].Answer);
    Equal(EvidenceStatus.Unknown, reports[0].AnswerEvidenceStatus);
}

static Task TestEndpointPolicyAllowsLoopback()
{
    var result = LocalEndpointPolicy.Validate(new Uri("http://127.0.0.1:11434/v1/"), allowPrivateLan: false);

    Equal(true, result.IsAllowed);
    return Task.CompletedTask;
}

static Task TestEndpointPolicyRefusesPublicEndpoint()
{
    var result = LocalEndpointPolicy.Validate(new Uri("https://api.openai.com/v1/"), allowPrivateLan: false);

    Equal(false, result.IsAllowed);
    Contains("loopback", result.Reason);
    return Task.CompletedTask;
}

static Task TestOpenAiStreamParserExtractsContentDelta()
{
    var content = OpenAiStreamParser.ExtractContentDelta(
        """{"choices":[{"delta":{"content":"hello"}}]}""");

    Equal("hello", content);
    return Task.CompletedTask;
}

static async Task TestRuntimeSettingsSaveAndLoad()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var options = CreateRuntimeOptions("fake-local-model", supportsVision: true);

    RuntimeSettingsStore.Save(directory, options);
    var loaded = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(directory);

    NotNull(loaded, "Loaded runtime settings should not be null.");
    Equal(options.Endpoint, loaded!.Endpoint);
    Equal(options.Model, loaded.Model);
    Equal(options.ContextTokens, loaded.ContextTokens);
    Equal(options.OutputTokenLimit, loaded.OutputTokenLimit);
    Equal(options.Temperature, loaded.Temperature);
    Equal(options.StreamingEnabled, loaded.StreamingEnabled);
    Equal(options.SupportsVision, loaded.SupportsVision);

    await Task.CompletedTask;
}

static async Task TestFailedHealthCheckDoesNotActivateRuntime()
{
    var fallback = new DevelopmentLocalModelRuntime();
    var failedCandidate = new OpenAiCompatibleLocalModelRuntime(
        new HttpClient(new FakeOpenAiHandler(model: "other-model")),
        CreateRuntimeOptions("missing-model"));
    var controller = new SafeActivatingLocalRuntime(fallback, failedCandidate);

    var health = await controller.CheckCandidateAsync(CancellationToken.None);
    var activated = controller.ActivateLastHealthChecked();

    Equal(false, health.Succeeded);
    Equal(false, activated);
    Equal("none", controller.ActiveProfile.PackageId);
}

static async Task TestSuccessfulHealthCheckCanActivateRuntime()
{
    var fallback = new DevelopmentLocalModelRuntime();
    var options = CreateRuntimeOptions("fake-local-model");
    var candidate = new OpenAiCompatibleLocalModelRuntime(
        new HttpClient(new FakeOpenAiHandler(options.Model)),
        options);
    var controller = new SafeActivatingLocalRuntime(fallback, candidate);

    var health = await controller.CheckCandidateAsync(CancellationToken.None);

    Equal(true, health.Succeeded);
    Equal("none", controller.ActiveProfile.PackageId);
    Equal(true, controller.ActivateLastHealthChecked());
    Equal(options.Model, controller.ActiveProfile.PackageId);
    Equal(options.Endpoint.ToString(), controller.ActiveProfile.RuntimeEndpoint);
}

static async Task TestVisionHealthCheckSendsImageContent()
{
    var options = CreateRuntimeOptions("fake-vision-model", supportsVision: true);
    var handler = new FakeOpenAiHandler(options.Model);
    var runtime = new OpenAiCompatibleLocalModelRuntime(new HttpClient(handler), options);

    var health = await runtime.CheckHealthAsync(CancellationToken.None);

    Equal(true, health.Succeeded);
    Equal(true, handler.ImageRequestCount > 0);
    Contains("\"image_url\":{\"url\":\"data:image/png;base64,", handler.LastChatBody);
}

static Task TestOpenAiResponseParserExtractsMessageContent()
{
    var content = OpenAiStreamParser.ExtractMessageContent(
        """{"choices":[{"message":{"content":"OK"}}]}""");

    Equal("OK", content);
    return Task.CompletedTask;
}

static async Task TestRuntimeCancellationPath()
{
    var options = CreateRuntimeOptions("fake-local-model");
    var runtime = new OpenAiCompatibleLocalModelRuntime(
        new HttpClient(new FakeOpenAiHandler(options.Model)),
        options);

    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();

    try
    {
        await foreach (var _ in runtime.StreamChatAsync(
                           new ChatRequest("conv", "msg", "hello", Array.Empty<ChatMessage>()),
                           cancellation.Token))
        {
        }

        throw new InvalidOperationException("Expected cancellation did not occur.");
    }
    catch (OperationCanceledException)
    {
    }
}

static async Task TestCorrectionQueueStoresRuntimeSnapshot()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var store = new FileCorrectionQueueStore(directory);
    var queue = new CorrectionQueueService(store);
    var options = CreateRuntimeOptions("fake-local-model");
    var profile = options.ToModelProfile(isLastKnownGood: true);

    await queue.FlagIncorrectAsync(
        conversationId: "conv_runtime",
        userMessageId: "msg_user",
        assistantMessageId: "msg_assistant",
        question: "What model are you using?",
        answer: "I am using a local model.",
        modelProfile: profile,
        answerEvidenceStatus: EvidenceStatus.Unverified,
        category: CorrectionCategory.Other,
        userNote: "Runtime snapshot check.",
        cancellationToken: CancellationToken.None);

    var reports = await store.ListAsync(CancellationToken.None);

    Equal(1, reports.Count);
    Equal(profile.RuntimeKind, reports[0].RuntimeKind);
    Equal(profile.RuntimeLocation, reports[0].RuntimeLocation);
    Equal(profile.RuntimeEndpoint, reports[0].RuntimeEndpoint);
    Equal(profile.PackageId, reports[0].ModelPackage);
    Equal(profile.ContextTokens, reports[0].ContextTokens);
    Equal(profile.OutputTokenLimit, reports[0].OutputTokenLimit);
    Equal(profile.Temperature, reports[0].Temperature);
    Equal(profile.StreamingEnabled, reports[0].StreamingEnabled);
}

static Task TestVoiceAudioInputIsTemporaryByDefault()
{
    var audio = new VoiceAudioInput("voice.wav", "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow);

    Equal("audio/wav", audio.ContentType);
    Equal(false, audio.RetainAudio);
    return Task.CompletedTask;
}

static Task TestVoiceTranscriptBecomesUserChatText()
{
    var transcript = new SpeechTranscript("What is your name?", "fake local STT", "unit-test", DateTimeOffset.UtcNow);
    var request = new ChatRequest("conv_voice", "msg_voice", transcript.Text, Array.Empty<ChatMessage>());

    Equal("What is your name?", request.UserText);
    return Task.CompletedTask;
}

static Task TestSpeechPolicyRefusesCloudSttEndpoint()
{
    ThrowsInvalidOperation(() => LocalSpeechToolPolicy.EnsureLocalOnly("Speech-to-text", "https://api.example.com/stt"));
    return Task.CompletedTask;
}

static Task TestSpeechPolicyRefusesCloudTtsEndpoint()
{
    ThrowsInvalidOperation(() => LocalSpeechToolPolicy.EnsureLocalOnly("Text-to-speech", "https://api.example.com/tts"));
    return Task.CompletedTask;
}

static async Task TestLocalSttFakeSuccessPath()
{
    var provider = new FakeSpeechToTextProvider("hello Ali");
    var transcript = await provider.TranscribeAsync(
        new VoiceAudioInput("fake.wav", "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow),
        CancellationToken.None);

    Equal("hello Ali", transcript.Text);
    Equal("Fake local STT", transcript.ProviderName);
    Equal("unit-test", transcript.Mode);
}

static async Task TestLocalSttFakeFailurePath()
{
    var provider = new FakeSpeechToTextProvider("ignored", fail: true);

    await ThrowsInvalidOperationAsync(() => provider.TranscribeAsync(
        new VoiceAudioInput("fake.wav", "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow),
        CancellationToken.None));
}

static async Task TestLocalTtsFakeSuccessPath()
{
    var provider = new FakeTextToSpeechProvider();
    var result = await provider.SynthesizeAsync(
        "hello",
        new VoiceSettings("fake-voice", Rate: 1.0, RetainAudio: false),
        CancellationToken.None);

    Equal("Fake local TTS", result.ProviderName);
    Equal("fake-voice", result.VoiceId);
    Equal(false, result.RetainAudio);
}

static async Task TestSpeechPlayerStopCancelsPlayback()
{
    var player = new FakeSpeechPlayer();
    using var cancellation = new CancellationTokenSource();
    var playTask = player.PlayAsync("fake.wav", cancellation.Token);

    player.Stop();
    cancellation.Cancel();
    await playTask;

    Equal(true, player.StopRequested);
    Equal(false, player.IsSpeaking);
}

static Task TestSpokenResponseCleanerStripsClutter()
{
    var cleaned = SpeechOutputCleaner.Clean(
        """
        # Heading
        Source: local test
        Visit https://example.com/details [1]
        ```csharp
        Console.WriteLine("nope");
        ```
           at Fake.Stack.Trace()
        Final answer.
        """);

    Equal(false, cleaned.Contains("https://", StringComparison.OrdinalIgnoreCase));
    Equal(false, cleaned.Contains("```", StringComparison.OrdinalIgnoreCase));
    Equal(false, cleaned.Contains("Source:", StringComparison.OrdinalIgnoreCase));
    Contains("Code block omitted", cleaned);
    Contains("Final answer.", cleaned);
    return Task.CompletedTask;
}

static Task TestVoiceRiskyCommandRequiresVisibleConfirmation()
{
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("delete my reminder for tomorrow"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("run this PowerShell command"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("switch to the 32b model"));
    Equal(false, VoiceCommandSafety.RequiresVisibleConfirmation("what is the capital of Alabama"));
    return Task.CompletedTask;
}

static async Task TestVoiceOriginCorrectionQueueMetadata()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var store = new FileCorrectionQueueStore(directory);
    var queue = new CorrectionQueueService(store);
    var profile = CreateRuntimeOptions("fake-local-model").ToModelProfile(isLastKnownGood: true);
    var voice = new VoiceTurnMetadata(
        VoiceInputOrigin.Voice,
        Transcript: "What did I say?",
        SpeechToTextProvider: "Fake local STT",
        SpeechToTextMode: "unit-test",
        TextToSpeechProvider: "Fake local TTS",
        TextToSpeechVoice: "fake-voice",
        RawAudioRetained: false);

    var report = await queue.FlagIncorrectAsync(
        conversationId: "conv_voice",
        userMessageId: "msg_user_voice",
        assistantMessageId: "msg_assistant_voice",
        question: "What did I say?",
        answer: "You asked a question.",
        modelProfile: profile,
        answerEvidenceStatus: EvidenceStatus.Unverified,
        category: CorrectionCategory.Other,
        userNote: "Voice metadata check.",
        voiceMetadata: voice,
        cancellationToken: CancellationToken.None);

    var stored = (await store.ListAsync(CancellationToken.None)).Single(item => item.Id == report.Id);

    Equal(VoiceInputOrigin.Voice, stored.InputOrigin);
    Equal("What did I say?", stored.VoiceTranscript);
    Equal("Fake local STT", stored.SpeechToTextProvider);
    Equal("unit-test", stored.SpeechToTextMode);
    Equal("Fake local TTS", stored.TextToSpeechProvider);
    Equal("fake-voice", stored.TextToSpeechVoice);
    Equal(false, stored.RawAudioRetained);
}

static OpenAiCompatibleRuntimeOptions CreateRuntimeOptions(string model, bool supportsVision = false) =>
    new(
        Enabled: true,
        Endpoint: new Uri("http://127.0.0.1:11434/v1/"),
        Model: model,
        DisplayName: $"Local {model}",
        Family: "fake",
        Size: "tiny",
        Quantization: "Q4",
        ContextTokens: 4096,
        OutputTokenLimit: 32,
        Temperature: 0.2,
        TopP: null,
        StreamingEnabled: true,
        SupportsVision: supportsVision,
        SupportsToolCalls: false,
        AllowPrivateLanEndpoint: false);

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }
}

static void NotNull(object? value, string message)
{
    if (value is null)
    {
        throw new InvalidOperationException(message);
    }
}

static void Contains(string expectedFragment, string actual)
{
    if (!actual.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Expected '{actual}' to contain '{expectedFragment}'.");
    }
}

static void ThrowsInvalidOperation(Action action)
{
    try
    {
        action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
}

static async Task ThrowsInvalidOperationAsync(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException("Expected InvalidOperationException was not thrown.");
}

static async Task RunRealRuntimeValidationAsync()
{
    var endpoint = new Uri(Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_ENDPOINT") ?? "http://127.0.0.1:11434/v1/");
    var model = Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_MODEL") ?? "qwen3:14b";
    var dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ali",
        "BootstrapData");

    var options = new OpenAiCompatibleRuntimeOptions(
        Enabled: true,
        Endpoint: endpoint,
        Model: model,
        DisplayName: $"Proof model {model}",
        Family: "Qwen",
        Size: "14B",
        Quantization: "Ollama package default",
        ContextTokens: 4096,
        OutputTokenLimit: 256,
        Temperature: 0.2,
        TopP: 0.9,
        StreamingEnabled: true,
        SupportsVision: false,
        SupportsToolCalls: false,
        AllowPrivateLanEndpoint: false);

    RuntimeSettingsStore.Save(dataRoot, options);

    var fallback = new DevelopmentLocalModelRuntime();
    var candidate = new OpenAiCompatibleLocalModelRuntime(new HttpClient(), options);
    var runtime = new SafeActivatingLocalRuntime(fallback, candidate);

    var health = await runtime.CheckCandidateAsync(CancellationToken.None);
    Console.WriteLine($"HEALTH_SUCCESS={health.Succeeded}");
    Console.WriteLine($"HEALTH_SUMMARY={health.Summary}");
    Console.WriteLine($"HEALTH_ENDPOINT={health.Endpoint}");
    Console.WriteLine($"HEALTH_MODEL={health.ModelPackageId}");
    Console.WriteLine($"HEALTH_ELAPSED_MS={health.Elapsed.TotalMilliseconds:N0}");
    Console.WriteLine($"HEALTH_STREAMING={health.StreamingSupported}");

    if (!health.Succeeded)
    {
        Environment.ExitCode = 2;
        return;
    }

    Console.WriteLine($"ACTIVE_BEFORE_ACTIVATE={runtime.ActiveProfile.PackageId}");
    var activated = runtime.ActivateLastHealthChecked();
    Console.WriteLine($"ACTIVATED={activated}");
    Console.WriteLine($"ACTIVE_AFTER_ACTIVATE={runtime.ActiveProfile.PackageId}");

    var prompt = "What model are you using? Answer in one short sentence.";
    var answer = await StreamToStringAsync(runtime, prompt, CancellationToken.None);
    Console.WriteLine($"PROMPT={prompt}");
    Console.WriteLine($"ANSWER_LENGTH={answer.Length}");
    Console.WriteLine($"ANSWER={answer.ReplaceLineEndings(" ").Trim()}");

    var cancelResult = await ValidateCancellationAfterFirstTokenAsync(runtime);
    Console.WriteLine($"CANCEL_AFTER_FIRST_TOKEN={cancelResult}");

    var correctionStore = new FileCorrectionQueueStore(dataRoot);
    var queue = new CorrectionQueueService(correctionStore);
    var report = await queue.FlagIncorrectAsync(
        conversationId: "real_runtime_validation",
        userMessageId: "real_user_model_question",
        assistantMessageId: "real_assistant_model_answer",
        question: prompt,
        answer: answer,
        modelProfile: runtime.ActiveProfile,
        answerEvidenceStatus: EvidenceStatus.Unverified,
        category: CorrectionCategory.Other,
        userNote: "Real local runtime heartbeat correction queue validation.",
        cancellationToken: CancellationToken.None);

    var reports = await correctionStore.ListAsync(CancellationToken.None);
    var stored = reports.FirstOrDefault(item => item.Id == report.Id);
    Console.WriteLine($"CORRECTION_STORED={stored is not null}");
    Console.WriteLine($"CORRECTION_ID={report.Id}");
    Console.WriteLine($"CORRECTION_MODEL={stored?.ModelPackage}");
    Console.WriteLine($"CORRECTION_ENDPOINT={stored?.RuntimeEndpoint}");
    Console.WriteLine($"CORRECTION_CONTEXT={stored?.ContextTokens}");
    Console.WriteLine($"CORRECTION_OUTPUT_LIMIT={stored?.OutputTokenLimit}");
    Console.WriteLine($"CORRECTION_TEMPERATURE={stored?.Temperature}");
    Console.WriteLine($"CORRECTION_STREAMING={stored?.StreamingEnabled}");
}

static async Task<string> StreamToStringAsync(
    ILocalModelRuntime runtime,
    string prompt,
    CancellationToken cancellationToken)
{
    var chunks = new List<string>();

    await foreach (var token in runtime.StreamChatAsync(
                       new ChatRequest(
                           ConversationId: "real_runtime_validation",
                           UserMessageId: $"msg_{Guid.NewGuid():N}",
                           UserText: prompt,
                           History: Array.Empty<ChatMessage>()),
                       cancellationToken))
    {
        chunks.Add(token.Text);
    }

    return string.Concat(chunks);
}

static async Task<bool> ValidateCancellationAfterFirstTokenAsync(ILocalModelRuntime runtime)
{
    using var cancellation = new CancellationTokenSource();
    var sawToken = false;

    try
    {
        await foreach (var token in runtime.StreamChatAsync(
                           new ChatRequest(
                               ConversationId: "real_runtime_validation",
                               UserMessageId: $"msg_{Guid.NewGuid():N}",
                               UserText: "Count slowly from one to twenty, one number per line.",
                               History: Array.Empty<ChatMessage>()),
                           cancellation.Token))
        {
            if (!string.IsNullOrEmpty(token.Text))
            {
                sawToken = true;
                cancellation.Cancel();
            }
        }
    }
    catch (OperationCanceledException)
    {
        return sawToken;
    }

    return sawToken && cancellation.IsCancellationRequested;
}

static async Task RunRealVisionValidationAsync()
{
    var endpoint = new Uri(Environment.GetEnvironmentVariable("ALI_REAL_VISION_ENDPOINT") ?? "http://127.0.0.1:11434/v1/");
    var model = Environment.GetEnvironmentVariable("ALI_REAL_VISION_MODEL") ?? "qwen3-vl:8b";
    var dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ali",
        "BootstrapData");

    var options = new OpenAiCompatibleRuntimeOptions(
        Enabled: true,
        Endpoint: endpoint,
        Model: model,
        DisplayName: $"Proof vision model {model}",
        Family: "Qwen VL",
        Size: "8B",
        Quantization: "Ollama package default",
        ContextTokens: 4096,
        OutputTokenLimit: 512,
        Temperature: 0.2,
        TopP: 0.9,
        StreamingEnabled: true,
        SupportsVision: true,
        SupportsToolCalls: false,
        AllowPrivateLanEndpoint: false);

    RuntimeSettingsStore.Save(dataRoot, options);

    var fallback = new DevelopmentLocalModelRuntime();
    var candidate = new OpenAiCompatibleLocalModelRuntime(new HttpClient(), options);
    var runtime = new SafeActivatingLocalRuntime(fallback, candidate);

    var health = await runtime.CheckCandidateAsync(CancellationToken.None);
    Console.WriteLine($"VISION_HEALTH_SUCCESS={health.Succeeded}");
    Console.WriteLine($"VISION_HEALTH_SUMMARY={health.Summary}");
    Console.WriteLine($"VISION_HEALTH_ENDPOINT={health.Endpoint}");
    Console.WriteLine($"VISION_HEALTH_MODEL={health.ModelPackageId}");
    Console.WriteLine($"VISION_HEALTH_ELAPSED_MS={health.Elapsed.TotalMilliseconds:N0}");
    Console.WriteLine($"VISION_HEALTH_STREAMING={health.StreamingSupported}");

    if (!health.Succeeded)
    {
        Environment.ExitCode = 3;
        return;
    }

    Console.WriteLine($"VISION_ACTIVE_BEFORE_ACTIVATE={runtime.ActiveProfile.PackageId}");
    var activated = runtime.ActivateLastHealthChecked();
    Console.WriteLine($"VISION_ACTIVATED={activated}");
    Console.WriteLine($"VISION_ACTIVE_AFTER_ACTIVATE={runtime.ActiveProfile.PackageId}");

    var prompt = "Describe the attached image in one short phrase. /no_think";
    var request = new ChatRequest(
        ConversationId: "real_vision_validation",
        UserMessageId: "real_vision_user",
        UserText: prompt,
        History: Array.Empty<ChatMessage>())
    {
        Attachments = new[]
        {
            new ChatAttachment(
                Id: "real_vision_red_pixel",
                Kind: AttachmentKind.Image,
                FileName: "red-pixel.png",
                ContentType: "image/png",
                Base64Data: "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAFgwJ/luzQ8wAAAABJRU5ErkJggg==",
                RetainAfterSession: false,
                CreatedAt: DateTimeOffset.UtcNow)
        }
    };

    var chunks = new List<string>();
    await foreach (var token in runtime.StreamChatAsync(request, CancellationToken.None))
    {
        chunks.Add(token.Text);
    }

    var answer = string.Concat(chunks).ReplaceLineEndings(" ").Trim();
    Console.WriteLine($"VISION_PROMPT={prompt}");
    Console.WriteLine($"VISION_ANSWER_LENGTH={answer.Length}");
    Console.WriteLine($"VISION_ANSWER={answer}");
}

internal sealed class FakeOpenAiHandler(string model) : HttpMessageHandler
{
    public int ImageRequestCount { get; private set; }

    public string LastChatBody { get; private set; } = string.Empty;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var path = request.RequestUri?.AbsolutePath ?? string.Empty;

        if (path.EndsWith("/models", StringComparison.OrdinalIgnoreCase))
        {
            return JsonResponse($$"""{"data":[{"id":"{{model}}"}]}""");
        }

        if (!path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found")
            };
        }

        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        LastChatBody = body;

        if (body.Contains("image_url", StringComparison.OrdinalIgnoreCase))
        {
            ImageRequestCount++;
        }

        if (body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n" +
                    "data: [DONE]\n\n")
            };
        }

        return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}

internal sealed class FakeSpeechToTextProvider(string transcript, bool fail = false) : ISpeechToTextProvider
{
    public string ProviderName => "Fake local STT";

    public string Mode => "unit-test";

    public bool IsConfigured => true;

    public Task<SpeechTranscript> TranscribeAsync(VoiceAudioInput audioInput, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fail)
        {
            throw new InvalidOperationException("Fake local STT failure.");
        }

        return Task.FromResult(new SpeechTranscript(transcript, ProviderName, Mode, DateTimeOffset.UtcNow));
    }
}

internal sealed class FakeTextToSpeechProvider : ITextToSpeechProvider
{
    public string ProviderName => "Fake local TTS";

    public string VoiceId => "fake-voice";

    public bool IsConfigured => true;

    public Task<SpeechSynthesisResult> SynthesizeAsync(
        string text,
        VoiceSettings settings,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SpeechSynthesisResult(
            "fake.wav",
            ProviderName,
            settings.VoiceId,
            settings.RetainAudio,
            DateTimeOffset.UtcNow));
    }
}

internal sealed class FakeSpeechPlayer : ISpeechPlayer
{
    public bool IsSpeaking { get; private set; }

    public bool StopRequested { get; private set; }

    public Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        IsSpeaking = true;
        return Task.Run(
            async () =>
            {
                while (!StopRequested && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(10, CancellationToken.None);
                }

                IsSpeaking = false;
            },
            CancellationToken.None);
    }

    public void Stop()
    {
        StopRequested = true;
        IsSpeaking = false;
    }
}
