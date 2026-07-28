using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ali.Modules.Coding.Debugging;

public sealed record DebugBreakpoint(string DocumentPath, int Line, bool Verified, int? ResolvedLine, string? Message);
public sealed record DebugStackFrame(int Id, string Name, string? SourcePath, int Line, int Column);
public sealed record DebugThread(int Id, string Name, IReadOnlyList<DebugStackFrame> Frames);
public sealed record DebugVariable(string Name, string Value, string? Type, int VariablesReference);
public sealed record DotNetDebugSessionResult(bool Success, string Summary, string? SessionId, int? ProcessId, IReadOnlyList<DebugBreakpoint> Breakpoints);
public sealed record DotNetDebugSnapshot(bool Success, string Summary, string SessionId, string State, IReadOnlyList<DebugThread> Threads, IReadOnlyList<DebugVariable> Variables, string? StopReason);
public sealed record DotNetDebugEvaluation(bool Success, string Summary, string? Value, string? Type, int VariablesReference);
public sealed record DotNetDebugControlResult(bool Success, string Summary, string SessionId, string State);
public sealed record DebugDiagnosticsHandoff(bool Success, string SessionId, int? ProcessId, string State, IReadOnlyList<string> SupportedConsumers);

/// <summary>
/// Owns bounded CLR debug sessions through netcoredbg's standard Debug Adapter Protocol.
/// It never accepts a shell command or an arbitrary executable path.
/// </summary>
internal sealed class AliDotNetDebugger : IAsyncDisposable
{
    private readonly AliCodingProjectResolver _resolver;
    private readonly ConcurrentDictionary<string, DebugSession> _sessions = new(StringComparer.Ordinal);

    public AliDotNetDebugger(AliCodingProjectResolver resolver) => _resolver = resolver;

