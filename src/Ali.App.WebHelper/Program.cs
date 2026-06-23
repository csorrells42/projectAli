using System.Text;
using Ali.Core.Conversations;
using Ali.Core.Evidence;
using Ali.Core.Runtime;
using Ali.Infrastructure.Bootstrap;

var builder = WebApplication.CreateBuilder(args);
var listenUrls = Environment.GetEnvironmentVariable("ALI_HELPER_URLS");
if (string.IsNullOrWhiteSpace(listenUrls))
{
    listenUrls = "http://127.0.0.1:8765";
}

builder.WebHost.UseUrls(listenUrls);
builder.Services.AddSingleton(AliServices.CreateForDesktop());
builder.Services.AddSingleton<HelperRuntimeState>();

var app = builder.Build();
var accessToken = Environment.GetEnvironmentVariable("ALI_HELPER_TOKEN");

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

internal sealed record AskRequest(
    string Message,
    string? ConversationId = null,
    IReadOnlyList<AskHistoryItem>? History = null);

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
      grid-template-columns: 280px 1fr;
    }

    aside {
      border-right: 1px solid #2a3038;
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
    refreshHistory();
  </script>
</body>
</html>
""";
}
