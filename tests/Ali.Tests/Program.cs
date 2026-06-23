using System.Globalization;
using Ali.Core.Conversations;
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

if (args.Contains("--real-voice", StringComparer.OrdinalIgnoreCase))
{
    await RunRealVoiceValidationAsync();
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
    ("health check retries empty non-streaming probe", TestHealthCheckRetriesEmptyNonStreamingProbe),
    ("vision health check sends image content", TestVisionHealthCheckSendsImageContent),
    ("OpenAI stream parser extracts content delta", TestOpenAiStreamParserExtractsContentDelta),
    ("OpenAI response parser extracts message content", TestOpenAiResponseParserExtractsMessageContent),
    ("runtime cancellation path throws OperationCanceledException", TestRuntimeCancellationPath),
    ("correction queue stores runtime snapshot", TestCorrectionQueueStoresRuntimeSnapshot),
    ("conversation store saves and reloads messages", TestConversationStoreSavesAndReloadsMessages),
    ("conversation store lists recents newest first", TestConversationStoreListsRecentsNewestFirst),
    ("conversation search finds title and message text", TestConversationSearchFindsTitleAndMessageText),
    ("conversation search does not mutate storage", TestConversationSearchDoesNotMutateStorage),
    ("conversation delete removes one saved chat", TestConversationDeleteRemovesOneSavedChat),
    ("conversation erase preserves settings and resources", TestConversationErasePreservesSettingsAndResources),
    ("conversation rename handles blank and duplicate titles", TestConversationRenameHandlesBlankAndDuplicateTitles),
    ("conversation missing index rebuilds from files", TestConversationMissingIndexRebuildsFromFiles),
    ("conversation corrupt file does not crash listing", TestConversationCorruptFileDoesNotCrashListing),
    ("conversation attachment raw data is not persisted", TestConversationAttachmentRawDataIsNotPersisted),
    ("conversation title comes from first message", TestConversationTitleComesFromFirstMessage),
    ("voice audio input is temporary by default", TestVoiceAudioInputIsTemporaryByDefault),
    ("voice transcript becomes user chat text", TestVoiceTranscriptBecomesUserChatText),
    ("speech tool policy refuses cloud STT endpoint", TestSpeechPolicyRefusesCloudSttEndpoint),
    ("speech tool policy refuses cloud TTS endpoint", TestSpeechPolicyRefusesCloudTtsEndpoint),
    ("local STT fake success path", TestLocalSttFakeSuccessPath),
    ("local STT fake failure path", TestLocalSttFakeFailurePath),
    ("local TTS fake success path", TestLocalTtsFakeSuccessPath),
    ("voice transcript routing keeps dictation in composer when voice mode off", TestVoiceTranscriptRoutingKeepsDictationInComposerWhenVoiceModeOff),
    ("voice transcript routing auto sends only when voice mode on", TestVoiceTranscriptRoutingAutoSendsOnlyWhenVoiceModeOn),
    ("speech player stop cancels playback", TestSpeechPlayerStopCancelsPlayback),
    ("spoken response cleaner strips clutter", TestSpokenResponseCleanerStripsClutter),
    ("voice settings persist microphone and preset", TestVoiceSettingsPersistMicrophoneAndPreset),
    ("missing saved microphone warns and falls back", TestMissingSavedMicrophoneWarnsAndFallsBack),
    ("input channel catalog supports Scarlett-style inputs", TestInputChannelCatalogSupportsScarlettInputs),
    ("diagnostic sample service records plays and deletes", TestDiagnosticSampleServiceRecordsPlaysAndDeletes),
    ("voice calibration evaluator keeps action gated", TestVoiceCalibrationEvaluatorKeepsActionGated),
    ("voice audio normalizer raises quiet audio", TestVoiceAudioNormalizerRaisesQuietAudio),
    ("voice input level classifier detects silence good and clipping", TestVoiceInputLevelClassifier),
    ("voice capture safety rejects bad audio levels", TestVoiceCaptureSafetyRejectsBadAudioLevels),
    ("spectrum analyzer emits live bars", TestSpectrumAnalyzerEmitsLiveBars),
    ("speech transcript guard rejects suspicious text", TestSpeechTranscriptGuardRejectsSuspiciousText),
    ("voice risky command requires visible confirmation", TestVoiceRiskyCommandRequiresVisibleConfirmation),
    ("edited voice dictation preserves raw transcript metadata", TestEditedVoiceDictationPreservesRawTranscriptMetadata),
    ("local STT missing model path produces explicit error", TestLocalSttMissingModelPathProducesExplicitError),
    ("local TTS missing voice model produces explicit error", TestLocalTtsMissingVoiceModelProducesExplicitError),
    ("local TTS voice mismatch is rejected", TestLocalTtsVoiceMismatchIsRejected),
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

static async Task TestHealthCheckRetriesEmptyNonStreamingProbe()
{
    var options = CreateRuntimeOptions("fake-local-model");
    var handler = new FlakyHealthProbeHandler(options.Model);
    var runtime = new OpenAiCompatibleLocalModelRuntime(new HttpClient(handler), options);

    var health = await runtime.CheckHealthAsync(CancellationToken.None);

    Equal(true, health.Succeeded);
    Equal(2, handler.NonStreamingPromptCount);
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

static Task TestConversationStoreSavesAndReloadsMessages()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    var conversation = CreateStoredConversation("conv_reload", "Factory Safe", "How safe are you?", "I need receipts.");

    store.Save(conversation);
    var loaded = store.Load("conv_reload");

    NotNull(loaded, "Conversation should reload from disk.");
    Equal("Factory Safe", loaded!.Title);
    Equal(2, loaded.Messages.Count);
    Equal("How safe are you?", loaded.Messages[0].Text);
    Equal(ChatRole.Assistant, loaded.Messages[1].Role);
    return Task.CompletedTask;
}

static Task TestConversationStoreListsRecentsNewestFirst()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    var older = CreateStoredConversation("conv_old", "Old", "old question", "old answer", DateTimeOffset.UtcNow.AddMinutes(-10));
    var newer = CreateStoredConversation("conv_new", "New", "new question", "new answer", DateTimeOffset.UtcNow);

    store.Save(older);
    store.Save(newer);

    var recents = store.ListSummaries().Conversations;

    Equal("conv_new", recents[0].ConversationId);
    Equal("conv_old", recents[1].ConversationId);
    return Task.CompletedTask;
}

static Task TestConversationSearchFindsTitleAndMessageText()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_title", "Scarlett Setup", "audio setup", "answer"));
    store.Save(CreateStoredConversation("conv_body", "Different Title", "Find the hidden microphone clue", "answer"));

    var titleResults = store.Search("scarlett").Conversations;
    var bodyResults = store.Search("hidden microphone").Conversations;
    var emptyResults = store.Search(string.Empty).Conversations;

    Equal(1, titleResults.Count);
    Equal("conv_title", titleResults[0].ConversationId);
    Equal(1, bodyResults.Count);
    Equal("conv_body", bodyResults[0].ConversationId);
    Equal(2, emptyResults.Count);
    return Task.CompletedTask;
}

static Task TestConversationSearchDoesNotMutateStorage()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_search", "Stable", "search me", "answer"));
    var before = File.ReadAllText(store.IndexPath);

    _ = store.Search("search");
    var after = File.ReadAllText(store.IndexPath);

    Equal(before, after);
    return Task.CompletedTask;
}