    public async Task<DotNetDebugSessionResult> LaunchAsync(
        string projectPath,
        string? configuration,
        bool stopAtEntry,
        string? breakpointDocument,
        int? breakpointLine,
        CancellationToken cancellationToken)
    {
        var project = _resolver.ResolveExistingProject(projectPath);
        var normalized = NormalizeConfiguration(configuration);
        var artifact = AliRoslynCodingTools.FindBuiltArtifact(project.PhysicalPath, normalized);
        if (artifact is null)
        {
            return new DotNetDebugSessionResult(false, "Build the project before starting a debugger session.", null, null, []);
        }

        AliCodingProjectResolver.RejectReparsePoints(project.MountRoot, artifact);
        var breakpointPath = breakpointDocument is null ? null : _resolver.ResolveDocument(project, breakpointDocument);
        if (breakpointPath is not null && breakpointLine is not > 0)
        {
            throw new ArgumentOutOfRangeException(nameof(breakpointLine), "A breakpoint line must be greater than zero.");
        }

        var id = Guid.NewGuid().ToString("N");
        var session = await DebugSession.StartAsync(id, ResolveDebuggerPath(), cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryAdd(id, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException("Could not register the debugger session.");
        }

        try
        {
            var breakpoints = await session.LaunchAsync(artifact, project.ProjectDirectory, stopAtEntry,
                breakpointPath, breakpointLine, cancellationToken).ConfigureAwait(false);
            return new DotNetDebugSessionResult(true, "The CLR debugger session started.", id, session.DebuggeeProcessId, breakpoints);
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<DotNetDebugSessionResult> AttachAsync(
        string projectPath,
        int processId,
        CancellationToken cancellationToken)
    {
        var project = _resolver.ResolveExistingProject(projectPath);
        using var process = Process.GetProcessById(processId);
        var processPath = process.MainModule?.FileName ?? throw new InvalidOperationException("The target process path is unavailable.");
        var projectRoot = Path.TrimEndingDirectorySeparator(project.ProjectDirectory) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(processPath).StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The process is not running from the approved project folder.");
        }

        var id = Guid.NewGuid().ToString("N");
        var session = await DebugSession.StartAsync(id, ResolveDebuggerPath(), cancellationToken).ConfigureAwait(false);
        if (!_sessions.TryAdd(id, session)) throw new InvalidOperationException("Could not register the debugger session.");
        try
        {
            await session.AttachAsync(processId, cancellationToken).ConfigureAwait(false);
            return new DotNetDebugSessionResult(true, "Attached the CLR debugger to the approved project process.", id, processId, []);
        }
        catch
        {
            _sessions.TryRemove(id, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task<DotNetDebugSnapshot> InspectAsync(string sessionId, CancellationToken cancellationToken) =>
        Get(sessionId).InspectAsync(cancellationToken);

    public Task<DotNetDebugEvaluation> EvaluateAsync(string sessionId, string expression, int? frameId, CancellationToken cancellationToken) =>
        Get(sessionId).EvaluateAsync(expression, frameId, cancellationToken);

    public Task<DotNetDebugControlResult> ControlAsync(string sessionId, string action, CancellationToken cancellationToken) =>
        Get(sessionId).ControlAsync(action, cancellationToken);

    public async Task<IReadOnlyList<DebugBreakpoint>> SetBreakpointsAsync(
        string sessionId,
        string projectPath,
        string documentPath,
        int[] lines,
        CancellationToken cancellationToken)
    {
        var project = _resolver.ResolveExistingProject(projectPath);
        var document = _resolver.ResolveDocument(project, documentPath);
        if (lines.Length == 0 || lines.Any(line => line <= 0))
        {
            throw new ArgumentException("At least one positive one-based breakpoint line is required.", nameof(lines));
        }
        return await Get(sessionId).SetBreakpointsAsync(document, lines.Distinct().Order().ToArray(), cancellationToken).ConfigureAwait(false);
    }

    public DebugDiagnosticsHandoff GetDiagnosticsHandoff(string sessionId)
    {
        var session = Get(sessionId);
        return new DebugDiagnosticsHandoff(true, sessionId, session.DebuggeeProcessId, session.State,
            [".NET diagnostics IPC", "dotnet-counters", "dotnet-trace", "coverage/profiler module"]);
    }

    public async Task<DotNetDebugControlResult> StopAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = Get(sessionId);
        var result = await session.StopAsync(cancellationToken).ConfigureAwait(false);
        _sessions.TryRemove(sessionId, out _);
        await session.DisposeAsync().ConfigureAwait(false);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _sessions.ToArray())
        {
            _sessions.TryRemove(pair.Key, out _);
            await pair.Value.DisposeAsync().ConfigureAwait(false);
        }
    }

    private DebugSession Get(string id) => _sessions.TryGetValue(id, out var session)
        ? session
        : throw new KeyNotFoundException("The debugger session is not active.");

    private static string ResolveDebuggerPath()
    {
        var configured = Environment.GetEnvironmentVariable("ALI_NETCOREDBG_PATH");
        var candidates = new[]
        {
            configured,
            Path.Combine(AppContext.BaseDirectory, "dependencies", "debugger", "netcoredbg", "netcoredbg.exe"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "runtime-assets", "win-x64", "dependencies", "debugger", "netcoredbg", "netcoredbg.exe"))
        };
        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
            ?? throw new FileNotFoundException("The pinned netcoredbg runtime is missing. Restore Ali runtime assets and republish.");
    }

    private static string NormalizeConfiguration(string? value) => string.IsNullOrWhiteSpace(value) ? "Debug" : value.Trim() switch
    {
        var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug",
        var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
        _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(value))
    };

    private sealed class DebugSession : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private readonly string _id;
        private readonly Process _process;
        private readonly Stream _input;
        private readonly Stream _output;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _events = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Task _reader;
        private int _sequence;
        private string _state = "starting";
        private string? _stopReason;
        private int? _stoppedThreadId;

        private DebugSession(string id, Process process)
        {
            _id = id;
            _process = process;
            _input = process.StandardInput.BaseStream;
            _output = process.StandardOutput.BaseStream;
            _reader = Task.Run(ReadLoopAsync);
        }

        public int? DebuggeeProcessId { get; private set; }
        public string State => _state;

        public static async Task<DebugSession> StartAsync(string id, string executable, CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--interpreter=vscode");
            var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Windows did not start netcoredbg.");
            var session = new DebugSession(id, process);
            await session.RequestAsync("initialize", new { clientID = "Ali", adapterID = "coreclr", pathFormat = "path", linesStartAt1 = true, columnsStartAt1 = true }, cancellationToken).ConfigureAwait(false);
            return session;
        }

        public async Task<IReadOnlyList<DebugBreakpoint>> LaunchAsync(string artifact, string cwd, bool stopAtEntry, string? document, int? line, CancellationToken cancellationToken)
        {
            var initialized = WaitEventAsync("initialized", cancellationToken);
            var launch = RequestAsync("launch", new { name = "Ali CLR Debug", type = "coreclr", request = "launch", program = artifact, cwd, stopAtEntry }, cancellationToken);
            await initialized.ConfigureAwait(false);
            var breakpoints = document is null ? [] : await SetBreakpointsAsync(document, [line!.Value], cancellationToken).ConfigureAwait(false);
            await RequestAsync("setExceptionBreakpoints", new { filters = new[] { "user-unhandled" } }, cancellationToken).ConfigureAwait(false);
            await RequestAsync("configurationDone", new { }, cancellationToken).ConfigureAwait(false);
            var launchResponse = await launch.ConfigureAwait(false);
            DebuggeeProcessId = launchResponse["body"]?["processId"]?.GetValue<int?>();
            _state = "running";
            return breakpoints;
        }

        public async Task AttachAsync(int processId, CancellationToken cancellationToken)
        {
            var initialized = WaitEventAsync("initialized", cancellationToken);
            var attach = RequestAsync("attach", new { name = "Ali CLR Attach", type = "coreclr", request = "attach", processId }, cancellationToken);
            await initialized.ConfigureAwait(false);
            await RequestAsync("setExceptionBreakpoints", new { filters = new[] { "user-unhandled" } }, cancellationToken).ConfigureAwait(false);
            await RequestAsync("configurationDone", new { }, cancellationToken).ConfigureAwait(false);
            await attach.ConfigureAwait(false);
            DebuggeeProcessId = processId;
            _state = "running";
        }

        public async Task<DotNetDebugSnapshot> InspectAsync(CancellationToken cancellationToken)
        {
            var threadsResponse = await RequestAsync("threads", new { }, cancellationToken).ConfigureAwait(false);
            var threads = new List<DebugThread>();
            var allVariables = new List<DebugVariable>();
            foreach (var node in threadsResponse["body"]?["threads"]?.AsArray() ?? [])
            {
                if (node is null) continue;
                var threadId = node["id"]!.GetValue<int>();
                var framesResponse = await RequestAsync("stackTrace", new { threadId, startFrame = 0, levels = 50 }, cancellationToken).ConfigureAwait(false);
                var frames = new List<DebugStackFrame>();
                foreach (var frameNode in framesResponse["body"]?["stackFrames"]?.AsArray() ?? [])
                {
                    if (frameNode is null) continue;
                    frames.Add(new DebugStackFrame(frameNode["id"]!.GetValue<int>(), frameNode["name"]?.GetValue<string>() ?? "frame",
                        frameNode["source"]?["path"]?.GetValue<string>(), frameNode["line"]?.GetValue<int>() ?? 0, frameNode["column"]?.GetValue<int>() ?? 0));
                }

                threads.Add(new DebugThread(threadId, node["name"]?.GetValue<string>() ?? $"Thread {threadId}", frames));
            }

            var selectedFrame = threads.SelectMany(thread => thread.Frames).FirstOrDefault();
            if (selectedFrame is not null)
            {
                var scopes = await RequestAsync("scopes", new { frameId = selectedFrame.Id }, cancellationToken).ConfigureAwait(false);
                foreach (var scope in scopes["body"]?["scopes"]?.AsArray() ?? [])
                {
                    var reference = scope?["variablesReference"]?.GetValue<int>() ?? 0;
                    if (reference <= 0) continue;
                    var variables = await RequestAsync("variables", new { variablesReference = reference }, cancellationToken).ConfigureAwait(false);
                    allVariables.AddRange((variables["body"]?["variables"]?.AsArray() ?? []).Where(node => node is not null).Select(node =>
                        new DebugVariable(node!["name"]?.GetValue<string>() ?? "value", node["value"]?.GetValue<string>() ?? "", node["type"]?.GetValue<string>(), node["variablesReference"]?.GetValue<int>() ?? 0)));
                }
            }

            return new DotNetDebugSnapshot(true, $"Debugger state: {_state}.", _id, _state, threads, allVariables, _stopReason);
        }

        public async Task<DotNetDebugEvaluation> EvaluateAsync(string expression, int? frameId, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(expression);
            var response = await RequestAsync("evaluate", new { expression, frameId, context = "watch" }, cancellationToken).ConfigureAwait(false);
            var body = response["body"];
            return new DotNetDebugEvaluation(true, "The expression was evaluated by the CLR debugger.", body?["result"]?.GetValue<string>(), body?["type"]?.GetValue<string>(), body?["variablesReference"]?.GetValue<int>() ?? 0);
        }

        public async Task<DotNetDebugControlResult> ControlAsync(string action, CancellationToken cancellationToken)
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
            var threadId = _stoppedThreadId ?? 1;
            await RequestAsync(command, new { threadId }, cancellationToken).ConfigureAwait(false);
            _state = command == "pause" ? "pausing" : "running";
            return new DotNetDebugControlResult(true, $"Debugger command '{command}' was accepted.", _id, _state);
        }

