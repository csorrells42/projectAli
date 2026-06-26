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
    bool ReadCurrentFile,
    bool OpenCurrentFile,
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
        var readCurrentFile = false;
        var openCurrentFile = false;
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
            readCurrentFile,
            openCurrentFile,
            showHelp);
    }

    public string? BuildCommand()
    {
        if (Handoff)
        {
            return "generate visual studio integration plan";
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
  --command <command>       Send a deterministic Ali coding command.
  --solution <path>         Visual Studio solution path for token expansion.
  --file <path>             Current file path for token expansion.
  --line <number>           Current line number for token expansion.
  --read-current-file       Read the current Visual Studio file through Ali.
  --open-current-file       Open the current Visual Studio file through Ali.
""";
}