static Task TestConversationDeleteRemovesOneSavedChat()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_keep", "Keep", "keep", "answer"));
    store.Save(CreateStoredConversation("conv_delete", "Delete", "delete", "answer"));

    Equal(true, store.Delete("conv_delete"));
    Equal(null, store.Load("conv_delete"));
    NotNull(store.Load("conv_keep"), "Other conversations should remain.");
    Equal(1, store.ListSummaries().Conversations.Count);
    return Task.CompletedTask;
}

static Task TestConversationErasePreservesSettingsAndResources()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_one", "One", "question", "answer"));
    var settingsPath = Path.Combine(directory, "BootstrapData", "runtime-settings.json");
    var voiceResourcePath = Path.Combine(directory, "lib", "voice", "README.md");
    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
    Directory.CreateDirectory(Path.GetDirectoryName(voiceResourcePath)!);
    File.WriteAllText(settingsPath, "settings stay");
    File.WriteAllText(voiceResourcePath, "voice resources stay");

    var result = store.EraseAll();

    Equal(1, result.DeletedConversationCount);
    Equal(true, File.Exists(settingsPath));
    Equal(true, File.Exists(voiceResourcePath));
    Equal(0, store.ListSummaries().Conversations.Count);
    return Task.CompletedTask;
}

static Task TestConversationRenameHandlesBlankAndDuplicateTitles()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_one", "One", "question one", "answer"));
    store.Save(CreateStoredConversation("conv_two", "Two", "question two", "answer"));

    var blankRename = store.Rename("conv_one", "   ");
    var duplicateRename = store.Rename("conv_two", "Untitled chat");

    NotNull(blankRename, "Blank rename should return a safe title.");
    NotNull(duplicateRename, "Duplicate rename should remain safe because ids are stable.");
    Equal("Untitled chat", blankRename!.Title);
    Equal("Untitled chat", duplicateRename!.Title);
    Equal(2, store.ListSummaries().Conversations.Count);
    return Task.CompletedTask;
}

static Task TestConversationMissingIndexRebuildsFromFiles()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_rebuild", "Rebuild", "question", "answer"));
    File.Delete(store.IndexPath);

    var rebuilt = new FileConversationStore(directory).ListSummaries();

    Equal(1, rebuilt.Conversations.Count);
    Equal("conv_rebuild", rebuilt.Conversations[0].ConversationId);
    Equal(true, File.Exists(store.IndexPath));
    return Task.CompletedTask;
}

static Task TestConversationCorruptFileDoesNotCrashListing()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    store.Save(CreateStoredConversation("conv_good", "Good", "question", "answer"));
    File.WriteAllText(Path.Combine(store.ConversationsDirectory, "conv_bad.json"), "{ definitely not json");
    File.Delete(store.IndexPath);

    var listed = store.ListSummaries();

    Equal(1, listed.Conversations.Count);
    Equal("conv_good", listed.Conversations[0].ConversationId);
    Equal(true, listed.Warnings.Count > 0);
    return Task.CompletedTask;
}

static Task TestConversationAttachmentRawDataIsNotPersisted()
{
    var directory = NewTestDirectory();
    var store = new FileConversationStore(directory);
    var createdAt = DateTimeOffset.UtcNow;
    var user = new StoredChatMessage(
        "msg_user",
        "conv_image",
        ChatRole.User,
        "Please read this screenshot.",
        createdAt,
        ChatMessageOrigin.Image,
        EvidenceStatus.Verified,
        new[]
        {
            new StoredAttachmentMetadata(
                "att_1",
                AttachmentKind.Image,
                "screen.png",
                "image/png",
                RetainAfterSession: false,
                createdAt)
        });
    var assistant = new StoredChatMessage(
        "msg_assistant",
        "conv_image",
        ChatRole.Assistant,
        "I can only report what I can verify.",
        createdAt.AddSeconds(1),
        ChatMessageOrigin.Typed,
        EvidenceStatus.Unknown,
        SourceAttachmentCount: 1,
        SourceUserMessageId: "msg_user",
        SourceQuestion: user.Text);

    store.Save(new StoredConversation("conv_image", "Image", createdAt, createdAt.AddSeconds(1), new[] { user, assistant }));
    var savedJson = File.ReadAllText(Path.Combine(store.ConversationsDirectory, "conv_image.json"));

    Contains("screen.png", savedJson);
    Equal(false, savedJson.Contains("base64Data", StringComparison.OrdinalIgnoreCase));
    Equal(false, savedJson.Contains("RAW_IMAGE_BYTES", StringComparison.OrdinalIgnoreCase));
    return Task.CompletedTask;
}

