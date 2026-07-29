using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Ali.Modules.ConversationBridge;

/// <summary>
/// Opt-in loopback bridge into Ali's real desktop conversation pipeline.
/// The host can submit text and observe UI state, but deliberately exposes no
/// endpoint that can resolve or bypass a permission prompt.
/// </summary>
public sealed class ConversationBridgeHost : IAsyncDisposable
{
    private readonly string _dataRoot;
    private readonly Func<string, CancellationToken, Task<ConversationBridgeSnapshot>> _submitTurn;
    private readonly Func<ConversationBridgeSnapshot> _captureSnapshot;
    private readonly Func<ConversationBridgeApprovalDecisionRequest, CancellationToken, Task<ConversationBridgeApprovalDecisionResult>> _submitApprovalDecision;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _statusLock = new();
    private WebApplication? _application;
    private ConversationBridgeRuntimeStatus _status;

    public ConversationBridgeHost(
        string dataRoot,
        Func<string, CancellationToken, Task<ConversationBridgeSnapshot>> submitTurn,
        Func<ConversationBridgeSnapshot> captureSnapshot,
        Func<ConversationBridgeApprovalDecisionRequest, CancellationToken, Task<ConversationBridgeApprovalDecisionResult>> submitApprovalDecision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
        _submitTurn = submitTurn ?? throw new ArgumentNullException(nameof(submitTurn));
        _captureSnapshot = captureSnapshot ?? throw new ArgumentNullException(nameof(captureSnapshot));
        _submitApprovalDecision = submitApprovalDecision ?? throw new ArgumentNullException(nameof(submitApprovalDecision));
        var settings = LoadSettings();
        _status = new ConversationBridgeRuntimeStatus(
            false,
            settings.Enabled ? "Stopped" : "Disabled",
            settings.Enabled
                ? "The live conversation bridge is enabled but not running."
                : "The live conversation bridge is off.",
            settings.Endpoint);
    }

    public event EventHandler<ConversationBridgeRuntimeStatus>? StatusChanged;

    public string SettingsPath => ConversationBridgeSettingsStore.GetPath(_dataRoot);

    public ConversationBridgeRuntimeStatus Status
    {
        get
        {
            lock (_statusLock)
            {
                return _status;
            }
        }
    }

    public bool IsRunning => Status.IsRunning;

    public ConversationBridgeSettings LoadSettings() =>
        ConversationBridgeSettingsStore.LoadOrCreate(_dataRoot);

    public ConversationBridgeSettings SaveSettings(ConversationBridgeSettings settings) =>
        ConversationBridgeSettingsStore.Save(_dataRoot, settings);

    public async Task StartIfEnabledAsync(CancellationToken cancellationToken = default)
    {
        if (LoadSettings().Enabled)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                return;
            }

            var settings = LoadSettings().Normalize();
            WebApplication? application = null;
            try
            {
                Publish(new ConversationBridgeRuntimeStatus(
                    false,
                    "Starting",
                    "Starting the authenticated live Ali conversation bridge...",
                    settings.Endpoint));
                var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
                {
                    ApplicationName = typeof(ConversationBridgeHost).Assembly.FullName,
                    Args = []
                });
                builder.Logging.ClearProviders();
                builder.WebHost.UseUrls(settings.Endpoint);
                application = builder.Build();
                application.Use(async (context, next) =>
                {
                    if (!context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                        && !HasValidBearerToken(context, settings.AuthenticationToken))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.Response.Headers.WWWAuthenticate = "Bearer";
                        await context.Response.WriteAsync(
                            "Conversation bridge authentication failed.",
                            context.RequestAborted).ConfigureAwait(false);
                        return;
                    }

                    await next(context).ConfigureAwait(false);
                });
                application.MapGet("/health", () => Results.Json(new
                {
                    service = "Ali Live Conversation Bridge",
                    state = "running",
                    mode = "live-ui-session",
                    canApprovePermissions = settings.AllowPermissionDecisions
                }));
                application.MapGet("/v1/session", () => Results.Json(_captureSnapshot()));
                application.MapPost("/v1/turns", SubmitTurnAsync);
                application.MapPost("/v1/approvals", SubmitApprovalDecisionAsync);
                await application.StartAsync(cancellationToken).ConfigureAwait(false);
                _application = application;
                application = null;
                Publish(new ConversationBridgeRuntimeStatus(
                    true,
                    "Running",
                    "Codex can observe and send through Ali's live UI conversation. Permission decisions remain user-only.",
                    settings.Endpoint));
            }
            catch
            {
                if (application is not null)
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }

                Publish(new ConversationBridgeRuntimeStatus(
                    false,
                    "Failed",
                    "The live conversation bridge failed to start.",
                    settings.Endpoint));
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var application = _application;
            _application = null;
            var settings = LoadSettings();
            if (application is not null)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    await application.StopAsync(timeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    await application.DisposeAsync().ConfigureAwait(false);
                }
            }

            Publish(new ConversationBridgeRuntimeStatus(
                false,
                settings.Enabled ? "Stopped" : "Disabled",
                settings.Enabled
                    ? "The live conversation bridge is enabled but stopped."
                    : "The live conversation bridge is off.",
                settings.Endpoint));
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _lifecycleGate.Dispose();
        _turnGate.Dispose();
    }

    private async Task<IResult> SubmitTurnAsync(
        ConversationBridgeTurnRequest request,
        HttpContext context)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return Results.BadRequest(new { error = "Text is required." });
        }

        if (text.Length > 64 * 1024)
        {
            return Results.BadRequest(new { error = "Text exceeds the 64 KiB bridge limit." });
        }

        if (!await _turnGate.WaitAsync(0, context.RequestAborted).ConfigureAwait(false))
        {
            return Results.Conflict(new
            {
                error = "Another bridge turn is already in progress.",
                session = _captureSnapshot()
            });
        }

        try
        {
            var current = _captureSnapshot();
            if (current.IsBusy)
            {
                return Results.Conflict(new
                {
                    error = "Ali is already processing a turn.",
                    session = current
                });
            }

            return Results.Json(await _submitTurn(text, context.RequestAborted).ConfigureAwait(false));
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<IResult> SubmitApprovalDecisionAsync(
        ConversationBridgeApprovalDecisionRequest request,
        HttpContext context)
    {
        if (!LoadSettings().AllowPermissionDecisions)
        {
            return Results.Json(
                new { error = "Authenticated bridge permission decisions are disabled in Ali Settings." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var requestId = request.RequestId?.Trim() ?? string.Empty;
        var decision = request.Decision?.Trim().ToLowerInvariant() ?? string.Empty;
        if (requestId.Length == 0)
        {
            return Results.BadRequest(new { error = "The pending approval request ID is required." });
        }

        if (decision is not ("allow-once" or "allow-arguments" or "allow-tool" or "deny"))
        {
            return Results.BadRequest(new
            {
                error = "Decision must be allow-once, allow-arguments, allow-tool, or deny."
            });
        }

        var result = await _submitApprovalDecision(
            new ConversationBridgeApprovalDecisionRequest(requestId, decision),
            context.RequestAborted).ConfigureAwait(false);
        return result.Accepted
            ? Results.Json(result)
            : Results.Conflict(result);
    }

    private static bool HasValidBearerToken(HttpContext context, string expectedToken)
    {
        var header = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = header[prefix.Length..].Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        return expectedBytes.Length == suppliedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
    }

    private void Publish(ConversationBridgeRuntimeStatus status)
    {
        lock (_statusLock)
        {
            _status = status;
        }

        StatusChanged?.Invoke(this, status);
    }
}
