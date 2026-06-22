using Ali.Core.Evidence;
using Ali.Core.Feedback;
using Ali.Core.Models;
using Ali.Core.Permissions;
using Ali.Core.Runtime;
using Ali.Core.Truthfulness;
using Ali.Infrastructure.Runtime;
using Ali.Infrastructure.Storage;

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
    ("OpenAI stream parser extracts content delta", TestOpenAiStreamParserExtractsContentDelta),
    ("OpenAI response parser extracts message content", TestOpenAiResponseParserExtractsMessageContent),
    ("runtime cancellation path throws OperationCanceledException", TestRuntimeCancellationPath),
    ("correction queue stores runtime snapshot", TestCorrectionQueueStoresRuntimeSnapshot)
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
    var options = CreateRuntimeOptions("fake-local-model");

    RuntimeSettingsStore.Save(directory, options);
    var loaded = RuntimeSettingsStore.LoadOpenAiCompatibleOptions(directory);

    NotNull(loaded, "Loaded runtime settings should not be null.");
    Equal(options.Endpoint, loaded!.Endpoint);
    Equal(options.Model, loaded.Model);
    Equal(options.ContextTokens, loaded.ContextTokens);
    Equal(options.OutputTokenLimit, loaded.OutputTokenLimit);
    Equal(options.Temperature, loaded.Temperature);
    Equal(options.StreamingEnabled, loaded.StreamingEnabled);

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
}

static OpenAiCompatibleRuntimeOptions CreateRuntimeOptions(string model) =>
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
        SupportsVision: false,
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

internal sealed class FakeOpenAiHandler(string model) : HttpMessageHandler
{
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

        return JsonResponse("""{"choices":[{"message":{"content":"OK"}}]}""");
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
}