static Task TestConversationTitleComesFromFirstMessage()
{
    var title = ConversationTitleFactory.CreateFromFirstMessage(
        "  Please help me debug this local WPF settings dropdown because it stopped populating after the refactor.  ");

    Contains("Please help me debug", title);
    Equal(true, title.Length <= 64);
    return Task.CompletedTask;
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

static Task TestVoiceTranscriptRoutingKeepsDictationInComposerWhenVoiceModeOff()
{
    var decision = VoiceTranscriptRouting.Decide(voiceModeEnabled: false);

    Equal(true, decision.PlaceTranscriptInComposer);
    Equal(false, decision.SendAutomatically);
    Contains("composer", decision.Description);
    return Task.CompletedTask;
}

static Task TestVoiceTranscriptRoutingAutoSendsOnlyWhenVoiceModeOn()
{
    var decision = VoiceTranscriptRouting.Decide(voiceModeEnabled: true);

    Equal(false, decision.PlaceTranscriptInComposer);
    Equal(true, decision.SendAutomatically);
    Contains("hands-free", decision.Description);
    return Task.CompletedTask;
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

static Task TestVoiceSettingsPersistMicrophoneAndPreset()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var settings = new VoiceRuntimeSettings(
        SelectedInputDeviceNumber: 3,
        SelectedInputDeviceName: "Headset Mic",
        SelectedOutputDeviceNumber: -1,
        SelectedOutputDeviceName: "Default playback device",
        LastSuccessfulSttDeviceNumber: 3,
        LastSuccessfulSttDeviceName: "Headset Mic",
        LastSuccessfulTtsDeviceNumber: -1,
        LastSuccessfulTtsDeviceName: "Default playback device",
        SelectedInputPreset: VoiceInputPreset.HeadsetMic,
        SelectedInputChannelMode: nameof(InputChannelMode.Input2Right),
        ExtraInputGainDb: 6,
        NormalizeBeforeStt: true,
        RetainDebugAudio: true,
        AutoSendVoiceTranscripts: true,
        WhisperExecutablePath: @"C:\Ali\lib\voice\whisper.exe",
        WhisperModelPath: @"C:\Ali\lib\voice\faster-whisper",
        PiperExecutablePath: @"C:\Ali\lib\voice\piper.exe",
        PiperModelPath: @"C:\Ali\lib\voice\en_US.onnx",
        PiperVoiceId: "en_US-test");

    VoiceRuntimeSettingsStore.Save(directory, settings);
    var loaded = VoiceRuntimeSettingsStore.LoadOrDefault(directory);

    Equal(3, loaded.SelectedInputDeviceNumber);
    Equal("Headset Mic", loaded.SelectedInputDeviceName);
    Equal(VoiceInputPreset.HeadsetMic, loaded.SelectedInputPreset);
    Equal(nameof(InputChannelMode.Input2Right), loaded.SelectedInputChannelMode);
    Equal(6d, loaded.ExtraInputGainDb);
    Equal(true, loaded.NormalizeBeforeStt);
    Equal(true, loaded.RetainDebugAudio);
    Equal(true, loaded.AutoSendVoiceTranscripts);
    Equal(@"C:\Ali\lib\voice\whisper.exe", loaded.WhisperExecutablePath);
    Equal(@"C:\Ali\lib\voice\en_US.onnx", loaded.PiperModelPath);
    Equal("en_US-test", loaded.PiperVoiceId);
    Equal(3, loaded.LastSuccessfulSttDeviceNumber);
    return Task.CompletedTask;
}

static Task TestMissingSavedMicrophoneWarnsAndFallsBack()
{
    var settings = new VoiceRuntimeSettings(
        SelectedInputDeviceNumber: 7,
        SelectedInputDeviceName: "Missing Mic");
    var devices = new[]
    {
        new AudioInputDevice(1, "Available Mic")
    };

    var resolved = VoiceDeviceSelection.ResolveInput(settings, devices);

    Equal(1, resolved.DeviceNumber);
    Equal(false, resolved.RestoredSavedDevice);
    Contains("Missing Mic", resolved.Warning ?? string.Empty);
    return Task.CompletedTask;
}

static Task TestInputChannelCatalogSupportsScarlettInputs()
{
    var labels = InputChannelModeCatalog.CreateLabels(channelCount: 2);

    Equal(3, labels.Count);
    Equal(InputChannelModeCatalog.MonoSumLabel, labels[0]);
    Equal("Input 1 L", labels[1]);
    Equal("Input 2 R", labels[2]);
    Equal(InputChannelMode.HighestEnergy, InputChannelModeCatalog.FromLabel("Auto strongest"));
    Equal(InputChannelMode.Input2Right, InputChannelModeCatalog.FromLabel("Input 2 R"));
    Equal(1, InputChannelModeCatalog.ChannelIndex(InputChannelMode.Input2Right));
    return Task.CompletedTask;
}

static async Task TestDiagnosticSampleServiceRecordsPlaysAndDeletes()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var recorder = new FakeVoiceRecorder();
    var player = new FakeSpeechPlayer(completeImmediately: true);
    var service = new VoiceDiagnosticSampleService(
        recorder,
        player,
        (filePath, deviceNumber, deviceName) => VoiceAudioFileAnalyzer.AnalyzeWaveAudio(filePath, deviceNumber, deviceName));

    var sample = await service.RecordSampleAsync(
        directory,
        TimeSpan.FromMilliseconds(1),
        inputDeviceNumber: 2,
        inputDeviceName: "Scarlett 2i2",
        channelMode: InputChannelMode.Input2Right,
        inputPreset: VoiceInputPreset.HeadsetMic,
        extraGainDb: 6,
        normalizeBeforeStt: false,
        retainDebugAudio: false,
        cancellationToken: CancellationToken.None);

    Equal(true, recorder.Started);
    Equal(true, File.Exists(sample.AudioInput.FilePath));
    Equal("Scarlett 2i2", sample.InputDeviceName);
    Equal("Input 2 R", sample.InputChannelLabel);
    Equal(6d, sample.ExtraGainDb);

    await service.PlaySampleAsync(sample, CancellationToken.None);
    Equal(true, player.PlayWasCalled);

    service.DeleteSample(sample);
    Equal(false, File.Exists(sample.AudioInput.FilePath));
}

static Task TestVoiceCalibrationEvaluatorKeepsActionGated()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var wavPath = Path.Combine(directory, "calibration.wav");
    TestAudioFiles.WritePcm16Wave(wavPath, amplitude: 0.2d);
    var diagnostics = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(wavPath, 1, "Test Mic");
    var sample = new VoiceDiagnosticSample(
        new VoiceAudioInput(wavPath, "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow),
        diagnostics,
        InputDeviceNumber: 1,
        InputDeviceName: "Test Mic",
        ChannelMode: InputChannelMode.HighestEnergy,
        InputChannelLabel: InputChannelModeCatalog.HighestEnergyLabel,
        InputPreset: VoiceInputPreset.HeadsetMic,
        ExtraGainDb: 3,
        NormalizeBeforeStt: true,
        RetainDebugAudio: false);

    var transcript = new SpeechTranscript("Ali this is a microphone test", "Fake local STT", "unit-test", DateTimeOffset.UtcNow);
    var guard = SpeechTranscriptGuard.Evaluate(transcript.Text, requireAssistantName: true);
    var result = VoiceCalibrationEvaluator.Evaluate(sample, transcript, guard);

    Equal(true, result.Accepted);
    Equal(true, result.SpeechDetected);
    Equal(false, result.Clipping);
    Equal("Ali this is a microphone test", result.Transcript);
    return Task.CompletedTask;
}

static Task TestVoiceAudioNormalizerRaisesQuietAudio()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var wavPath = Path.Combine(directory, "quiet.wav");
    TestAudioFiles.WritePcm16Wave(wavPath, amplitude: 0.01d);
    var before = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(wavPath);

    var result = VoiceAudioNormalizer.NormalizePcm16WaveInPlace(wavPath, targetRms: 0.06d);
    var after = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(wavPath);

    Equal(true, result.Applied);
    Equal(true, after.Level.Rms > before.Level.Rms);
    Equal(true, after.Level.Peak <= 0.92d);
    return Task.CompletedTask;
}

static Task TestVoiceInputLevelClassifier()
{
    Equal(VoiceInputLevelState.Silence, VoiceInputLevelAnalyzer.Classify(rms: 0.0001, peak: 0.001));
    Equal(VoiceInputLevelState.TooQuiet, VoiceInputLevelAnalyzer.Classify(rms: 0.006, peak: 0.04));
    Equal(VoiceInputLevelState.Good, VoiceInputLevelAnalyzer.Classify(rms: 0.08, peak: 0.30));
    Equal(VoiceInputLevelState.Clipping, VoiceInputLevelAnalyzer.Classify(rms: 0.25, peak: 0.99));
    return Task.CompletedTask;
}

static Task TestVoiceCaptureSafetyRejectsBadAudioLevels()
{
    var silence = CreateCaptureDiagnostics(rms: 0.0001, peak: 0.001);
    var tooQuiet = CreateCaptureDiagnostics(rms: 0.006, peak: 0.04);
    var good = CreateCaptureDiagnostics(rms: 0.08, peak: 0.30);
    var clipping = CreateCaptureDiagnostics(rms: 0.25, peak: 0.99);

    Equal(false, VoiceCaptureSafetyGate.Evaluate(silence).Accepted);
    Equal(VoiceCaptureSafetyGate.Silence, VoiceCaptureSafetyGate.Evaluate(silence).Reason);
    Equal(false, VoiceCaptureSafetyGate.Evaluate(tooQuiet).Accepted);
    Equal(VoiceCaptureSafetyGate.TooQuiet, VoiceCaptureSafetyGate.Evaluate(tooQuiet).Reason);
    Equal(true, VoiceCaptureSafetyGate.Evaluate(good).Accepted);
    Equal(false, VoiceCaptureSafetyGate.Evaluate(clipping).Accepted);
    Equal(VoiceCaptureSafetyGate.Clipping, VoiceCaptureSafetyGate.Evaluate(clipping).Reason);
    return Task.CompletedTask;
}

