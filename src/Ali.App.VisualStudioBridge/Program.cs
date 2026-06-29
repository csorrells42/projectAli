using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using System.Text.Json;

var options = BridgeOptions.Parse(args);
if (options.ShowHelp)
{
    Console.WriteLine(BridgeHelp.Text);
    return 0;
}

if (options.ListExternalTools)
{
    Console.WriteLine(VisualStudioExternalToolsGuide.Build());
    return 0;
}

if (!Uri.TryCreate(options.HelperUrl, UriKind.Absolute, out var helperUri)
    || !IsLoopbackHttp(helperUri))
{
    Console.Error.WriteLine("Ali Visual Studio Bridge only connects to loopback http://127.0.0.1 or http://localhost helper URLs.");
    return 2;
}

using var http = new HttpClient
{
    BaseAddress = helperUri,
    Timeout = TimeSpan.FromSeconds(15)
};
http.DefaultRequestHeaders.UserAgent.ParseAdd("AliVisualStudioBridge/1.0");
if (!string.IsNullOrWhiteSpace(options.Token))
{
    http.DefaultRequestHeaders.Add("X-Ali-Helper-Token", options.Token);
}

try
{
    if (!await IsHelperReachableAsync(http).ConfigureAwait(false))
    {
        if (!options.StartHelper)
        {
            Console.Error.WriteLine("Ali WebHelper is not reachable. Start it first or omit --no-start-helper.");
            return 2;
        }

        var started = await TryStartHelperAsync(options, helperUri, http).ConfigureAwait(false);
        if (!started)
        {
            Console.Error.WriteLine("Ali WebHelper is not reachable and the bridge could not start it automatically.");
            return 2;
        }
    }

    CodingCommandResponse response;
    if (options.StatusOnly)
    {
        response = await ReadStatusAsync(http).ConfigureAwait(false);
    }
    else
    {
        var command = options.BuildCommand();
        if (string.IsNullOrWhiteSpace(command))
        {
            Console.Error.WriteLine("No Ali coding command was provided.");
            Console.WriteLine(BridgeHelp.Text);
            return 2;
        }

        response = await SendCommandAsync(http, command).ConfigureAwait(false);
    }

    Console.WriteLine(response.Message);
    return response.Succeeded ? 0 : 1;
}
catch (HttpRequestException ex)
{
    Console.Error.WriteLine($"Ali Visual Studio Bridge could not reach the local helper: {ex.Message}");
    return 2;
}
catch (TaskCanceledException ex)
{
    Console.Error.WriteLine($"Ali Visual Studio Bridge timed out: {ex.Message}");
    return 2;
}

static async Task<bool> IsHelperReachableAsync(HttpClient http)
{
    try
    {
        using var response = await http.GetAsync("/api/status").ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }
    catch (HttpRequestException)
    {
        return false;
    }
    catch (TaskCanceledException)
    {
        return false;
    }
}

static async Task<bool> TryStartHelperAsync(BridgeOptions options, Uri helperUri, HttpClient http)
{
    var projectPath = options.HelperProjectPath;
    if (string.IsNullOrWhiteSpace(projectPath))
    {
        projectPath = FindHelperProjectPath();
    }

    if (string.IsNullOrWhiteSpace(projectPath) || !File.Exists(projectPath))
    {
        return false;
    }

    var listenUrl = helperUri.GetLeftPart(UriPartial.Authority);
    var startInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        UseShellExecute = false,
        CreateNoWindow = true,
        WorkingDirectory = Path.GetDirectoryName(projectPath) ?? Environment.CurrentDirectory
    };
    startInfo.ArgumentList.Add("run");
    startInfo.ArgumentList.Add("--project");
    startInfo.ArgumentList.Add(projectPath);
    startInfo.ArgumentList.Add("--no-build");
    startInfo.Environment["ALI_HELPER_URLS"] = listenUrl;

    if (!string.IsNullOrWhiteSpace(options.Token))
    {
        startInfo.Environment["ALI_HELPER_TOKEN"] = options.Token;
    }

    try
    {
        Process.Start(startInfo);
    }
    catch
    {
        return false;
    }

    for (var attempt = 0; attempt < 20; attempt++)
    {
        await Task.Delay(500).ConfigureAwait(false);
        if (await IsHelperReachableAsync(http).ConfigureAwait(false))
        {
            return true;
        }
    }

    return false;
}