        public async Task<DotNetDebugControlResult> StopAsync(CancellationToken cancellationToken)
        {
            try { await RequestAsync("disconnect", new { restart = false, terminateDebuggee = true }, cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (_process.HasExited) { }
            _state = "terminated";
            return new DotNetDebugControlResult(true, "The debugger and debuggee were terminated.", _id, _state);
        }

        public async Task<IReadOnlyList<DebugBreakpoint>> SetBreakpointsAsync(string document, IReadOnlyList<int> lines, CancellationToken cancellationToken)
        {
            var response = await RequestAsync("setBreakpoints", new { source = new { path = document }, breakpoints = lines.Select(line => new { line }).ToArray(), sourceModified = false }, cancellationToken).ConfigureAwait(false);
            return (response["body"]?["breakpoints"]?.AsArray() ?? []).Where(node => node is not null).Select((node, index) =>
                new DebugBreakpoint(document, lines[Math.Min(index, lines.Count - 1)], node!["verified"]?.GetValue<bool>() ?? false,
                    node["line"]?.GetValue<int?>(), node["message"]?.GetValue<string>())).ToArray();
        }

        private async Task<JsonObject> RequestAsync(string command, object arguments, CancellationToken cancellationToken)
        {
            var sequence = Interlocked.Increment(ref _sequence);
            var completion = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[sequence] = completion;
            await SendAsync(new { seq = sequence, type = "request", command, arguments }, cancellationToken).ConfigureAwait(false);
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            var response = await completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            if (response["success"]?.GetValue<bool>() == false)
            {
                throw new InvalidOperationException(response["message"]?.GetValue<string>() ?? $"Debugger command '{command}' failed.");
            }
            return response;
        }

        private Task<JsonObject> WaitEventAsync(string name, CancellationToken cancellationToken)
        {
            var completion = _events.GetOrAdd(name, _ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously));
            return completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }

        private async Task SendAsync(object message, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOptions);
            var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _input.WriteAsync(header, cancellationToken).ConfigureAwait(false);
                await _input.WriteAsync(body, cancellationToken).ConfigureAwait(false);
                await _input.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var length = await ReadHeaderAsync(_output, _lifetime.Token).ConfigureAwait(false);
                    if (length is null) break;
                    var buffer = new byte[length.Value];
                    await _output.ReadExactlyAsync(buffer, _lifetime.Token).ConfigureAwait(false);
                    var parsed = JsonNode.Parse(buffer);
                    if (parsed is null) continue;
                    var message = parsed.AsObject();
                    var type = message["type"]?.GetValue<string>();
                    if (type == "response" && message["request_seq"]?.GetValue<int>() is int requestSequence && _pending.TryRemove(requestSequence, out var pending))
                    {
                        pending.TrySetResult(message);
                    }
                    else if (type == "event")
                    {
                        var name = message["event"]?.GetValue<string>() ?? "";
                        _events.GetOrAdd(name, _ => new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(message);
                        if (name == "stopped")
                        {
                            _state = "stopped";
                            _stopReason = message["body"]?["reason"]?.GetValue<string>();
                            _stoppedThreadId = message["body"]?["threadId"]?.GetValue<int?>();
                        }
                        else if (name is "terminated" or "exited") _state = "terminated";
                        else if (name == "continued") _state = "running";
                        else if (name == "process") DebuggeeProcessId = message["body"]?["systemProcessId"]?.GetValue<int?>();
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                foreach (var pending in _pending.Values) pending.TrySetException(ex);
            }
        }

        private static async Task<int?> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
        {
            var bytes = new List<byte>(128);
            var current = new byte[1];
            while (bytes.Count < 8192)
            {
                var read = await stream.ReadAsync(current, cancellationToken).ConfigureAwait(false);
                if (read == 0) return null;
                bytes.Add(current[0]);
                if (bytes.Count >= 4 && bytes[^4] == '\r' && bytes[^3] == '\n' && bytes[^2] == '\r' && bytes[^1] == '\n') break;
            }
            var header = Encoding.ASCII.GetString(bytes.ToArray());
            var line = header.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(value => value.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            return int.TryParse(line?["Content-Length:".Length..].Trim(), out var length) ? length : throw new InvalidDataException("Debugger response omitted Content-Length.");
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
            try { await _reader.ConfigureAwait(false); } catch { }
            _process.Dispose();
            _lifetime.Dispose();
            _writeLock.Dispose();
        }
    }
}