static Task TestSpectrumAnalyzerEmitsLiveBars()
{
    var analyzer = new SpectrumAnalyzer();
    var samples = new float[4096];
    for (var index = 0; index < samples.Length; index++)
    {
        samples[index] = (float)(Math.Sin(2d * Math.PI * 440d * index / 44100d) * 0.5d);
    }

    var frame = analyzer.AddSamples(samples);

    Equal(SpectrumAnalyzer.BarCount, frame.Magnitudes.Length);
    Equal(true, frame.PeakLevel > 0.45d);
    Equal(true, frame.Magnitudes.Any(magnitude => magnitude > 0d));
    return Task.CompletedTask;
}

static Task TestSpeechTranscriptGuardRejectsSuspiciousText()
{
    var empty = SpeechTranscriptGuard.Evaluate("");
    var tooShort = SpeechTranscriptGuard.Evaluate("a");
    var repeated = SpeechTranscriptGuard.Evaluate("you you you you");
    var missingName = SpeechTranscriptGuard.Evaluate("what model are you using", requireAssistantName: true);

    Equal(false, empty.Accepted);
    Equal(SpeechTranscriptGuard.EmptyReason, empty.Reason);
    Equal(false, tooShort.Accepted);
    Equal(SpeechTranscriptGuard.TooShortReason, tooShort.Reason);
    Equal(false, repeated.Accepted);
    Equal(SpeechTranscriptGuard.RepeatedTextReason, repeated.Reason);
    Equal(true, SpeechTranscriptGuard.Evaluate("Ali what model are you using").Accepted);
    Equal(false, missingName.Accepted);
    Equal(SpeechTranscriptGuard.MissingAssistantNameReason, missingName.Reason);
    Equal(true, SpeechTranscriptGuard.Evaluate("Ali what model are you using", requireAssistantName: true).Accepted);
    return Task.CompletedTask;
}

