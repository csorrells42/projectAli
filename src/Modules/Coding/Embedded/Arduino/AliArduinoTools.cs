using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Coding.Languages;
using Ali.Modules.Coding.Toolchains;
using Ali.Modules.WorkstationFiles;

namespace Ali.Modules.Coding.Embedded.Arduino;

public sealed record AliArduinoReport(
    bool Success,
    string Summary,
    string? Cli,
    string? Ide,
    string CliVersion,
    IReadOnlyList<string> InstalledCores,
    IReadOnlyList<string> ConnectedBoards,
    IReadOnlyList<string> Capabilities);

public sealed record AliArduinoOperationResult(
    bool Success,
    string Operation,
    string Summary,
    int? ExitCode,
    long DurationMilliseconds,
    string Output,
    IReadOnlyList<string> Artifacts);

internal sealed class AliArduinoTools(
    AliLanguageProjectResolver resolver,
    AliWorkstationFileAccess fileAccess)
{
    private static readonly Regex SafeIdentifier = new("^[A-Za-z0-9_.:@/+-]{1,160}$", RegexOptions.CultureInvariant);

    public async Task<AliArduinoReport> InspectAsync(CancellationToken cancellationToken)
    {
        var cli = File.Exists(AliDeveloperToolPaths.ArduinoCli) ? AliDeveloperToolPaths.ArduinoCli : null;
        var ide = File.Exists(AliDeveloperToolPaths.ArduinoIde) ? AliDeveloperToolPaths.ArduinoIde : null;
        if (cli is null)
            return new(false, "Arduino CLI is not provisioned. Run tools/RestoreCodingToolchains.ps1.", null, ide, "unavailable", [], [], Capabilities());

        var version = await RunCliAsync(["version", "--format", "json"], TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var cores = await RunCliAsync(["core", "list"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        var boards = await RunCliAsync(["board", "list", "--format", "json"], TimeSpan.FromSeconds(45), cancellationToken).ConfigureAwait(false);
        return new(
            version.Success && cores.Success,
            "Arduino CLI and IDE share one standard Arduino15 package/library store; compile, upload, core, board, and library operations are ready.",
            cli,
            ide,
            ExtractVersion(version.Output),
            ExtractCoreIds(cores.Output),
            ExtractNames(boards.Output, "matching_boards", "name"),
            Capabilities());
    }

    public async Task<AliArduinoOperationResult> SearchLibrariesAsync(string query, CancellationToken cancellationToken)
    {
        Validate(query, nameof(query));
        var result = await RunCliAsync(["lib", "search", query, "--format", "json"], TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        return From(result, "search_libraries", result.Success ? "Arduino Library Manager returned matching libraries." : "Arduino library search failed.");
    }

    public async Task<AliArduinoOperationResult> InstallCoreAsync(string core, CancellationToken cancellationToken)
    {
        Validate(core, nameof(core));
        var result = await RunCliAsync(["core", "install", core], TimeSpan.FromMinutes(45), cancellationToken).ConfigureAwait(false);
        return From(result, "install_core", result.Success ? $"Arduino core {core} is installed." : $"Arduino core {core} could not be installed.");
    }

    public async Task<AliArduinoOperationResult> InstallLibraryAsync(string library, string? version, CancellationToken cancellationToken)
    {
        Validate(library, nameof(library));
        if (!string.IsNullOrWhiteSpace(version)) Validate(version, nameof(version));
        var spec = string.IsNullOrWhiteSpace(version) ? library : $"{library}@{version}";
        var result = await RunCliAsync(["lib", "install", spec], TimeSpan.FromMinutes(30), cancellationToken).ConfigureAwait(false);
        return From(result, "install_library", result.Success ? $"Arduino library {spec} is installed." : $"Arduino library {spec} could not be installed.");
    }

    public async Task<AliArduinoOperationResult> CreateAndCompileAsync(
        string sketchPath,
        string source,
        string fqbn,
        CancellationToken cancellationToken)
    {
        Validate(fqbn, nameof(fqbn));
        if (string.IsNullOrWhiteSpace(source) || source.Length > 200_000)
        {
            throw new ArgumentException("Provide a non-empty Arduino sketch no larger than 200,000 characters.", nameof(source));
        }

        var resolved = fileAccess.ResolvePhysicalFilePath(sketchPath);
        if (!Path.GetExtension(resolved.PhysicalPath).Equals(".ino", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The new Arduino sketch path must end in .ino.", nameof(sketchPath));
        }

        var sketchName = Path.GetFileNameWithoutExtension(resolved.PhysicalPath);
        var directoryName = Path.GetFileName(Path.GetDirectoryName(resolved.PhysicalPath));
        if (!string.Equals(sketchName, directoryName, StringComparison.OrdinalIgnoreCase))
        {
            return new AliArduinoOperationResult(
                false,
                "create_and_compile",
                "Arduino requires the main .ino filename to match its parent sketch folder.",
                null,
                0,
                $"Use a path such as Desktop/{sketchName}/{sketchName}.ino.",
                []);
        }

        if (File.Exists(resolved.PhysicalPath))
        {
            return new AliArduinoOperationResult(
                false,
                "create_and_compile",
                "The target sketch already exists; Ali did not overwrite it.",
                null,
                0,
                resolved.PhysicalPath,
                [resolved.PhysicalPath]);
        }

        await fileAccess.Store.WriteAsync(sketchPath, source, cancellationToken).ConfigureAwait(false);
        var compiled = await CompileOrUploadAsync(sketchPath, fqbn, null, upload: false, cancellationToken).ConfigureAwait(false);
        var artifacts = new[] { resolved.PhysicalPath }
            .Concat(compiled.Artifacts)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return compiled with
        {
            Operation = "create_and_compile",
            Summary = compiled.Success
                ? $"Created {sketchPath} and compiled it successfully for {fqbn}."
                : $"Created {sketchPath}, but its {fqbn} compilation failed; the compiler diagnostics are included.",
            Artifacts = artifacts
        };
    }

    public Task<AliArduinoOperationResult> CompileAsync(string sketchPath, string fqbn, CancellationToken cancellationToken) =>
        CompileOrUploadAsync(sketchPath, fqbn, null, false, cancellationToken);

    public Task<AliArduinoOperationResult> UploadAsync(string sketchPath, string fqbn, string port, CancellationToken cancellationToken) =>
        CompileOrUploadAsync(sketchPath, fqbn, port, true, cancellationToken);

    public AliArduinoOperationResult OpenIde(string sketchPath)
    {
        var project = resolver.Resolve(sketchPath);
        if (project.Language != AliProgrammingLanguage.Arduino)
            throw new ArgumentException("The target must be an Arduino .ino file or sketch.yaml.", nameof(sketchPath));
        if (!File.Exists(AliDeveloperToolPaths.ArduinoIde))
            return new(false, "open_ide", "Arduino IDE is not installed.", null, 0, string.Empty, []);
        Process.Start(new ProcessStartInfo(AliDeveloperToolPaths.ArduinoIde)
        {
            UseShellExecute = true,
            WorkingDirectory = project.ProjectDirectory,
            ArgumentList = { project.PhysicalPath }
        });
        return new(true, "open_ide", "Arduino IDE opened the approved sketch.", 0, 0, project.PhysicalPath, []);
    }

    private async Task<AliArduinoOperationResult> CompileOrUploadAsync(
        string sketchPath, string fqbn, string? port, bool upload, CancellationToken cancellationToken)
    {
        Validate(fqbn, nameof(fqbn));
        if (upload) Validate(port!, nameof(port));
        var project = resolver.Resolve(sketchPath);
        if (project.Language != AliProgrammingLanguage.Arduino)
            throw new ArgumentException("The target must be an Arduino .ino file or sketch.yaml.", nameof(sketchPath));
        var output = Path.Combine(project.ProjectDirectory, ".ali", "arduino", Sanitize(fqbn));
        Directory.CreateDirectory(output);
        IReadOnlyList<string> args = upload
            ? ["upload", "--fqbn", fqbn, "--port", port!, "--input-dir", output, project.ProjectDirectory]
            : ["compile", "--fqbn", fqbn, "--output-dir", output, "--warnings", "all", project.ProjectDirectory];
        var result = await RunCliAsync(args, TimeSpan.FromMinutes(upload ? 10 : 30), cancellationToken).ConfigureAwait(false);
        var artifacts = Directory.Exists(output)
            ? Directory.EnumerateFiles(output, "*", SearchOption.TopDirectoryOnly).Take(100).ToArray()
            : [];
        var operation = upload ? "upload" : "compile";
        var summary = result.Success
            ? upload ? $"Arduino CLI uploaded the {fqbn} firmware to {port}." : $"Arduino CLI compiled the sketch for {fqbn}."
            : upload ? "Arduino upload failed; no hardware success is claimed." : "Arduino compilation failed.";
        return From(result, operation, summary, artifacts);
    }

    private static Task<BoundedProcessResult> RunCliAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(AliDeveloperToolPaths.ArduinoCli))
            throw new InvalidOperationException("Arduino CLI is not installed. Run tools/RestoreCodingToolchains.ps1.");
        var args = new List<string> { "--config-file", AliDeveloperToolPaths.ArduinoConfiguration };
        args.AddRange(arguments);
        return AliBoundedProcessRunner.RunAsync(AliDeveloperToolPaths.ArduinoCli, AliDeveloperToolPaths.ToolchainRoot, args, timeout, cancellationToken);
    }

    private static AliArduinoOperationResult From(BoundedProcessResult result, string operation, string summary, IReadOnlyList<string>? artifacts = null) =>
        new(result.Success, operation, summary, result.ExitCode, result.DurationMilliseconds, result.Output, artifacts ?? []);

    private static IReadOnlyList<string> Capabilities() =>
    [
        "Arduino IDE editing and board/library browsing",
        "Arduino CLI board discovery, compile, upload, core and library management",
        "AVR/Uno libraries and official mbed RP2040/Pico libraries",
        "Explicit FQBN and serial-port selection; no guessed hardware uploads"
    ];

    private static void Validate(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifier.IsMatch(value))
            throw new ArgumentException("Use a bounded Arduino identifier without shell syntax or whitespace.", parameter);
    }

    private static string Sanitize(string value) => Regex.Replace(value, "[^A-Za-z0-9_.-]", "_");

    private static string ExtractVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            foreach (var name in new[] { "VersionString", "version", "Version" })
                if (document.RootElement.TryGetProperty(name, out var value)) return value.ToString();
        }
        catch { }
        return json.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown";
    }

    private static IReadOnlyList<string> ExtractNames(string json, params string[] propertyNames)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var document = JsonDocument.Parse(json);
            Walk(document.RootElement);
        }
        catch { }
        return values.Order(StringComparer.OrdinalIgnoreCase).Take(200).ToArray();

        void Walk(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in element.EnumerateObject())
                {
                    if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                        values.Add(property.Value.GetString()!);
                    Walk(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
                foreach (var child in element.EnumerateArray()) Walk(child);
        }
    }

    private static IReadOnlyList<string> ExtractCoreIds(string output) => output
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
        .Where(value => value?.Contains(':', StringComparison.Ordinal) == true)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Cast<string>()
        .ToArray();
}
