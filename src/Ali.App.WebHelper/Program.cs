using System.Text;
using Ali.Core.Conversations;
using Ali.Core.Evidence;
using Ali.Core.Runtime;
using Ali.Infrastructure.Bootstrap;
using Ali.Infrastructure.Runtime;
using System.Diagnostics;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var listenUrls = Environment.GetEnvironmentVariable("ALI_HELPER_URLS");
if (string.IsNullOrWhiteSpace(listenUrls))
{
    listenUrls = "http://127.0.0.1:8765";
}

builder.WebHost.UseUrls(listenUrls);
builder.Services.AddSingleton(AliServices.CreateForDesktop());
builder.Services.AddSingleton<OllamaProcessOwner>();
builder.Services.AddSingleton<HelperRuntimeState>();

var app = builder.Build();
var accessToken = Environment.GetEnvironmentVariable("ALI_HELPER_TOKEN");
var ollamaOwner = app.Services.GetRequiredService<OllamaProcessOwner>();
app.Lifetime.ApplicationStopping.Register(ollamaOwner.StopProcessesStartedByAli);

app.MapGet("/", () => Results.Content(HelperPage.IndexHtml, "text/html; charset=utf-8"));

app.MapGet("/api/status", (AliServices services, HelperRuntimeState state) =>
{
    var active = services.RuntimeController.ActiveProfile;
    return Results.Ok(new StatusResponse(
        ActiveRuntime: active.PackageId,
        ActiveDisplayName: active.DisplayName,
        RuntimeEndpoint: active.RuntimeEndpoint,
        IsUsingFallback: services.RuntimeController.IsUsingFallback,
        LastHealth: state.LastHealth?.Summary,
        ListeningOn: listenUrls));
});

app.MapGet("/api/conversations", (HttpContext httpContext, AliServices services) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    var result = services.Conversations.ListSummaries();
    return Results.Ok(new ConversationListResponse(
        result.Conversations.Take(20).Select(ConversationSummaryResponse.FromSummary).ToArray(),
        result.Warnings));
});

app.MapGet("/api/conversations/{conversationId}", (string conversationId, HttpContext httpContext, AliServices services) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    var conversation = services.Conversations.Load(conversationId);
    return conversation is null
        ? Results.NotFound(new ErrorResponse("Conversation was not found."))
        : Results.Ok(ConversationResponse.FromConversation(conversation));
});

app.MapPost("/api/conversations", (HttpContext httpContext) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    var conversationId = $"web_{Guid.NewGuid():N}";
    return Results.Ok(new NewConversationResponse(conversationId));
});