static Task TestVoiceRiskyCommandRequiresVisibleConfirmation()
{
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("Ali, delete all my files."));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("run command prompt"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("delete my reminder for tomorrow"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("run this PowerShell command"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("use PowerShell to inspect the folder"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("install software for me"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("modify my calendar"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("change memory about my project"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("switch to the 32b model"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("send an email to Chris"));
    Equal(true, VoiceCommandSafety.RequiresVisibleConfirmation("rename this folder"));
    Equal(false, VoiceCommandSafety.RequiresVisibleConfirmation("what is the capital of Alabama"));
    return Task.CompletedTask;
}

static async Task TestEditedVoiceDictationPreservesRawTranscriptMetadata()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    var store = new FileCorrectionQueueStore(directory);
    var queue = new CorrectionQueueService(store);
    var profile = CreateRuntimeOptions("fake-local-model").ToModelProfile(isLastKnownGood: true);
    var rawTranscript = "Ali right the word blueberry";
    var editedSentText = "Ali write the word blueberry.";
    var voice = new VoiceTurnMetadata(
        VoiceInputOrigin.Voice,
        Transcript: rawTranscript,
        SpeechToTextProvider: "Fake local STT",
        SpeechToTextMode: "unit-test",
        TextToSpeechProvider: "Fake local TTS",
        TextToSpeechVoice: "fake-voice",
        RawAudioRetained: false,
        InputDeviceNumber: 0,
        InputDeviceName: "Focusrite input",
        InputChannelMode: InputChannelModeCatalog.ToLabel(InputChannelMode.Input1Left),
        InputPreset: VoiceInputPreset.HeadsetMic,
        ExtraInputGainDb: 6,
        NormalizeBeforeStt: true,
        SpeechToTextModel: "small.en",
        TextToSpeechModel: "en_US-hfc_female-medium.onnx",
        SuspiciousOrNoSpeech: false,
        RejectionReason: null,
        InputPeak: 0.22,
        InputRms: 0.07,
        InputLevelState: VoiceInputLevelState.Good.ToString());

    await queue.FlagIncorrectAsync(
        conversationId: "conv_voice_edit",
        userMessageId: "msg_user_voice_edit",
        assistantMessageId: "msg_assistant_voice_edit",
        question: editedSentText,
        answer: "blueberry",
        modelProfile: profile,
        answerEvidenceStatus: EvidenceStatus.Unverified,
        category: CorrectionCategory.Other,
        userNote: "Edited dictation metadata check.",
        voiceMetadata: voice,
        cancellationToken: CancellationToken.None);

    var stored = (await store.ListAsync(CancellationToken.None)).Single();

    Equal(editedSentText, stored.Question);
    Equal(rawTranscript, stored.VoiceTranscript);
    Equal("Fake local STT", stored.SpeechToTextProvider);
    Equal("small.en", stored.SpeechToTextModel);
    Equal(VoiceInputOrigin.Voice, stored.InputOrigin);
}

static async Task TestLocalSttMissingModelPathProducesExplicitError()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fakeExe = Path.Combine(directory, "python.exe");
    var audioPath = Path.Combine(directory, "voice.wav");
    await File.WriteAllTextAsync(fakeExe, "not really python");
    await File.WriteAllBytesAsync(audioPath, [0, 1, 2, 3]);

    var provider = new WhisperCliSpeechToTextProvider(new WhisperCliSpeechToTextOptions(
        fakeExe,
        Path.Combine(directory, "missing-whisper-root"),
        "\"wrapper.py\" --audio \"{audio}\" --model-root \"{model}\" --output-base \"{outputBase}\"",
        ".txt"));

    Equal(false, provider.IsConfigured);
    var ex = await ThrowsAsync<FileNotFoundException>(() => provider.TranscribeAsync(
        new VoiceAudioInput(audioPath, "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow),
        CancellationToken.None));
    Contains("Local STT model path was not found", ex.Message);
}

static async Task TestLocalTtsMissingVoiceModelProducesExplicitError()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fakeExe = Path.Combine(directory, "python.exe");
    await File.WriteAllTextAsync(fakeExe, "not really python");

    var provider = new PiperCliTextToSpeechProvider(new PiperCliTextToSpeechOptions(
        fakeExe,
        Path.Combine(directory, "missing-voice.onnx"),
        "missing-voice",
        "\"wrapper.py\" --model \"{model}\" --output \"{output}\"",
        directory));

    Equal(false, provider.IsConfigured);
    var ex = await ThrowsAsync<FileNotFoundException>(() => provider.SynthesizeAsync(
        "hello",
        new VoiceSettings("missing-voice", Rate: 1.0, RetainAudio: false),
        CancellationToken.None));
    Contains("Local TTS voice model was not found", ex.Message);
}

static async Task TestLocalTtsVoiceMismatchIsRejected()
{
    var directory = Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var fakeExe = Path.Combine(directory, "python.exe");
    var fakeModel = Path.Combine(directory, "en_US-hfc_female-medium.onnx");
    await File.WriteAllTextAsync(fakeExe, "not really python");
    await File.WriteAllTextAsync(fakeModel, "not really a voice model");

    var provider = new PiperCliTextToSpeechProvider(new PiperCliTextToSpeechOptions(
        fakeExe,
        fakeModel,
        "en_US-hfc_female-medium",
        "\"wrapper.py\" --model \"{model}\" --output \"{output}\"",
        directory));

    var ex = await ThrowsAsync<InvalidOperationException>(() => provider.SynthesizeAsync(
        "hello",
        new VoiceSettings("en_US-amy-low", Rate: 1.0, RetainAudio: false),
        CancellationToken.None));
    Contains("does not match configured Piper voice", ex.Message);
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
        RawAudioRetained: false,
        InputDeviceNumber: 3,
        InputDeviceName: "Headset Mic",
        InputChannelMode: InputChannelModeCatalog.HighestEnergyLabel,
        InputPreset: VoiceInputPreset.HeadsetMic,
        ExtraInputGainDb: 6,
        NormalizeBeforeStt: true,
        SpeechToTextModel: "small.en",
        TextToSpeechModel: "en_US-hfc_female-medium.onnx",
        SuspiciousOrNoSpeech: false,
        RejectionReason: null,
        InputPeak: 0.25,
        InputRms: 0.08,
        InputLevelState: VoiceInputLevelState.Good.ToString());

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
    Equal(3, stored.VoiceInputDeviceNumber);
    Equal("Headset Mic", stored.VoiceInputDeviceName);
    Equal(InputChannelModeCatalog.HighestEnergyLabel, stored.VoiceInputChannelMode);
    Equal(VoiceInputPreset.HeadsetMic, stored.VoiceInputPreset);
    Equal(6d, stored.VoiceExtraInputGainDb);
    Equal(true, stored.VoiceNormalizeBeforeStt);
    Equal("small.en", stored.SpeechToTextModel);
    Equal("en_US-hfc_female-medium.onnx", stored.TextToSpeechModel);
    Equal(false, stored.SuspiciousOrNoSpeech);
    Equal(null, stored.VoiceRejectionReason);
    Equal(0.25, stored.VoiceInputPeak);
    Equal(0.08, stored.VoiceInputRms);
    Equal(VoiceInputLevelState.Good.ToString(), stored.VoiceInputLevelState);
}

static VoiceCaptureDiagnostics CreateCaptureDiagnostics(double rms, double peak)
{
    var level = VoiceInputLevelAnalyzer.CreateSnapshot(
        deviceNumber: 2,
        deviceName: "Scarlett 2i2",
        sampleRate: 44100,
        channels: 1,
        rms,
        peak);

    return new VoiceCaptureDiagnostics(
        "voice.wav",
        DurationSeconds: 1.0,
        SampleRate: 44100,
        Channels: 1,
        RmsPcm: (int)(rms * short.MaxValue),
        PeakPcm: (int)(peak * short.MaxValue),
        level);
}

static string NewTestDirectory() =>
    Path.Combine(Path.GetTempPath(), "Ali.Tests", Guid.NewGuid().ToString("N"));

static StoredConversation CreateStoredConversation(
    string conversationId,
    string title,
    string question,
    string answer,
    DateTimeOffset? updatedAt = null)
{
    var createdAt = (updatedAt ?? DateTimeOffset.UtcNow).AddSeconds(-2);
    var userMessageId = $"{conversationId}_user";
    var assistantMessageId = $"{conversationId}_assistant";
    var messages = new[]
    {
        new StoredChatMessage(
            userMessageId,
            conversationId,
            ChatRole.User,
            question,
            createdAt,
            ChatMessageOrigin.Typed,
            EvidenceStatus.Verified),
        new StoredChatMessage(
            assistantMessageId,
            conversationId,
            ChatRole.Assistant,
            answer,
            createdAt.AddSeconds(1),
            ChatMessageOrigin.Typed,
            EvidenceStatus.Unknown,
            SourceUserMessageId: userMessageId,
            SourceQuestion: question)
    };

    return new StoredConversation(
        conversationId,
        title,
        createdAt,
        updatedAt ?? createdAt.AddSeconds(1),
        messages);
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

static async Task<TException> ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name} was not thrown.");
}

static async Task RunRealRuntimeValidationAsync()
{
    var endpoint = new Uri(Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_ENDPOINT") ?? "http://127.0.0.1:11434/v1/");
    var model = Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_MODEL") ?? "qwen3:8b";
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
        ContextTokens: 2048,
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

static async Task RunRealVoiceValidationAsync()
{
    var endpoint = new Uri(Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_ENDPOINT") ?? "http://127.0.0.1:11434/v1/");
    var model = Environment.GetEnvironmentVariable("ALI_REAL_RUNTIME_MODEL") ?? "qwen3:8b";
    var recordSeconds = ReadIntEnvironment("ALI_REAL_VOICE_RECORD_SECONDS", 5);
    var countdownSeconds = ReadIntEnvironment("ALI_REAL_VOICE_COUNTDOWN_SECONDS", 0);
    var retainDebugAudio = ReadBoolEnvironment("ALI_REAL_VOICE_RETAIN_AUDIO", false);
    var dspMode = Environment.GetEnvironmentVariable("ALI_REAL_VOICE_DSP_MODE") ?? "default";
    var dspBypassed = dspMode.Equals("bypass", StringComparison.OrdinalIgnoreCase)
        || dspMode.Equals("raw", StringComparison.OrdinalIgnoreCase);
    var voicePreset = VoiceInputPreset.Normalize(Environment.GetEnvironmentVariable("ALI_REAL_VOICE_PRESET"));
    var gainDb = ReadNullableDoubleEnvironment("ALI_REAL_VOICE_GAIN_DB");
    var voiceChannel = InputChannelModeCatalog.FromStorageValue(Environment.GetEnvironmentVariable("ALI_REAL_VOICE_CHANNEL"));
    var normalizeBeforeStt = ReadBoolEnvironment("ALI_REAL_VOICE_NORMALIZE", false);
    var dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Ali",
        "BootstrapData");

    Directory.CreateDirectory(dataRoot);

    var stt = new WhisperCliSpeechToTextProvider(WhisperCliSpeechToTextOptions.FromEnvironment());
    var tts = new PiperCliTextToSpeechProvider(PiperCliTextToSpeechOptions.FromEnvironment(dataRoot));

    Console.WriteLine($"VOICE_STT_PROVIDER={stt.ProviderName}");
    Console.WriteLine($"VOICE_STT_MODE={stt.Mode}");
    Console.WriteLine($"VOICE_STT_CONFIGURED={stt.IsConfigured}");
    Console.WriteLine($"VOICE_TTS_PROVIDER={tts.ProviderName}");
    Console.WriteLine($"VOICE_TTS_VOICE={tts.VoiceId}");
    Console.WriteLine($"VOICE_TTS_CONFIGURED={tts.IsConfigured}");
    Console.WriteLine($"VOICE_DSP_MODE={(dspBypassed ? "bypass" : "default")}");
    Console.WriteLine($"VOICE_INPUT_PRESET={voicePreset}");
    Console.WriteLine($"VOICE_INPUT_CHANNEL={InputChannelModeCatalog.ToLabel(voiceChannel)}");
    Console.WriteLine($"VOICE_GAIN_DB={(gainDb?.ToString(CultureInfo.InvariantCulture) ?? "preset")}");
    Console.WriteLine($"VOICE_NORMALIZE_BEFORE_STT={normalizeBeforeStt}");

    if (!stt.IsConfigured || !tts.IsConfigured)
    {
        Console.WriteLine("VOICE_HEALTH_SUCCESS=False");
        Console.WriteLine("VOICE_HEALTH_SUMMARY=Local STT/TTS environment variables are not fully configured.");
        Environment.ExitCode = 4;
        return;
    }

    var options = new OpenAiCompatibleRuntimeOptions(
        Enabled: true,
        Endpoint: endpoint,
        Model: model,
        DisplayName: $"Proof voice text model {model}",
        Family: "Qwen",
        Size: "14B",
        Quantization: "Ollama package default",
        ContextTokens: 2048,
        OutputTokenLimit: 256,
        Temperature: 0.2,
        TopP: 0.9,
        StreamingEnabled: true,
        SupportsVision: false,
        SupportsToolCalls: false,
        AllowPrivateLanEndpoint: false);

    RuntimeSettingsStore.Save(dataRoot, options);
    var runtime = new SafeActivatingLocalRuntime(
        new DevelopmentLocalModelRuntime(),
        new OpenAiCompatibleLocalModelRuntime(new HttpClient(), options));

    var health = await runtime.CheckCandidateAsync(CancellationToken.None);
    Console.WriteLine($"VOICE_MODEL_HEALTH_SUCCESS={health.Succeeded}");
    Console.WriteLine($"VOICE_MODEL_HEALTH_SUMMARY={health.Summary}");
    Console.WriteLine($"VOICE_MODEL={health.ModelPackageId}");
    Console.WriteLine($"VOICE_MODEL_ENDPOINT={health.Endpoint}");

    if (!health.Succeeded)
    {
        Console.WriteLine("VOICE_HEALTH_SUCCESS=False");
        Environment.ExitCode = 5;
        return;
    }

    Console.WriteLine($"VOICE_ACTIVE_BEFORE_ACTIVATE={runtime.ActiveProfile.PackageId}");
    var activated = runtime.ActivateLastHealthChecked();
    Console.WriteLine($"VOICE_MODEL_ACTIVATED={activated}");
    Console.WriteLine($"VOICE_ACTIVE_AFTER_ACTIVATE={runtime.ActiveProfile.PackageId}");

    var inputDevices = NAudioVoiceRecorder.GetInputDevices();
    var outputDevices = NAudioWaveSpeechPlayer.GetOutputDevices();
    var selectedInputDeviceNumber = ReadIntEnvironment("ALI_REAL_VOICE_INPUT_DEVICE", inputDevices.FirstOrDefault()?.DeviceNumber ?? 0);
    var selectedOutputDeviceNumber = ReadIntEnvironment("ALI_REAL_VOICE_OUTPUT_DEVICE", -1);

    if (inputDevices.Count > 0 && inputDevices.All(device => device.DeviceNumber != selectedInputDeviceNumber))
    {
        Console.WriteLine($"VOICE_INPUT_DEVICE_WARNING=Requested input device {selectedInputDeviceNumber} was not found. Falling back to {inputDevices[0].DeviceNumber}.");
        selectedInputDeviceNumber = inputDevices[0].DeviceNumber;
    }

    if (outputDevices.All(device => device.DeviceNumber != selectedOutputDeviceNumber))
    {
        Console.WriteLine($"VOICE_OUTPUT_DEVICE_WARNING=Requested output device {selectedOutputDeviceNumber} was not found. Falling back to default playback device.");
        selectedOutputDeviceNumber = -1;
    }

    Console.WriteLine($"VOICE_INPUT_DEVICE_COUNT={inputDevices.Count}");
    foreach (var device in inputDevices)
    {
        Console.WriteLine($"VOICE_INPUT_DEVICE_{device.DeviceNumber}={device.Name}");
    }

    Console.WriteLine($"VOICE_SELECTED_INPUT_DEVICE={selectedInputDeviceNumber}");
    Console.WriteLine($"VOICE_OUTPUT_DEVICE_COUNT={outputDevices.Count}");
    foreach (var device in outputDevices)
    {
        Console.WriteLine($"VOICE_OUTPUT_DEVICE_{device.DeviceNumber}={device.Name}");
    }

    Console.WriteLine($"VOICE_SELECTED_OUTPUT_DEVICE={selectedOutputDeviceNumber}");
    var audioDirectory = Path.Combine(
        dataRoot,
        "SessionAudio",
        DateTimeOffset.Now.ToString("yyyyMMdd"));
    Console.WriteLine($"VOICE_RECORD_SECONDS={recordSeconds}");
    Console.WriteLine($"VOICE_RECORD_COUNTDOWN_SECONDS={countdownSeconds}");
    Console.WriteLine("VOICE_RECORD_PROMPT=Speak now for the live Ali voice gate.");

    var recorderSettings = dspBypassed
        ? new VoiceProcessorSettings(
            HighPassEnabled: false,
            NoiseGateEnabled: false,
            NoiseSuppressionEnabled: false,
            EchoReducerEnabled: false,
            CompressorEnabled: false,
            DeEsserEnabled: false,
            DePopperEnabled: false,
            MakeupGainDb: gainDb ?? 24d,
            LimiterEnabled: true)
        : VoiceInputPreset.CreateSettings(voicePreset);
    if (gainDb is not null)
    {
        recorderSettings = recorderSettings with { MakeupGainDb = gainDb.Value };
    }

    VoiceAudioInput audioInput;
    var recorder = new NAudioVoiceRecorder(selectedInputDeviceNumber, recorderSettings)
    {
        ChannelMode = voiceChannel
    };
    try
    {
        await RunVoiceCountdownAsync(countdownSeconds);
        await recorder.StartAsync(audioDirectory, CancellationToken.None);
        await Task.Delay(TimeSpan.FromSeconds(recordSeconds));
        audioInput = await recorder.StopAsync(CancellationToken.None);
        audioInput = audioInput with { RetainAudio = retainDebugAudio };
    }
    catch (Exception ex)
    {
        recorder.Cancel();
        Console.WriteLine("VOICE_MIC_RECORD_SUCCESS=False");
        Console.WriteLine($"VOICE_MIC_RECORD_ERROR={ex.Message}");
        Environment.ExitCode = 6;
        return;
    }

    Console.WriteLine("VOICE_MIC_RECORD_SUCCESS=True");
    Console.WriteLine($"VOICE_AUDIO_PATH={audioInput.FilePath}");
    Console.WriteLine($"VOICE_AUDIO_BYTES={new FileInfo(audioInput.FilePath).Length}");
    Console.WriteLine($"VOICE_RAW_AUDIO_RETAINED={audioInput.RetainAudio}");
    var selectedInputDeviceName = inputDevices.FirstOrDefault(device => device.DeviceNumber == selectedInputDeviceNumber)?.Name
        ?? $"Device {selectedInputDeviceNumber}";
    if (normalizeBeforeStt)
    {
        var normalization = VoiceAudioNormalizer.NormalizePcm16WaveInPlace(audioInput.FilePath);
        Console.WriteLine($"VOICE_NORMALIZATION_APPLIED={normalization.Applied}");
        Console.WriteLine($"VOICE_NORMALIZATION_GAIN={normalization.GainMultiplier:0.00}");
    }

    var audioStats = VoiceAudioFileAnalyzer.AnalyzeWaveAudio(
        audioInput.FilePath,
        selectedInputDeviceNumber,
        selectedInputDeviceName);
    Console.WriteLine($"VOICE_AUDIO_DURATION_SECONDS={audioStats.DurationSeconds:N2}");
    Console.WriteLine($"VOICE_AUDIO_RMS={audioStats.RmsPcm}");
    Console.WriteLine($"VOICE_AUDIO_PEAK={audioStats.PeakPcm}");
    Console.WriteLine($"VOICE_AUDIO_STATE={audioStats.Level.State}");
    Console.WriteLine($"VOICE_AUDIO_SUMMARY={audioStats.Summary}");

    SpeechTranscript transcript;
    try
    {
        transcript = await stt.TranscribeAsync(audioInput, CancellationToken.None);
    }
    catch (Exception ex)
    {
        DeleteIfTemporary(audioInput.FilePath, audioInput.RetainAudio);
        Console.WriteLine("VOICE_TRANSCRIBE_SUCCESS=False");
        Console.WriteLine($"VOICE_TRANSCRIBE_ERROR={ex.Message}");
        Environment.ExitCode = 7;
        return;
    }
    finally
    {
        DeleteIfTemporary(audioInput.FilePath, audioInput.RetainAudio);
    }

    Console.WriteLine("VOICE_TRANSCRIBE_SUCCESS=True");
    Console.WriteLine($"VOICE_TRANSCRIPT_LENGTH={transcript.Text.Length}");
    Console.WriteLine($"VOICE_TRANSCRIPT={transcript.Text.ReplaceLineEndings(" ").Trim()}");

    var transcriptGuard = SpeechTranscriptGuard.Evaluate(transcript.Text, requireAssistantName: true);
    Console.WriteLine($"VOICE_TRANSCRIPT_GUARD_ACCEPTED={transcriptGuard.Accepted}");
    if (!transcriptGuard.Accepted)
    {
        Console.WriteLine("VOICE_HEALTH_SUCCESS=False");
        Console.WriteLine($"VOICE_HEALTH_SUMMARY={transcriptGuard.Message}");
        Environment.ExitCode = 8;
        return;
    }

    if (VoiceCommandSafety.RequiresVisibleConfirmation(transcript.Text))
    {
        Console.WriteLine("VOICE_RISKY_COMMAND_BLOCKED=True");
        Console.WriteLine($"VOICE_BLOCK_MESSAGE={VoiceCommandSafety.BlockedPhaseOneCMessage()}");
        Environment.ExitCode = 9;
        return;
    }

    var answer = (await StreamToStringAsync(runtime, transcript.Text, CancellationToken.None))
        .ReplaceLineEndings(" ")
        .Trim();
    Console.WriteLine($"VOICE_MODEL_ANSWER_LENGTH={answer.Length}");
    Console.WriteLine($"VOICE_MODEL_ANSWER={answer}");

    if (string.IsNullOrWhiteSpace(answer))
    {
        Console.WriteLine("VOICE_HEALTH_SUCCESS=False");
        Console.WriteLine("VOICE_HEALTH_SUMMARY=Local text model returned an empty answer.");
        Environment.ExitCode = 10;
        return;
    }

    var voiceMetadata = new VoiceTurnMetadata(
        VoiceInputOrigin.Voice,
        transcript.Text,
        transcript.ProviderName,
        transcript.Mode,
        tts.ProviderName,
        tts.VoiceId,
        audioInput.RetainAudio,
        selectedInputDeviceNumber,
        selectedInputDeviceName,
        InputChannelModeCatalog.ToLabel(voiceChannel),
        voicePreset,
        gainDb ?? 0d,
        normalizeBeforeStt,
        stt.ModelPath,
        tts.ModelPath,
        SuspiciousOrNoSpeech: false);

    SpeechSynthesisResult speech;
    try
    {
        speech = await tts.SynthesizeAsync(
            answer,
            new VoiceSettings(tts.VoiceId, Rate: 1.0, RetainAudio: false),
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine("VOICE_TTS_SUCCESS=False");
        Console.WriteLine($"VOICE_TTS_ERROR={ex.Message}");
        Environment.ExitCode = 11;
        return;
    }

    Console.WriteLine("VOICE_TTS_SUCCESS=True");
    Console.WriteLine($"VOICE_TTS_AUDIO_PATH={speech.AudioPath}");
    Console.WriteLine($"VOICE_TTS_AUDIO_BYTES={new FileInfo(speech.AudioPath).Length}");

    var player = new NAudioWaveSpeechPlayer { OutputDeviceNumber = selectedOutputDeviceNumber };
    try
    {
        await player.PlayAsync(speech.AudioPath, CancellationToken.None);
        Console.WriteLine("VOICE_SPEAK_ANSWER_SUCCESS=True");
    }
    catch (Exception ex)
    {
        Console.WriteLine("VOICE_SPEAK_ANSWER_SUCCESS=False");
        Console.WriteLine($"VOICE_SPEAK_ANSWER_ERROR={ex.Message}");
        Environment.ExitCode = 12;
        return;
    }
    finally
    {
        DeleteIfTemporary(speech.AudioPath, speech.RetainAudio);
    }

    var stopResult = await ValidateStopSpeakingAsync(tts, selectedOutputDeviceNumber);
    Console.WriteLine($"VOICE_STOP_SPEAKING_SUCCESS={stopResult}");

    if (!stopResult)
    {
        Environment.ExitCode = 13;
        return;
    }

    var correctionStore = new FileCorrectionQueueStore(dataRoot);
    var queue = new CorrectionQueueService(correctionStore);
    var report = await queue.FlagIncorrectAsync(
        conversationId: "real_voice_validation",
        userMessageId: "real_voice_user",
        assistantMessageId: "real_voice_assistant",
        question: transcript.Text,
        answer: answer,
        modelProfile: runtime.ActiveProfile,
        answerEvidenceStatus: EvidenceStatus.Unverified,
        category: CorrectionCategory.Other,
        userNote: "Live local voice gate correction metadata validation.",
        voiceMetadata: voiceMetadata,
        cancellationToken: CancellationToken.None);

    var stored = (await correctionStore.ListAsync(CancellationToken.None))
        .FirstOrDefault(item => item.Id == report.Id);

    Console.WriteLine($"VOICE_CORRECTION_STORED={stored is not null}");
    Console.WriteLine($"VOICE_CORRECTION_ID={report.Id}");
    Console.WriteLine($"VOICE_CORRECTION_INPUT_ORIGIN={stored?.InputOrigin}");
    Console.WriteLine($"VOICE_CORRECTION_TRANSCRIPT={stored?.VoiceTranscript}");
    Console.WriteLine($"VOICE_CORRECTION_STT={stored?.SpeechToTextProvider}");
    Console.WriteLine($"VOICE_CORRECTION_STT_MODE={stored?.SpeechToTextMode}");
    Console.WriteLine($"VOICE_CORRECTION_TTS={stored?.TextToSpeechProvider}");
    Console.WriteLine($"VOICE_CORRECTION_TTS_VOICE={stored?.TextToSpeechVoice}");
    Console.WriteLine($"VOICE_CORRECTION_RAW_AUDIO_RETAINED={stored?.RawAudioRetained}");
    Console.WriteLine($"VOICE_CORRECTION_INPUT_DEVICE={stored?.VoiceInputDeviceNumber}:{stored?.VoiceInputDeviceName}");
    Console.WriteLine($"VOICE_CORRECTION_INPUT_CHANNEL={stored?.VoiceInputChannelMode}");
    Console.WriteLine($"VOICE_CORRECTION_INPUT_PRESET={stored?.VoiceInputPreset}");
    Console.WriteLine($"VOICE_CORRECTION_EXTRA_GAIN_DB={stored?.VoiceExtraInputGainDb}");
    Console.WriteLine($"VOICE_CORRECTION_NORMALIZE_BEFORE_STT={stored?.VoiceNormalizeBeforeStt}");
    Console.WriteLine($"VOICE_CORRECTION_STT_MODEL={stored?.SpeechToTextModel}");
    Console.WriteLine($"VOICE_CORRECTION_TTS_MODEL={stored?.TextToSpeechModel}");
    Console.WriteLine($"VOICE_CORRECTION_SUSPICIOUS_OR_NO_SPEECH={stored?.SuspiciousOrNoSpeech}");

    var metadataPassed = stored is not null
        && stored.InputOrigin == VoiceInputOrigin.Voice
        && stored.VoiceTranscript == transcript.Text
        && stored.SpeechToTextProvider == transcript.ProviderName
        && stored.SpeechToTextMode == transcript.Mode
        && stored.TextToSpeechProvider == tts.ProviderName
        && stored.TextToSpeechVoice == tts.VoiceId
        && stored.RawAudioRetained == audioInput.RetainAudio
        && stored.VoiceInputDeviceNumber == selectedInputDeviceNumber
        && stored.VoiceInputDeviceName == selectedInputDeviceName
        && stored.VoiceInputChannelMode == InputChannelModeCatalog.ToLabel(voiceChannel)
        && stored.VoiceInputPreset == voicePreset
        && stored.VoiceExtraInputGainDb == (gainDb ?? 0d)
        && stored.VoiceNormalizeBeforeStt == normalizeBeforeStt
        && stored.SpeechToTextModel == stt.ModelPath
        && stored.TextToSpeechModel == tts.ModelPath
        && stored.SuspiciousOrNoSpeech == false;

    Console.WriteLine($"VOICE_CORRECTION_METADATA_SUCCESS={metadataPassed}");
    Console.WriteLine($"VOICE_HEALTH_SUCCESS={metadataPassed}");
    Console.WriteLine("VOICE_HEALTH_SUMMARY=Live local microphone -> STT -> local text model -> Piper -> stop speaking -> correction metadata gate completed.");

    if (!metadataPassed)
    {
        Environment.ExitCode = 14;
    }
}

static async Task<bool> ValidateStopSpeakingAsync(ITextToSpeechProvider tts, int outputDeviceNumber)
{
    SpeechSynthesisResult? speech = null;
    var player = new NAudioWaveSpeechPlayer { OutputDeviceNumber = outputDeviceNumber };

    try
    {
        var longText = string.Join(
            " ",
            Enumerable.Repeat("This is Ali testing stop speaking with local Piper audio.", 24));
        speech = await tts.SynthesizeAsync(
            longText,
            new VoiceSettings(tts.VoiceId, Rate: 0.85, RetainAudio: false),
            CancellationToken.None);

        var playTask = player.PlayAsync(speech.AudioPath, CancellationToken.None);
        await Task.Delay(800);
        player.Stop();

        var completed = await Task.WhenAny(playTask, Task.Delay(5000)) == playTask;
        if (completed)
        {
            try
            {
                await playTask;
            }
            catch
            {
                return !player.IsSpeaking;
            }
        }

        return completed && !player.IsSpeaking;
    }
    finally
    {
        player.Stop();
        if (speech is not null)
        {
            DeleteIfTemporary(speech.AudioPath, speech.RetainAudio);
        }
    }
}

static async Task RunVoiceCountdownAsync(int countdownSeconds)
{
    for (var remaining = countdownSeconds; remaining > 0; remaining--)
    {
        Console.WriteLine($"VOICE_RECORD_COUNTDOWN={remaining}");
        TryBeep(880, 140);
        await Task.Delay(1000);
    }

    if (countdownSeconds > 0)
    {
        Console.WriteLine("VOICE_RECORD_COUNTDOWN=recording");
        TryBeep(1200, 260);
    }
}

static void TryBeep(int frequency, int duration)
{
    try
    {
        Console.Beep(frequency, duration);
    }
    catch
    {
        // Countdown beeps are convenience only; live certification must still run without speakers.
    }
}

static int ReadIntEnvironment(string name, int defaultValue) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

static bool ReadBoolEnvironment(string name, bool defaultValue) =>
    bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : defaultValue;

static double? ReadNullableDoubleEnvironment(string name) =>
    double.TryParse(
        Environment.GetEnvironmentVariable(name),
        NumberStyles.Float,
        CultureInfo.InvariantCulture,
        out var value)
        ? value
        : null;

static void DeleteIfTemporary(string filePath, bool retain)
{
    if (retain)
    {
        return;
    }

    try
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }
    catch
    {
        // Live gate cleanup should not hide the validation result.
    }
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

internal sealed class FlakyHealthProbeHandler(string model) : HttpMessageHandler
{
    public int NonStreamingPromptCount { get; private set; }

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

        if (body.Contains("\"stream\":true", StringComparison.OrdinalIgnoreCase))
        {
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"OK\"}}]}\n\n" +
                    "data: [DONE]\n\n")
            };
        }

        NonStreamingPromptCount++;
        return NonStreamingPromptCount == 1
            ? JsonResponse("""{"choices":[{"message":{"content":""}}]}""")
            : JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
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

internal sealed class FakeVoiceRecorder : IVoiceRecorder
{
    private string? _outputDirectory;

    public bool Started { get; private set; }

    public bool IsRecording { get; private set; }

    public Task StartAsync(string outputDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
        Started = true;
        IsRecording = true;
        return Task.CompletedTask;
    }

    public Task<VoiceAudioInput> StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRecording || string.IsNullOrWhiteSpace(_outputDirectory))
        {
            throw new InvalidOperationException("Fake recorder is not recording.");
        }

        IsRecording = false;
        var filePath = Path.Combine(_outputDirectory, "fake_sample.wav");
        TestAudioFiles.WritePcm16Wave(filePath, amplitude: 0.2d);
        return Task.FromResult(new VoiceAudioInput(filePath, "audio/wav", RetainAudio: false, DateTimeOffset.UtcNow));
    }

    public void Cancel() => IsRecording = false;
}

internal static class TestAudioFiles
{
    public static void WritePcm16Wave(string filePath, double amplitude, int sampleRate = 44100, int seconds = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? Path.GetTempPath());
        var sampleCount = sampleRate * seconds;
        var dataSize = sampleCount * sizeof(short);
        using var stream = File.Create(filePath);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataSize);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8.ToArray());
        writer.Write(dataSize);

        for (var index = 0; index < sampleCount; index++)
        {
            var sample = (short)(Math.Sin(2d * Math.PI * 440d * index / sampleRate) * amplitude * short.MaxValue);
            writer.Write(sample);
        }
    }
}

internal sealed class FakeSpeechPlayer(bool completeImmediately = false) : ISpeechPlayer
{
    public bool IsSpeaking { get; private set; }

    public bool StopRequested { get; private set; }

    public bool PlayWasCalled { get; private set; }

    public Task PlayAsync(string audioPath, CancellationToken cancellationToken)
    {
        PlayWasCalled = true;
        IsSpeaking = true;
        if (completeImmediately)
        {
            IsSpeaking = false;
            return Task.CompletedTask;
        }

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
