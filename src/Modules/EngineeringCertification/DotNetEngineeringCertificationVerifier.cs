using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Ali.Modules.EngineeringCertification;

/// <summary>
/// Independent fixture verifier. It uses process exit codes plus compiler diagnostic ids; it does
/// not inspect or grade the model's natural-language answer.
/// </summary>
internal sealed partial class DotNetEngineeringCertificationVerifier : IEngineeringCertificationVerifier
{
    private const int MaximumTranscriptCharacters = 131_072;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);

    public async Task<EngineeringVerificationBaseline> CaptureBaselineAsync(
        EngineeringCertificationTask task,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        task.Validate();
        var build = await RunDotNetAsync(
            workspacePath,
            ["build", GetTestProject(workspacePath), "--configuration", "Release", "--nologo", "--verbosity", "minimal"],
            cancellationToken).ConfigureAwait(false);
        var diagnostics = CountRoslynDiagnostics(build.Output);
        return new EngineeringVerificationBaseline(
            diagnostics.Errors,
            diagnostics.Warnings,
            build.Output);
    }

    public async Task<EngineeringVerificationReceipt> VerifyAsync(
        EngineeringCertificationTask task,
        string workspacePath,
        CancellationToken cancellationToken)
    {
        task.Validate();
        var testProject = GetTestProject(workspacePath);
        var build = await RunDotNetAsync(
            workspacePath,
            ["build", testProject, "--configuration", "Release", "--nologo", "--verbosity", "minimal"],
            cancellationToken).ConfigureAwait(false);
        ProcessResult? tests = null;
        if (build.ExitCode == 0)
        {
            tests = await RunDotNetAsync(
                workspacePath,
                ["test", testProject, "--configuration", "Release", "--no-build", "--nologo", "--verbosity", "minimal"],
                cancellationToken).ConfigureAwait(false);
        }

        var diagnostics = CountRoslynDiagnostics(build.Output);
        var hallucinated = build.Output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => HallucinatedApiDiagnostic().IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .Take(100)
            .ToArray();
        var raw = new StringBuilder()
            .AppendLine("=== dotnet build ===")
            .AppendLine(build.Output)
            .AppendLine("=== dotnet test ===")
            .AppendLine(tests?.Output ?? "Tests were not executed because the Release build failed.")
            .ToString();
        return new EngineeringVerificationReceipt(
            build.ExitCode == 0,
            tests?.ExitCode == 0,
            diagnostics.Errors,
            diagnostics.Warnings,
            hallucinated,
            raw);
    }

    internal static (int Errors, int Warnings) CountRoslynDiagnostics(string output)
    {
        var diagnostics = output.ReplaceLineEndings("\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => CompilerDiagnostic().Match(line))
            .Where(match => match.Success)
            .Select(match => new
            {
                Severity = match.Groups[1].Value,
                Id = match.Groups[2].Value,
                Line = match.Value
            })
            .DistinctBy(item => item.Line, StringComparer.Ordinal)
            .ToArray();
        return (
            diagnostics.Count(item => item.Severity.Equals("error", StringComparison.OrdinalIgnoreCase)),
            diagnostics.Count(item => item.Severity.Equals("warning", StringComparison.OrdinalIgnoreCase)));
    }

    private static string GetTestProject(string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var path = Path.GetFullPath(Path.Combine(root, "Fixture.Tests", "Fixture.Tests.csproj"));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path))
        {
            throw new FileNotFoundException("The isolated certification test project is missing.", path);
        }
        return path;
    }

    private static async Task<ProcessResult> RunDotNetAsync(
        string workspacePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host
                       && File.Exists(host)
                ? host
                : "dotnet",
            WorkingDirectory = Path.GetFullPath(workspacePath),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CommandTimeout);
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The .NET verifier process did not start.");
        }

        var standardOutput = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var standardError = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = string.Join(
                Environment.NewLine,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
            return new ProcessResult(process.ExitCode, output);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var remaining = MaximumTranscriptCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }
        return output.ToString();
    }

    [GeneratedRegex(@"\b(error|warning)\s+(CS\d{4})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CompilerDiagnostic();

    [GeneratedRegex(@"\berror\s+(CS0103|CS0117|CS1061|CS0234|CS0246)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HallucinatedApiDiagnostic();

    private sealed record ProcessResult(int ExitCode, string Output);
}
