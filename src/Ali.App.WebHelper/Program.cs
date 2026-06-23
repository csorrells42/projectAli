using System.Text;
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
    var history = BuildHistory(request.History);
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
    return Results.Ok(new AskResponse(
        ConversationId: conversationId,
        Answer: answer.ToString(),
        EvidenceStatus: evidence.ToString(),
        Runtime: profile.PackageId,
        RuntimeDisplayName: profile.DisplayName));
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
    string RuntimeDisplayName);

internal sealed record ErrorResponse(string Error);

internal sealed record StatusResponse(
    string ActiveRuntime,
    string ActiveDisplayName,
    string RuntimeEndpoint,
    bool IsUsingFallback,
    string? LastHealth,
    string ListeningOn);

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
      padding: 18px;
      overflow: auto;
      display: flex;
      flex-direction: column;
      gap: 12px;
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
  <main id="chat"></main>
  <form id="form">
    <textarea id="message" placeholder="Ask Ali..." autofocus></textarea>
    <button id="send" type="submit">Send</button>
  </form>
  <script>
    const chat = document.getElementById('chat');
    const form = document.getElementById('form');
    const message = document.getElementById('message');
    const send = document.getElementById('send');
    const status = document.getElementById('status');
    const token = document.getElementById('token');
    const conversationId = crypto.randomUUID();
    const history = [];
    token.value = localStorage.getItem('aliHelperToken') || '';
    token.addEventListener('change', () => {
      localStorage.setItem('aliHelperToken', token.value.trim());
    });

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
        const headers = { 'Content-Type': 'application/json' };
        const accessToken = token.value.trim();
        if (accessToken) {
          headers['X-Ali-Helper-Token'] = accessToken;
        }
        const res = await fetch('/api/ask', {
          method: 'POST',
          headers,
          body: JSON.stringify({ conversationId, message: text, history })
        });
        const data = await res.json();
        if (!res.ok) {
          addMessage(null, data.error || `HTTP ${res.status}`, 'error');
        } else {
          addMessage('assistant', data.answer || '(empty response)', 'assistant');
          status.textContent = `${data.runtime} | ${data.evidenceStatus}`;
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
  </script>
</body>
</html>
""";
}
