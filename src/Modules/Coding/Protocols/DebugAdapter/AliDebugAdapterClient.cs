using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ali.Modules.Coding.Protocols.DebugAdapter;

public sealed record AliDebugAdapterBreakpoint(string SourcePath, int RequestedLine, bool Verified, int? Line, string? Message);
public sealed record AliDebugAdapterFrame(int Id, string Name, string? SourcePath, int Line, int Column);
public sealed record AliDebugAdapterThread(int Id, string Name, IReadOnlyList<AliDebugAdapterFrame> Frames);
public sealed record AliDebugAdapterVariable(string Name, string Value, string? Type, int VariablesReference);
public sealed record AliDebugAdapterSnapshot(
    string State,
    int? ProcessId,
    string? StopReason,
    IReadOnlyList<AliDebugAdapterThread> Threads,
    IReadOnlyList<AliDebugAdapterVariable> Variables);

/// <summary>
/// Language-neutral Debug Adapter Protocol client. Providers supply a pinned adapter,
/// launch arguments, and approved source paths; this class supplies only DAP semantics.
/// </summary>
internal sealed class AliDebugAdapterClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AliProtocolProcess _process;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _events = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _reader;
    private int _sequence;
    private int? _stoppedThreadId;

    private AliDebugAdapterClient(AliProtocolProcess process)
    {
        _process = process;
        _reader = Task.Run(ReadLoopAsync);
    }

    public string State { get; private set; } = "starting";
    public string? StopReason { get; private set; }
    public int? DebuggeeProcessId { get; private set; }

    public static async Task<AliDebugAdapterClient> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string adapterId,
        CancellationToken cancellationToken)
    {
        var process = AliProtocolProcess.Start(executable, workingDirectory, arguments);
        var client = new AliDebugAdapterClient(process);
        try
        {
            await client.RequestAsync(
                "initialize",
                new
                {
                    clientID = "Ali",
                    clientName = "Ali",
                    adapterID = adapterId,
                    pathFormat = "path",
                    linesStartAt1 = true,
                    columnsStartAt1 = true,
                    supportsRunInTerminalRequest = false,
                    supportsVariableType = true,
                    supportsVariablePaging = true
                },
                cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task LaunchAsync(
        object launchArguments,
        IReadOnlyDictionary<string, IReadOnlyList<int>>? breakpoints,
        CancellationToken cancellationToken)
    {
        var initialized = WaitForEventAsync("initialized", cancellationToken);
        var launch = RequestAsync("launch", launchArguments, cancellationToken);
        await initialized.ConfigureAwait(false);

        if (breakpoints is not null)
        {
            foreach (var pair in breakpoints)
            {
                await SetBreakpointsAsync(pair.Key, pair.Value, cancellationToken).ConfigureAwait(false);
            }
        }

        await RequestAsync("setExceptionBreakpoints", new { filters = Array.Empty<string>() }, cancellationToken).ConfigureAwait(false);
        await RequestAsync("configurationDone", new { }, cancellationToken).ConfigureAwait(false);
        var response = await launch.ConfigureAwait(false);
        DebuggeeProcessId ??= response["body"]?["processId"]?.GetValue<int?>();
        State = "running";
    }

    public async Task AttachAsync(object attachArguments, CancellationToken cancellationToken)
    {
        var initialized = WaitForEventAsync("initialized", cancellationToken);
        var attach = RequestAsync("attach", attachArguments, cancellationToken);
        await initialized.ConfigureAwait(false);
        await RequestAsync("setExceptionBreakpoints", new { filters = Array.Empty<string>() }, cancellationToken).ConfigureAwait(false);
        await RequestAsync("configurationDone", new { }, cancellationToken).ConfigureAwait(false);
        await attach.ConfigureAwait(false);
        State = "running";
    }

    public async Task<IReadOnlyList<AliDebugAdapterBreakpoint>> SetBreakpointsAsync(
        string sourcePath,
        IReadOnlyList<int> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0 || lines.Any(line => line <= 0))
        {
            throw new ArgumentException("At least one positive breakpoint line is required.", nameof(lines));
        }

        var response = await RequestAsync(
            "setBreakpoints",
            new
            {
                source = new { path = Path.GetFullPath(sourcePath) },
                breakpoints = lines.Select(line => new { line }).ToArray(),
                sourceModified = false
            },
            cancellationToken).ConfigureAwait(false);
        return (response["body"]?["breakpoints"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Select((node, index) => new AliDebugAdapterBreakpoint(
                sourcePath,
                lines[Math.Min(index, lines.Count - 1)],
                node["verified"]?.GetValue<bool>() ?? false,
                node["line"]?.GetValue<int?>(),
                node["message"]?.GetValue<string>()))
            .ToArray();
    }

    public async Task<AliDebugAdapterSnapshot> InspectAsync(CancellationToken cancellationToken)
    {
        var threadsResponse = await RequestAsync("threads", new { }, cancellationToken).ConfigureAwait(false);
        var threads = new List<AliDebugAdapterThread>();
        var variables = new List<AliDebugAdapterVariable>();

        foreach (var threadNode in (threadsResponse["body"]?["threads"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var threadId = threadNode["id"]?.GetValue<int>() ?? 0;
            var framesResponse = await RequestAsync(
                "stackTrace",
                new { threadId, startFrame = 0, levels = 50 },
                cancellationToken).ConfigureAwait(false);
            var frames = (framesResponse["body"]?["stackFrames"] as JsonArray ?? [])
                .OfType<JsonObject>()
                .Select(node => new AliDebugAdapterFrame(
                    node["id"]?.GetValue<int>() ?? 0,
                    node["name"]?.GetValue<string>() ?? "frame",
                    node["source"]?["path"]?.GetValue<string>(),
                    node["line"]?.GetValue<int>() ?? 0,
                    node["column"]?.GetValue<int>() ?? 0))
                .ToArray();
            threads.Add(new AliDebugAdapterThread(
                threadId,
                threadNode["name"]?.GetValue<string>() ?? $"Thread {threadId}",
                frames));
        }

        var selectedFrame = threads.SelectMany(thread => thread.Frames).FirstOrDefault();
        if (selectedFrame is not null)
        {
            var scopes = await RequestAsync("scopes", new { frameId = selectedFrame.Id }, cancellationToken).ConfigureAwait(false);
            foreach (var scope in (scopes["body"]?["scopes"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var reference = scope["variablesReference"]?.GetValue<int>() ?? 0;
                if (reference <= 0)
                {
                    continue;
                }

                var response = await RequestAsync("variables", new { variablesReference = reference }, cancellationToken).ConfigureAwait(false);
                variables.AddRange((response["body"]?["variables"] as JsonArray ?? [])
                    .OfType<JsonObject>()
                    .Select(node => new AliDebugAdapterVariable(
                        node["name"]?.GetValue<string>() ?? "value",
                        node["value"]?.GetValue<string>() ?? string.Empty,
                        node["type"]?.GetValue<string>(),
                        node["variablesReference"]?.GetValue<int>() ?? 0)));
            }
        }

        return new AliDebugAdapterSnapshot(State, DebuggeeProcessId, StopReason, threads, variables);
    }

    public async Task<JsonObject> EvaluateAsync(
        string expression,
        int? frameId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        return await RequestAsync(
            "evaluate",
            new { expression, frameId, context = "watch" },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ControlAsync(string action, CancellationToken cancellationToken)
    {
        var command = action.Trim().ToLowerInvariant() switch
        {
            "continue" => "continue",
            "next" or "stepover" => "next",
            "stepin" => "stepIn",
            "stepout" => "stepOut",
            "pause" => "pause",
            _ => throw new ArgumentException("Action must be continue, next, stepIn, stepOut, or pause.", nameof(action))
        };
        await RequestAsync(command, new { threadId = _stoppedThreadId ?? 1 }, cancellationToken).ConfigureAwait(false);
        State = command == "pause" ? "pausing" : "running";
    }

    public async Task DisconnectAsync(bool terminateDebuggee, CancellationToken cancellationToken)
    {
        try
        {
            await RequestAsync(
                "disconnect",
                new { restart = false, terminateDebuggee },
                cancellationToken).ConfigureAwait(false);
        }
        catch when (_process.Process.HasExited)
        {
        }
        State = "terminated";
    }

    internal async Task<JsonObject> RequestAsync(string command, object arguments, CancellationToken cancellationToken)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[sequence] = completion;
        await _process.Transport.WriteAsync(
            new JsonObject
            {
                ["seq"] = sequence,
                ["type"] = "request",
                ["command"] = command,
                ["arguments"] = JsonSerializer.SerializeToNode(arguments, JsonOptions)
            },
            cancellationToken).ConfigureAwait(false);
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        if (response["success"]?.GetValue<bool>() == false)
        {
            throw new InvalidOperationException(response["message"]?.GetValue<string>() ?? $"Debug adapter command '{command}' failed.");
        }
        return response;
    }

    private Task<JsonObject> WaitForEventAsync(string name, CancellationToken cancellationToken)
    {
        var completion = _events.GetOrAdd(
            name,
            _ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously));
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var message = await _process.Transport.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                var type = message["type"]?.GetValue<string>();
                if (type == "response"
                    && message["request_seq"]?.GetValue<int>() is int requestSequence
                    && _pending.TryRemove(requestSequence, out var pending))
                {
                    pending.TrySetResult(message);
                }
                else if (type == "event")
                {
                    CaptureEvent(message);
                }
                else if (type == "request")
                {
                    await RejectReverseRequestAsync(message, _lifetime.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or JsonException or OperationCanceledException)
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(ex);
            }
        }
    }

    private void CaptureEvent(JsonObject message)
    {
        var name = message["event"]?.GetValue<string>() ?? string.Empty;
        _events.GetOrAdd(
                name,
                _ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously))
            .TrySetResult(message);
        switch (name)
        {
            case "stopped":
                State = "stopped";
                StopReason = message["body"]?["reason"]?.GetValue<string>();
                _stoppedThreadId = message["body"]?["threadId"]?.GetValue<int?>();
                break;
            case "continued":
                State = "running";
                break;
            case "terminated":
            case "exited":
                State = "terminated";
                break;
            case "process":
                DebuggeeProcessId = message["body"]?["systemProcessId"]?.GetValue<int?>();
                break;
        }
    }

    private Task RejectReverseRequestAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var requestSequence = request["seq"]?.GetValue<int>() ?? 0;
        var command = request["command"]?.GetValue<string>() ?? "unknown";
        return _process.Transport.WriteAsync(
            new JsonObject
            {
                ["seq"] = Interlocked.Increment(ref _sequence),
                ["type"] = "response",
                ["request_seq"] = requestSequence,
                ["success"] = false,
                ["command"] = command,
                ["message"] = "Ali does not permit debug adapters to request arbitrary terminal commands."
            },
            cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try
        {
            await _reader.ConfigureAwait(false);
        }
        catch
        {
        }
        await _process.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