app.MapGet("/api/coding/status", async (
    HttpContext httpContext,
    AliServices services,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    if (!IsLoopbackRequest(httpContext))
    {
        return Results.Json(
            new ErrorResponse("Coding bridge is loopback-only."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var result = await services.LocalCodingTool.TryHandleAsync("show visual studio integration", cancellationToken).ConfigureAwait(false);
    return Results.Ok(CodingCommandResponse.FromResult(result));
});

app.MapPost("/api/coding/command", async (
    CodingCommandRequest request,
    HttpContext httpContext,
    AliServices services,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    if (!IsLoopbackRequest(httpContext))
    {
        return Results.Json(
            new ErrorResponse("Coding bridge is loopback-only."),
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (string.IsNullOrWhiteSpace(request.Command))
    {
        return Results.BadRequest(new ErrorResponse("Command is required."));
    }

    if (request.Command.Length > 12000)
    {
        return Results.BadRequest(new ErrorResponse("Command is too long for the coding bridge."));
    }

    var result = await services.LocalCodingTool.TryHandleAsync(request.Command.Trim(), cancellationToken).ConfigureAwait(false);
    return result.Handled
        ? Results.Ok(CodingCommandResponse.FromResult(result))
        : Results.BadRequest(new ErrorResponse("Not a deterministic Ali coding command."));
});

app.MapPost("/api/ask", async (
    AskRequest request,
    HttpContext httpContext,
    AliServices services,
    HelperRuntimeState state,
    CancellationToken cancellationToken) =>
{
    if (!IsAuthorized(httpContext, accessToken))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Message))
    {
        return Results.BadRequest(new ErrorResponse("Message is required."));
    }

    if (request.Message.Length > 12000)
    {
        return Results.BadRequest(new ErrorResponse("Message is too long for the basic helper."));
    }

    await ollamaOwner.EnsureStartedForAsync(services.LoadRuntimeSettings(), cancellationToken).ConfigureAwait(false);
    var health = await state.EnsureLocalRuntimeActivatedAsync(services, cancellationToken).ConfigureAwait(false);
    if (!health.Succeeded || services.RuntimeController.IsUsingFallback)
    {
        return Results.Json(
            new ErrorResponse(health.ErrorText ?? health.Summary),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
        ? $"web_{Guid.NewGuid():N}"
        : request.ConversationId;
    var userMessageId = $"web_user_{Guid.NewGuid():N}";
    var assistantMessageId = $"web_assistant_{Guid.NewGuid():N}";
    var existingConversation = services.Conversations.Load(conversationId);
    var history = existingConversation is not null
        ? BuildHistoryFromConversation(existingConversation)
        : BuildHistory(request.History);
    var answer = new StringBuilder();
    var evidence = EvidenceStatus.Unknown;

    await foreach (var chunk in services.Orchestrator.StreamAnswerAsync(
                       conversationId,
                       userMessageId,
                       assistantMessageId,
                       request.Message.Trim(),
                       history,
                       Array.Empty<ChatAttachment>(),
                       cancellationToken).ConfigureAwait(false))
    {
        answer.Append(chunk.Text);
        if (chunk.EvidenceStatus > evidence)
        {
            evidence = chunk.EvidenceStatus;
        }
    }

    var profile = services.RuntimeController.ActiveProfile;
    var now = DateTimeOffset.UtcNow;
    var saved = SaveConversationTurn(
        services,
        existingConversation,
        conversationId,
        request.Message.Trim(),
        answer.ToString(),
        userMessageId,
        assistantMessageId,
        evidence,
        now);

    return Results.Ok(new AskResponse(
        ConversationId: saved.ConversationId,
        Answer: answer.ToString(),
        EvidenceStatus: evidence.ToString(),
        Runtime: profile.PackageId,
        RuntimeDisplayName: profile.DisplayName,
        Title: saved.Title,
        UpdatedAt: saved.UpdatedAt));
});

app.Run();

static bool IsAuthorized(HttpContext context, string? accessToken)
{
    if (string.IsNullOrWhiteSpace(accessToken))
    {
        return true;
    }

    return context.Request.Headers.TryGetValue("X-Ali-Helper-Token", out var value)
        && string.Equals(value.ToString(), accessToken, StringComparison.Ordinal);
}

static bool IsLoopbackRequest(HttpContext context)
{
    var address = context.Connection.RemoteIpAddress;
    if (address is null)
    {
        return true;
    }

    if (address.IsIPv4MappedToIPv6)
    {
        address = address.MapToIPv4();
    }

    return IPAddress.IsLoopback(address);
}

static IReadOnlyList<ChatMessage> BuildHistory(IReadOnlyList<AskHistoryItem>? history)
{
    if (history is null || history.Count == 0)
    {
        return Array.Empty<ChatMessage>();
    }

    var messages = new List<ChatMessage>();
    foreach (var item in history.TakeLast(12))
    {
        var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
            ? ChatRole.Assistant
            : ChatRole.User;
        if (string.IsNullOrWhiteSpace(item.Text))
        {
            continue;
        }

        messages.Add(new ChatMessage(
            $"web_history_{Guid.NewGuid():N}",
            role,
            item.Text,
            DateTimeOffset.UtcNow,
            EvidenceStatus.Unverified));
    }

    return messages;
}

static IReadOnlyList<ChatMessage> BuildHistoryFromConversation(StoredConversation conversation) =>
    conversation.Messages
        .Where(message => message.Role is ChatRole.User or ChatRole.Assistant)
        .OrderBy(message => message.CreatedAt)
        .TakeLast(12)
        .Select(message => new ChatMessage(
            message.MessageId,
            message.Role,
            message.Text,
            message.CreatedAt,
            message.EvidenceStatus))
        .ToArray();

static StoredConversation SaveConversationTurn(
    AliServices services,
    StoredConversation? existingConversation,
    string conversationId,
    string userText,
    string assistantText,
    string userMessageId,
    string assistantMessageId,
    EvidenceStatus evidence,
    DateTimeOffset now)
{
    var messages = existingConversation?.Messages.ToList() ?? new List<StoredChatMessage>();
    messages.Add(new StoredChatMessage(
        userMessageId,
        conversationId,
        ChatRole.User,
        userText,
        now,
        ChatMessageOrigin.Typed,
        EvidenceStatus.Verified));
    messages.Add(new StoredChatMessage(
        assistantMessageId,
        conversationId,
        ChatRole.Assistant,
        assistantText,
        now.AddMilliseconds(1),
        ChatMessageOrigin.Typed,
        evidence,
        SourceUserMessageId: userMessageId,
        SourceQuestion: userText));

    var createdAt = existingConversation?.CreatedAt ?? now;
    var title = existingConversation?.Title;
    if (string.IsNullOrWhiteSpace(title) || string.Equals(title, "Untitled chat", StringComparison.OrdinalIgnoreCase))
    {
        title = ConversationTitleFactory.CreateFromFirstMessage(userText);
    }

    return services.Conversations.Save(new StoredConversation(
        conversationId,
        title,
        createdAt,
        now.AddMilliseconds(1),
        messages));
}

internal sealed class HelperRuntimeState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _attemptedActivation;

    public RuntimeHealthCheck? LastHealth { get; private set; }

    public async Task<RuntimeHealthCheck> EnsureLocalRuntimeActivatedAsync(
        AliServices services,
        CancellationToken cancellationToken)
    {
        if (!services.RuntimeController.IsUsingFallback && LastHealth is { Succeeded: true })
        {
            return LastHealth;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!services.RuntimeController.IsUsingFallback && LastHealth is { Succeeded: true })
            {
                return LastHealth;
            }

            var options = services.LoadRuntimeSettings();
            if (!options.Enabled || string.IsNullOrWhiteSpace(options.Model))
            {
                LastHealth = new RuntimeHealthCheck(
                    Succeeded: false,
                    Summary: "Local runtime settings are not enabled/configured.",
                    CheckedAt: DateTimeOffset.UtcNow,
                    Elapsed: TimeSpan.Zero,
                    Endpoint: options.Endpoint.ToString(),
                    ModelPackageId: options.Model,
                    ErrorText: "Open Ali Settings, configure a local runtime, run Check, then try the web helper again.");
                return LastHealth;
            }

            if (!_attemptedActivation)
            {
                services.ConfigureRuntimeCandidate(options);
                LastHealth = await services.RuntimeController.CheckCandidateAsync(cancellationToken).ConfigureAwait(false);
                if (LastHealth.Succeeded)
                {
                    services.RuntimeController.ActivateLastHealthChecked();
                }

                _attemptedActivation = true;
            }

            return LastHealth ?? new RuntimeHealthCheck(
                Succeeded: false,
                Summary: "Local runtime activation did not produce a health result.",
                CheckedAt: DateTimeOffset.UtcNow,
                Elapsed: TimeSpan.Zero,
                ErrorText: "Unknown runtime activation state.");
        }
        finally
        {
            _gate.Release();
        }
    }
}

internal sealed class OllamaProcessOwner
{
    private static readonly TimeSpan OllamaStartRetryInterval = TimeSpan.FromMinutes(2);
    private readonly HashSet<int> _processIdsStartedByAli = new();
    private bool _ollamaWasRunningAtStartup;
    private bool _startInProgress;
    private DateTimeOffset _nextStartAttemptAt = DateTimeOffset.MinValue;

    public async Task EnsureStartedForAsync(
        OpenAiCompatibleRuntimeOptions options,
        CancellationToken cancellationToken)
    {
        if (!options.Enabled || !IsLocalOllamaEndpoint(options.Endpoint))
        {
            return;
        }

        if (_startInProgress || _processIdsStartedByAli.Count > 0)
        {
            return;
        }

        var before = GetOllamaProcesses();
        if (before.Count > 0)
        {
            _ollamaWasRunningAtStartup = true;
            _nextStartAttemptAt = DateTimeOffset.MaxValue;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now < _nextStartAttemptAt)
        {
            return;
        }

        _startInProgress = true;
        _nextStartAttemptAt = now + OllamaStartRetryInterval;

        var appPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama app.exe");
        var serverPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs",
            "Ollama",
            "ollama.exe");

        try
        {
            var launchedProcess = StartOwnedOllamaProcess(serverPath, appPath);
            if (launchedProcess is null)
            {
                return;
            }

            _processIdsStartedByAli.Add(launchedProcess.Id);
            await Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken).ConfigureAwait(false);
            var beforeIds = before.Select(process => process.Id).ToHashSet();
            foreach (var process in GetOllamaProcesses())
            {
                if (!beforeIds.Contains(process.Id))
                {
                    _processIdsStartedByAli.Add(process.Id);
                }
            }
        }
        finally
        {
            _startInProgress = false;
        }
    }

    private static Process? StartOwnedOllamaProcess(string serverPath, string appPath)
    {
        if (File.Exists(serverPath))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = serverPath,
                Arguments = "serve",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        if (File.Exists(appPath))
        {
            return Process.Start(new ProcessStartInfo
            {
                FileName = appPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }

        return null;
    }

    public void StopProcessesStartedByAli()
    {
        if (_ollamaWasRunningAtStartup || _processIdsStartedByAli.Count == 0)
        {
            return;
        }

        foreach (var process in GetOllamaProcesses())
        {
            if (!_processIdsStartedByAli.Contains(process.Id))
            {
                continue;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best-effort cleanup of the Ollama instance Ali launched.
            }
        }

        _processIdsStartedByAli.Clear();
    }

    private static IReadOnlyList<Process> GetOllamaProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(process =>
                    process.ProcessName.Equals("ollama", StringComparison.OrdinalIgnoreCase)
                    || process.ProcessName.Equals("ollama app", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch
        {
            return Array.Empty<Process>();
        }
    }

    private static bool IsLocalOllamaEndpoint(Uri endpoint) =>
        endpoint.Port == 11434
        && (endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || endpoint.Host.Equals("::1", StringComparison.OrdinalIgnoreCase));
}

internal sealed record AskRequest(
    string Message,
    string? ConversationId = null,
    IReadOnlyList<AskHistoryItem>? History = null);

internal sealed record CodingCommandRequest(string Command);

internal sealed record CodingCommandResponse(
    bool Handled,
    bool Succeeded,
    string Message,
    string? ToolName,
    string? TargetPath,
    int? LineNumber,
    int? ExitCode)
{
    public static CodingCommandResponse FromResult(Ali.Core.Coding.CodingToolResult result) =>
        new(
            result.Handled,
            result.Succeeded,
            result.Message,
            result.ToolName,
            result.TargetPath,
            result.LineNumber,
            result.ExitCode);
}

internal sealed record AskHistoryItem(string Role, string Text);

internal sealed record AskResponse(
    string ConversationId,
    string Answer,
    string EvidenceStatus,
    string Runtime,
    string RuntimeDisplayName,
    string Title,
    DateTimeOffset UpdatedAt);

internal sealed record ErrorResponse(string Error);

internal sealed record StatusResponse(
    string ActiveRuntime,
    string ActiveDisplayName,
    string RuntimeEndpoint,
    bool IsUsingFallback,
    string? LastHealth,
    string ListeningOn);

internal sealed record NewConversationResponse(string ConversationId);

internal sealed record ConversationListResponse(
    IReadOnlyList<ConversationSummaryResponse> Conversations,
    IReadOnlyList<string> Warnings);

internal sealed record ConversationSummaryResponse(
    string ConversationId,
    string Title,
    DateTimeOffset UpdatedAt,
    int MessageCount,
    string Preview)
{
    public static ConversationSummaryResponse FromSummary(StoredConversationSummary summary) =>
        new(
            summary.ConversationId,
            summary.Title,
            summary.UpdatedAt,
            summary.MessageCount,
            summary.Preview);
}

internal sealed record ConversationResponse(
    string ConversationId,
    string Title,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessageResponse> Messages)
{
    public static ConversationResponse FromConversation(StoredConversation conversation) =>
        new(
            conversation.ConversationId,
            conversation.Title,
            conversation.UpdatedAt,
            conversation.Messages
                .OrderBy(message => message.CreatedAt)
                .Select(ConversationMessageResponse.FromMessage)
                .ToArray());
}

internal sealed record ConversationMessageResponse(
    string Role,
    string Text,
    string EvidenceStatus,
    DateTimeOffset CreatedAt)
{
    public static ConversationMessageResponse FromMessage(StoredChatMessage message) =>
        new(
            message.Role == ChatRole.Assistant ? "assistant" : "user",
            message.Text,
            message.EvidenceStatus.ToString(),
            message.CreatedAt);
}

internal static class HelperPage
{
public const string IndexHtml = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Ali Helper</title>
  <style>
    :root {
      color-scheme: dark;
      font-family: "Segoe UI", system-ui, sans-serif;
      background: #0b0d10;
      color: #eef2f7;
    }

    body {
      margin: 0;
      min-height: 100vh;
      display: grid;
      grid-template-rows: auto 1fr auto;
      background: #0b0d10;
    }

    header {
      padding: 14px 18px;
      border-bottom: 1px solid #2a3038;
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }

    .header-right {
      display: flex;
      align-items: center;
      gap: 10px;
    }

    h1 {
      margin: 0;
      font-size: 18px;
      font-weight: 700;
    }

    #status {
      font-size: 12px;
      color: #9aa6b2;
      text-align: right;
    }

    #token {
      width: 150px;
      background: #11161d;
      color: #eef2f7;
      border: 1px solid #384250;
      border-radius: 8px;
      padding: 8px;
      font: inherit;
      font-size: 12px;
    }

    main {
      min-height: 0;
      display: grid;
      grid-template-columns: 260px minmax(0, 1fr) 360px;
    }

    aside {
      border-right: 1px solid #2a3038;
      padding: 12px;
      overflow: auto;
      background: #080a0d;
    }

    #programmingRail {
      border-left: 1px solid #2a3038;
      padding: 12px;
      overflow: auto;
      background: #080a0d;
    }

    #chat {
      padding: 18px;
      overflow: auto;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }

    #newChat {
      width: 100%;
      height: 42px;
      margin-bottom: 12px;
      border-color: #384250;
      background: #151a22;
    }

    #codingBridge {
      display: grid;
      gap: 10px;
    }

    #codingBridge h2 {
      margin: 0;
      font-size: 14px;
      font-weight: 700;
    }

    .bridge-kicker {
      margin: 0;
      color: #a7b0bc;
      font-size: 11px;
      line-height: 1.35;
    }

    .command-group {
      display: grid;
      gap: 6px;
    }

    .command-group h3 {
      margin: 6px 0 0;
      color: #d7dde7;
      font-size: 11px;
      font-weight: 700;
      text-transform: uppercase;
      letter-spacing: .06em;
    }

    .command-grid {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 6px;
    }

    .command-chip {
      min-width: 0;
      min-height: 34px;
      padding: 7px 8px;
      border-color: #405063;
      background: #131820;
      color: #eef2f7;
      font-size: 11px;
      font-weight: 700;
      text-align: left;
      cursor: pointer;
    }

    .command-chip:hover {
      border-color: #4ade80;
      background: #142019;
    }

    .command-chip.confirm {
      border-color: #a16207;
      background: #211807;
    }

    .command-chip.confirm:hover {
      border-color: #f59e0b;
    }

    .skill-list {
      display: grid;
      gap: 6px;
      margin: 0;
      padding: 0;
      list-style: none;
    }

    .skill-list li {
      border: 1px solid #2a3038;
      border-radius: 8px;
      padding: 7px 8px;
      background: #0e1218;
      color: #cbd5e1;
      font-size: 11px;
      line-height: 1.35;
    }

    .skill-list strong {
      display: block;
      color: #eef2f7;
      font-size: 12px;
      margin-bottom: 2px;
    }

    #codingCommand {
      min-height: 58px;
      resize: vertical;
      background: #11161d;
      color: #eef2f7;
      border: 1px solid #384250;
      border-radius: 8px;
      padding: 8px;
      font: inherit;
      font-size: 12px;
    }

    #codingRun {
      height: 36px;
      min-width: 0;
    }

    .command-actions {
      display: grid;
      grid-template-columns: 1fr 1fr;
      gap: 8px;
    }

    #codingClear {
      height: 36px;
      min-width: 0;
      border-color: #405063;
      background: #151a22;
    }

    #codingOutput {
      min-height: 100px;
      max-height: 240px;
      overflow: auto;
      white-space: pre-wrap;
      margin: 0;
      border: 1px solid #2a3038;
      border-radius: 8px;
      padding: 8px;
      background: #101419;
      color: #cbd5e1;
      font-size: 11px;
      line-height: 1.35;
    }

    .history-item {
      width: 100%;
      display: block;
      text-align: left;
      border: 1px solid #2a3038;
      background: #101419;
      color: #eef2f7;
      border-radius: 8px;
      padding: 10px;
      margin-bottom: 8px;
      cursor: pointer;
    }

    .history-item.active {
      border-color: #38bdf8;
      background: #162033;
    }

    .history-title {
      font-weight: 700;
      font-size: 13px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .history-preview {
      margin-top: 4px;
      color: #9aa6b2;
      font-size: 12px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }

    .msg {
      max-width: 900px;
      white-space: pre-wrap;
      line-height: 1.45;
      border: 1px solid #2a3038;
      padding: 12px 14px;
      border-radius: 8px;
    }

    .user {
      align-self: flex-end;
      background: #162033;
    }

    .assistant {
      align-self: flex-start;
      background: #101419;
    }

    .error {
      align-self: flex-start;
      background: #2a1414;
      border-color: #7f1d1d;
    }

    form {
      border-top: 1px solid #2a3038;
      display: grid;
      grid-template-columns: 1fr auto;
      gap: 10px;
      padding: 12px;
    }

    textarea {
      min-height: 52px;
      max-height: 180px;
      resize: vertical;
      background: #11161d;
      color: #eef2f7;
      border: 1px solid #384250;
      border-radius: 8px;
      padding: 10px;
      font: inherit;
    }

    button {
      border: 1px solid #60a5fa;
      background: #1d4ed8;
      color: white;
      border-radius: 8px;
      padding: 0 18px;
      font-weight: 700;
      min-width: 92px;
    }

    button:disabled {
      opacity: .55;
    }

    @media (max-width: 980px) {
      main {
        grid-template-columns: 1fr;
      }

      aside {
        border-right: 0;
        border-bottom: 1px solid #2a3038;
        max-height: 28vh;
      }

      #programmingRail {
        border-left: 0;
        border-top: 1px solid #2a3038;
        max-height: 46vh;
      }
    }
  </style>