static string? FindHelperProjectPath()
{
    var current = AppContext.BaseDirectory;
    for (var depth = 0; depth < 12 && !string.IsNullOrWhiteSpace(current); depth++)
    {
        var candidate = Path.Combine(current, "src", "Ali.App.WebHelper", "Ali.App.WebHelper.csproj");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        candidate = Path.Combine(current, "..", "Ali.App.WebHelper", "Ali.App.WebHelper.csproj");
        candidate = Path.GetFullPath(candidate);
        if (File.Exists(candidate))
        {
            return candidate;
        }

        current = Directory.GetParent(current)?.FullName;
    }

    return null;
}

static async Task<CodingCommandResponse> ReadStatusAsync(HttpClient http)
{
    using var response = await http.GetAsync("/api/coding/status").ConfigureAwait(false);
    return await ReadBridgeResponseAsync(response).ConfigureAwait(false);
}

static async Task<CodingCommandResponse> SendCommandAsync(HttpClient http, string command)
{
    using var response = await http.PostAsJsonAsync("/api/coding/command", new CodingCommandRequest(command)).ConfigureAwait(false);
    return await ReadBridgeResponseAsync(response).ConfigureAwait(false);
}

static async Task<CodingCommandResponse> ReadBridgeResponseAsync(HttpResponseMessage response)
{
    var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    if (!response.IsSuccessStatusCode)
    {
        var error = TryReadError(text) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        return new CodingCommandResponse(true, false, error, "Ali coding bridge", null, null, null);
    }

    var result = JsonSerializer.Deserialize<CodingCommandResponse>(
        text,
        new JsonSerializerOptions(JsonSerializerDefaults.Web));
    return result ?? new CodingCommandResponse(true, false, "Ali coding bridge returned an empty response.", "Ali coding bridge", null, null, null);
}

static string? TryReadError(string text)
{
    try
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.TryGetProperty("error", out var error)
            ? error.GetString()
            : null;
    }
    catch (JsonException)
    {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}

static bool IsLoopbackHttp(Uri uri)
{
    if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    if (IPAddress.TryParse(uri.Host, out var address))
    {
        return IPAddress.IsLoopback(address);
    }

    return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}