</head>
<body>
  <header>
    <h1>Ali Helper</h1>
    <div class="header-right">
      <input id="token" type="password" placeholder="access token">
      <div id="status">Checking status...</div>
    </div>
  </header>
  <main>
    <aside>
      <button id="newChat" type="button">New Chat</button>
      <div id="history"></div>
    </aside>
    <section id="chat"></section>
    <section id="programmingRail">
      <section id="codingBridge">
        <h2>Programming Companion</h2>
        <p class="bridge-kicker">Pick a command, review it, then run it through Ali's normal approval gates.</p>
        <div class="command-group">
          <h3>Awareness</h3>
          <div class="command-grid">
            <button class="command-chip" type="button" data-command="show visual studio integration">VS Status</button>
            <button class="command-chip" type="button" data-command="inspect coding workspace">Workspace</button>
            <button class="command-chip" type="button" data-command="analyze solution architecture">Architecture</button>
            <button class="command-chip" type="button" data-command="list packages">Packages</button>
          </div>
        </div>
        <div class="command-group">
          <h3>Plan</h3>
          <div class="command-grid">
            <button class="command-chip" type="button" data-command="explore build idea ">Explore Idea</button>
            <button class="command-chip" type="button" data-command="draft implementation roadmap ">Roadmap</button>
            <button class="command-chip" type="button" data-command="show next coding action">Next Action</button>
            <button class="command-chip" type="button" data-command="show active roadmap step">Active Step</button>
            <button class="command-chip" type="button" data-command="show crash recovery status">Recovery</button>
          </div>
        </div>
        <div class="command-group">
          <h3>Build</h3>
          <div class="command-grid">
            <button class="command-chip confirm" type="button" data-command="confirm dotnet build &quot;path&quot;">Build</button>
            <button class="command-chip confirm" type="button" data-command="confirm dotnet test &quot;path&quot;">Test</button>
            <button class="command-chip" type="button" data-command="diagnose last build failure">Diagnose</button>
            <button class="command-chip" type="button" data-command="suggest patch from last failure">Patch Preview</button>
          </div>
        </div>
        <div class="command-group">
          <h3>Git and Reports</h3>
          <div class="command-grid">
            <button class="command-chip" type="button" data-command="git status">Git Status</button>
            <button class="command-chip" type="button" data-command="git diff">Git Diff</button>
            <button class="command-chip" type="button" data-command="show coding receipts">Receipts</button>
            <button class="command-chip" type="button" data-command="generate coding report">Report</button>
          </div>
        </div>
        <div class="command-group">
          <h3>Skills</h3>
          <ul class="skill-list">
            <li><strong>Scout</strong>Maps solutions, packages, files, and architecture.</li>
            <li><strong>Plan</strong>Drafts roadmaps, next actions, and approval checkpoints.</li>
            <li><strong>Build</strong>Runs confirmed dotnet build/test/restore/package commands.</li>
            <li><strong>Recover</strong>Compares roadmap, receipts, and Git state after interruption.</li>
            <li><strong>Guard</strong>Previews exact patches before applying confirmed edits.</li>
          </ul>
        </div>
        <textarea id="codingCommand" placeholder="show visual studio integration"></textarea>
        <div class="command-actions">
          <button id="codingRun" type="button">Run</button>
          <button id="codingClear" type="button">Clear</button>
        </div>
        <pre id="codingOutput">Checking coding bridge...</pre>
      </section>
    </section>
  </main>
  <form id="form">
    <textarea id="message" placeholder="Ask Ali..." autofocus></textarea>
    <button id="send" type="submit">Send</button>
  </form>
  <script>
    const chat = document.getElementById('chat');
    const historyList = document.getElementById('history');
    const newChat = document.getElementById('newChat');
    const form = document.getElementById('form');
    const message = document.getElementById('message');
    const send = document.getElementById('send');
    const status = document.getElementById('status');
    const token = document.getElementById('token');
    const codingCommand = document.getElementById('codingCommand');
    const codingRun = document.getElementById('codingRun');
    const codingClear = document.getElementById('codingClear');
    const codingOutput = document.getElementById('codingOutput');
    const commandChips = Array.from(document.querySelectorAll('.command-chip'));
    let conversationId = crypto.randomUUID();
    const history = [];
    token.value = localStorage.getItem('aliHelperToken') || '';
    token.addEventListener('change', () => {
      localStorage.setItem('aliHelperToken', token.value.trim());
    });

    function requestHeaders(json = false) {
      const headers = json ? { 'Content-Type': 'application/json' } : {};
      const accessToken = token.value.trim();
      if (accessToken) {
        headers['X-Ali-Helper-Token'] = accessToken;
      }

      return headers;
    }

    function addMessage(role, text, className) {
      const div = document.createElement('div');
      div.className = `msg ${className}`;
      div.textContent = text;
      chat.appendChild(div);
      chat.scrollTop = chat.scrollHeight;
      if (role) {
        history.push({ role, text });
        while (history.length > 12) history.shift();
      }
    }

    function clearChat() {
      chat.textContent = '';
      history.length = 0;
    }

    async function refreshHistory() {
      try {
        const res = await fetch('/api/conversations', { headers: requestHeaders() });
        const data = await res.json();
        if (!res.ok) {
          historyList.textContent = res.status === 401 ? 'Token required' : 'History unavailable';
          return;
        }

        historyList.textContent = '';
        for (const item of data.conversations || []) {
          const button = document.createElement('button');
          button.type = 'button';
          button.className = `history-item ${item.conversationId === conversationId ? 'active' : ''}`;
          button.innerHTML = `<div class="history-title"></div><div class="history-preview"></div>`;
          button.querySelector('.history-title').textContent = item.title || 'Untitled chat';
          button.querySelector('.history-preview').textContent = item.preview || `${item.messageCount} message(s)`;
          button.addEventListener('click', () => loadConversation(item.conversationId));
          historyList.appendChild(button);
        }
      } catch {
        historyList.textContent = 'History unavailable';
      }
    }

    async function loadConversation(id) {
      const res = await fetch(`/api/conversations/${encodeURIComponent(id)}`, { headers: requestHeaders() });
      const data = await res.json();
      if (!res.ok) {
        addMessage(null, data.error || `HTTP ${res.status}`, 'error');
        return;
      }

      conversationId = data.conversationId;
      clearChat();
      for (const item of data.messages || []) {
        if (item.role === 'assistant') {
          addMessage('assistant', item.text, 'assistant');
        } else {
          addMessage('user', item.text, 'user');
        }
      }

      await refreshHistory();
      message.focus();
    }

    newChat.addEventListener('click', async () => {
      try {
        const res = await fetch('/api/conversations', { method: 'POST', headers: requestHeaders() });
        const data = await res.json();
        conversationId = data.conversationId || crypto.randomUUID();
      } catch {
        conversationId = crypto.randomUUID();
      }

      clearChat();
      await refreshHistory();
      message.focus();
    });

    async function refreshStatus() {
      try {
        const res = await fetch('/api/status');
        const data = await res.json();
        status.textContent = `${data.activeRuntime} | ${data.isUsingFallback ? 'stub' : 'local runtime'}`;
      } catch {
        status.textContent = 'status unavailable';
      }
    }

    async function refreshCodingStatus() {
      try {
        const res = await fetch('/api/coding/status', { headers: requestHeaders() });
        const data = await res.json();
        if (!res.ok) {
          codingOutput.textContent = data.error || `HTTP ${res.status}`;
          return;
        }

        codingOutput.textContent = data.message || 'Coding bridge ready.';
      } catch (error) {
        codingOutput.textContent = error.message || 'Coding bridge unavailable';
      }
    }

    commandChips.forEach((button) => {
      button.addEventListener('click', () => {
        codingCommand.value = button.dataset.command || '';
        codingCommand.focus();
      });
    });

    codingClear.addEventListener('click', () => {
      codingCommand.value = '';
      codingCommand.focus();
    });

    codingRun.addEventListener('click', async () => {
      const command = codingCommand.value.trim() || 'show visual studio integration';
      codingRun.disabled = true;
      try {
        const res = await fetch('/api/coding/command', {
          method: 'POST',
          headers: requestHeaders(true),
          body: JSON.stringify({ command })
        });
        const data = await res.json();
        if (!res.ok) {
          codingOutput.textContent = data.error || `HTTP ${res.status}`;
        } else {
          codingOutput.textContent = `${data.succeeded ? 'SUCCEEDED' : 'NOT APPLIED'} | ${data.toolName || 'coding'}\n${data.message || ''}`;
        }
      } catch (error) {
        codingOutput.textContent = error.message || 'Coding bridge request failed';
      } finally {
        codingRun.disabled = false;
      }
    });

    form.addEventListener('submit', async (event) => {
      event.preventDefault();
      const text = message.value.trim();
      if (!text) return;
      message.value = '';
      send.disabled = true;
      addMessage('user', text, 'user');
      try {
        const res = await fetch('/api/ask', {
          method: 'POST',
          headers: requestHeaders(true),
          body: JSON.stringify({ conversationId, message: text, history })
        });
        const data = await res.json();
        if (!res.ok) {
          addMessage(null, data.error || `HTTP ${res.status}`, 'error');
        } else {
          conversationId = data.conversationId || conversationId;
          addMessage('assistant', data.answer || '(empty response)', 'assistant');
          status.textContent = `${data.runtime} | ${data.evidenceStatus}`;
          await refreshHistory();
        }
      } catch (error) {
        addMessage(null, error.message || 'Request failed', 'error');
      } finally {
        send.disabled = false;
        message.focus();
      }
    });

    message.addEventListener('keydown', (event) => {
      if (event.key === 'Enter' && !event.shiftKey) {
        event.preventDefault();
        form.requestSubmit();
      }
    });

    refreshStatus();
    refreshCodingStatus();
    refreshHistory();
  </script>
</body>
</html>
""";
}