internal sealed record BridgeOptions(
    string HelperUrl,
    string? Token,
    string? Command,
    string? SolutionPath,
    string? FilePath,
    int? LineNumber,
    string? HelperProjectPath,
    bool StartHelper,
    bool StatusOnly,
    bool Handoff,
    string? Preset,
    bool ReadCurrentFile,
    bool OpenCurrentFile,
    bool ListExternalTools,
    bool ShowHelp)
{
    public static BridgeOptions Parse(IReadOnlyList<string> args)
    {
        var helperUrl = Environment.GetEnvironmentVariable("ALI_HELPER_URL") ?? "http://127.0.0.1:8765";
        var token = Environment.GetEnvironmentVariable("ALI_HELPER_TOKEN");
        string? command = null;
        string? solution = null;
        string? file = null;
        int? line = null;
        var status = false;
        var handoff = false;
        string? preset = null;
        var readCurrentFile = false;
        var openCurrentFile = false;
        var listExternalTools = false;
        var startHelper = true;
        string? helperProjectPath = null;
        var showHelp = args.Count == 0;
        var remainder = new List<string>();

        for (var index = 0; index < args.Count; index++)
        {
            var arg = args[index];
            switch (arg.ToLowerInvariant())
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;
                case "--helper-url":
                    helperUrl = ReadValue(args, ref index, arg);
                    break;
                case "--token":
                    token = ReadValue(args, ref index, arg);
                    break;
                case "--command":
                    command = ReadValue(args, ref index, arg);
                    break;
                case "--solution":
                    solution = ReadValue(args, ref index, arg);
                    break;
                case "--file":
                    file = ReadValue(args, ref index, arg);
                    break;
                case "--line":
                    if (int.TryParse(ReadValue(args, ref index, arg), out var parsedLine) && parsedLine > 0)
                    {
                        line = parsedLine;
                    }
                    break;
                case "--helper-project":
                    helperProjectPath = ReadValue(args, ref index, arg);
                    break;
                case "--no-start-helper":
                    startHelper = false;
                    break;
                case "--status":
                    status = true;
                    break;
                case "--handoff":
                    handoff = true;
                    break;
                case "--preset":
                    preset = ReadValue(args, ref index, arg);
                    break;
                case "--list-external-tools":
                case "--external-tools":
                    listExternalTools = true;
                    break;
                case "--read-current-file":
                    readCurrentFile = true;
                    break;
                case "--open-current-file":
                    openCurrentFile = true;
                    break;
                default:
                    remainder.Add(arg);
                    break;
            }
        }

        if (command is null && remainder.Count > 0)
        {
            command = string.Join(" ", remainder);
        }

        return new BridgeOptions(
            helperUrl.TrimEnd('/'),
            token,
            command,
            solution,
            file,
            line,
            helperProjectPath,
            startHelper,
            status,
            handoff,
            preset,
            readCurrentFile,
            openCurrentFile,
            listExternalTools,
            showHelp);
    }

    public string? BuildCommand()
    {
        if (Handoff)
        {
            return "generate visual studio integration plan";
        }

        if (!string.IsNullOrWhiteSpace(Preset))
        {
            return BuildPresetCommand(Preset);
        }

        if (ReadCurrentFile)
        {
            return string.IsNullOrWhiteSpace(FilePath)
                ? null
                : WithLine($"read file \"{FilePath}\"");
        }

        if (OpenCurrentFile)
        {
            return string.IsNullOrWhiteSpace(FilePath)
                ? null
                : WithLine($"open file \"{FilePath}\"");
        }

        if (!string.IsNullOrWhiteSpace(Command))
        {
            return ExpandTokens(Command);
        }

        if (!string.IsNullOrWhiteSpace(FilePath))
        {
            return WithLine($"read file \"{FilePath}\"");
        }

        return !string.IsNullOrWhiteSpace(SolutionPath)
            ? $"open solution \"{SolutionPath}\""
            : null;
    }

    private string? BuildPresetCommand(string preset)
    {
        var normalized = NormalizePreset(preset);
        return normalized switch
        {
            "status" => "show visual studio integration",
            "architecture" => "analyze solution architecture",
            "map" => "show project map",
            "packages" => "list packages",
            "goal" => "interpret build goal Visual Studio companion upgrade",
            "options" => "show architecture options Visual Studio companion upgrade",
            "criteria" => "write acceptance criteria Visual Studio companion upgrade",
            "test-plan" => "suggest tests for Visual Studio companion upgrade",
            "patterns" => "detect codebase patterns",
            "feature-files" => "plan feature files Visual Studio companion upgrade",
            "safety" => "show refactor safety checklist Visual Studio companion upgrade",
            "next-action" => "show next coding action",
            "packet" => "show execution packet",
            "approve-packet" => "approve execution packet",
            "packet-console" => "show packet commands",
            "packet-ledger" => "show packet ledger",
            "packet-progress" => "show packet progress",
            "resume-build" => "resume build plan",
            "package-lookup" => "plan package lookup Visual Studio tool window",
            "install-packet" => "plan dependency install packet Visual Studio tool window",
            "scaffold" => "preview project scaffold Visual Studio companion upgrade",
            "scaffold-apply" => "plan scaffold apply Visual Studio companion upgrade",
            "validate" => "plan post edit validation",
            "windows-toolkit" => "show windows troubleshooting toolkit",
            "process-evidence" => "collect process evidence dotnet",
            "port-owner" => "diagnose port 8765",
            "process-hunt" => "plan rogue process hunt port 8765",
            "services-startup" => "inspect services and startup",
            "event-logs" => "triage event logs",
            "build-lock" => "diagnose build lock",
            "failure-class" => "classify last build failure",
            "step-check" => "show roadmap step checklist",
            "install-doctor" => "show install doctor",
            "roadmap" => "show active roadmap step",
            "recovery" => "show crash recovery status",
            "skill-index" => "show coding skill command index",
            "session-summary" => "show coding session summary",
            "receipts" => "show coding receipts",
            "report" => "generate coding report",
            "morning-report" => "generate morning report",
            "git-status" => "git status",
            "git-diff" => "git diff",
            "build" => string.IsNullOrWhiteSpace(SolutionPath) ? null : $"confirm dotnet build \"{SolutionPath}\"",
            "test" => string.IsNullOrWhiteSpace(SolutionPath) ? null : $"confirm dotnet test \"{SolutionPath}\"",
            "read-file" => string.IsNullOrWhiteSpace(FilePath) ? null : WithLine($"read file \"{FilePath}\""),
            "open-file" => string.IsNullOrWhiteSpace(FilePath) ? null : WithLine($"open file \"{FilePath}\""),
            _ => null
        };
    }

    private static string NormalizePreset(string preset) =>
        preset.Trim().ToLowerInvariant() switch
        {
            "analyze" or "analyze-solution" or "architecture" => "architecture",
            "project-map" or "workspace-map" or "map" => "map",
            "package" or "packages" => "packages",
            "active-step" or "roadmap-step" or "roadmap" => "roadmap",
            "crash-recovery" or "recovery" => "recovery",
            "coding-receipts" or "receipts" => "receipts",
            "coding-report" or "report" => "report",
            "gitstatus" or "git-status" => "git-status",
            "gitdiff" or "git-diff" => "git-diff",
            "build-solution" or "build" => "build",
            "test-solution" or "test" => "test",
            "read-current-file" or "read-file" => "read-file",
            "open-current-file" or "open-file" => "open-file",
            "status" => "status",
            var value => value
        };

    private string WithLine(string command) =>
        LineNumber is > 0
            ? $"{command} at line {LineNumber.Value}"
            : command;

    private string ExpandTokens(string command)
    {
        var solutionDirectory = string.IsNullOrWhiteSpace(SolutionPath)
            ? string.Empty
            : Path.GetDirectoryName(SolutionPath) ?? string.Empty;
        return command
            .Replace("{solution}", SolutionPath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{solutionDir}", solutionDirectory, StringComparison.OrdinalIgnoreCase)
            .Replace("{file}", FilePath ?? string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("{line}", LineNumber?.ToString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadValue(IReadOnlyList<string> args, ref int index, string option)
    {
        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"{option} needs a value.");
        }

        index++;
        return args[index];
    }
}

internal sealed record CodingCommandRequest(string Command);

internal sealed record CodingCommandResponse(
    bool Handled,
    bool Succeeded,
    string Message,
    string? ToolName,
    string? TargetPath,
    int? LineNumber,
    int? ExitCode);

internal static class BridgeHelp
{
    public const string Text = """
Ali Visual Studio Bridge

Connects Visual Studio External Tools or a future VSIX to Ali's local coding bridge.
Ali must have the WebHelper running on loopback, for example:
  dotnet run --project .\src\Ali.App.WebHelper\Ali.App.WebHelper.csproj

Examples:
  Ali.App.VisualStudioBridge.exe --status
  Ali.App.VisualStudioBridge.exe --handoff
  Ali.App.VisualStudioBridge.exe --list-external-tools
  Ali.App.VisualStudioBridge.exe --preset recovery
  Ali.App.VisualStudioBridge.exe --preset build --solution "$(SolutionPath)"
  Ali.App.VisualStudioBridge.exe --command "analyze solution architecture"
  Ali.App.VisualStudioBridge.exe --read-current-file --file "$(ItemPath)" --line "$(CurLine)"
  Ali.App.VisualStudioBridge.exe --command "search workspace for WidgetFactory"
  Ali.App.VisualStudioBridge.exe --command "read file \"{file}\" at line {line}" --file "$(ItemPath)" --line "$(CurLine)"

Options:
  --helper-url <url>        Default: ALI_HELPER_URL or http://127.0.0.1:8765
  --token <token>           Default: ALI_HELPER_TOKEN
  --helper-project <path>   Optional path to Ali.App.WebHelper.csproj for auto-start.
  --no-start-helper         Do not auto-start the local WebHelper if it is offline.
  --status                  Show Ali coding tool status.
  --handoff                 Generate the Visual Studio integration handoff.
  --preset <name>           Run a named Visual Studio preset.
  --list-external-tools     Print recommended Visual Studio External Tools entries.
  --command <command>       Send a deterministic Ali coding command.
  --solution <path>         Visual Studio solution path for token expansion.
  --file <path>             Current file path for token expansion.
  --line <number>           Current line number for token expansion.
  --read-current-file       Read the current Visual Studio file through Ali.
  --open-current-file       Open the current Visual Studio file through Ali.
""";
}

internal static class VisualStudioExternalToolsGuide
{
    private static readonly ExternalToolPreset[] Presets =
    [
        new("Ali Status", "--status", "Show workspace, tool discovery, and permission gates."),
        new("Ali Architecture", "--preset architecture --solution \"$(SolutionPath)\"", "Analyze the active solution architecture."),
        new("Ali Project Map", "--preset map --solution \"$(SolutionPath)\"", "Show the coding workspace project map."),
        new("Ali Packages", "--preset packages --solution \"$(SolutionPath)\"", "List package references in the approved workspace."),
        new("Ali Build Solution", "--preset build --solution \"$(SolutionPath)\"", "Run a confirmed guarded dotnet build for the active solution."),
        new("Ali Test Solution", "--preset test --solution \"$(SolutionPath)\"", "Run a confirmed guarded dotnet test for the active solution."),
        new("Ali Next Action", "--preset next-action --solution \"$(SolutionPath)\"", "Show Ali's next guarded coding action."),
        new("Ali Execution Packet", "--preset packet --solution \"$(SolutionPath)\"", "Show the guarded command packet for the current roadmap step."),
        new("Ali Approve Packet", "--preset approve-packet --solution \"$(SolutionPath)\"", "Approve the current execution packet as local planning state."),
        new("Ali Packet Console", "--preset packet-console --solution \"$(SolutionPath)\"", "Show numbered commands from the approved packet."),
        new("Ali Packet Ledger", "--preset packet-ledger --solution \"$(SolutionPath)\"", "Show receipts since packet approval."),
        new("Ali Packet Progress", "--preset packet-progress --solution \"$(SolutionPath)\"", "Show progress for the approved execution packet."),
        new("Ali Resume Build Plan", "--preset resume-build --solution \"$(SolutionPath)\"", "Resume after crash or interruption using roadmap, packet, receipts, and Git state."),
        new("Ali Package Lookup Plan", "--preset package-lookup --solution \"$(SolutionPath)\"", "Plan dependency lookup and risk cards without installing packages."),
        new("Ali Windows Toolkit", "--preset windows-toolkit --solution \"$(SolutionPath)\"", "Show safe PowerShell and CMD troubleshooting guidance."),
        new("Ali Process Evidence", "--preset process-evidence --solution \"$(SolutionPath)\"", "Collect read-only evidence for common process suspects."),
        new("Ali Port Owner", "--preset port-owner --solution \"$(SolutionPath)\"", "Map a port to owning process evidence."),
        new("Ali Build Lock", "--preset build-lock --solution \"$(SolutionPath)\"", "Diagnose common build-lock suspect processes."),
        new("Ali Install Doctor", "--preset install-doctor --solution \"$(SolutionPath)\"", "Check Ali developer install readiness without changing files."),
        new("Ali Roadmap Step", "--preset roadmap --solution \"$(SolutionPath)\"", "Show the active roadmap step."),
        new("Ali Crash Recovery", "--preset recovery --solution \"$(SolutionPath)\"", "Diagnose roadmap, receipts, interrupted validation, and Git state after a crash."),
        new("Ali Read Current File", "--preset read-file --file \"$(ItemPath)\" --line \"$(CurLine)\"", "Read the current file through Ali."),
        new("Ali Open Current File", "--preset open-file --file \"$(ItemPath)\" --line \"$(CurLine)\"", "Open the current file through Ali's configured launcher."),
        new("Ali Git Status", "--preset git-status --solution \"$(SolutionPath)\"", "Run guarded read-only git status."),
        new("Ali Coding Report", "--preset report --solution \"$(SolutionPath)\"", "Generate a local coding session report PDF."),
        new("Ali Morning Report", "--preset morning-report --solution \"$(SolutionPath)\"", "Generate a local morning build report PDF.")
    ];

    public static string Build()
    {
        var bridgePath = Environment.ProcessPath ?? "Ali.App.VisualStudioBridge.exe";
        var lines = new List<string>
        {
            "Ali Visual Studio External Tools setup:",
            "Add these entries in Visual Studio: Tools > External Tools...",
            $"Command for each entry: {bridgePath}",
            "Initial directory: $(SolutionDir)",
            "Check 'Use Output window' so Ali's response appears inside Visual Studio.",
            "Leave 'Close on exit' unchecked while testing.",
            string.Empty
        };

        for (var index = 0; index < Presets.Length; index++)
        {
            var preset = Presets[index];
            lines.Add($"{index + 1}. {preset.Title}");
            lines.Add($"   Arguments: {preset.Arguments}");
            lines.Add($"   Purpose: {preset.Description}");
        }

        lines.Add(string.Empty);
        lines.Add("Notes:");
        lines.Add("- The bridge talks only to Ali's loopback WebHelper and uses Ali's existing confirmation gates.");
        lines.Add("- Build/test/package/git-write commands still require Ali-side confirmation policy approval.");
        lines.Add("- This is External Tools integration, not a Visual Studio extension or in-IDE panel.");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ExternalToolPreset(
        string Title,
        string Arguments,
        string Description);
}
