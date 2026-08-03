using System.Diagnostics;
using System.Xml.Linq;
using Ali.Modules.Coding.Execution;
using Ali.Modules.Coding.Infrastructure;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Modules.Coding.Engineering;

public sealed record EngineeringFailure(
    string Stage,
    string? TestName,
    string Message,
    string? StackTrace,
    string? File,
    int? Line);

public sealed record DotNetTestResult(
    bool Success,
    string TargetPath,
    string Configuration,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    string Summary,
    IReadOnlyList<EngineeringFailure> Failures,
    string? ResultsPath,
    string Output,
    long DurationMilliseconds,
    bool TimedOut);

public sealed record DotNetVerificationResult(
    bool Success,
    string TargetPath,
    string Configuration,
    string Summary,
    DotNetBuildResult Build,
    DotNetTestResult? Tests,
    IReadOnlyList<EngineeringFailure> Failures,
    long DurationMilliseconds);

/// <summary>
/// A bounded build/test verification loop shared by future language adapters. It accepts
/// only approved .NET targets and fixed SDK verbs; source repair stays with Roslyn and the agent.
/// </summary>
internal sealed class AliDotNetEngineeringLoop
{
    private const int MaximumOutputCharacters = 24_000;
    private static readonly SemaphoreSlim TestLock = new(1, 1);
    private static readonly TimeSpan TestProcessTimeout = TimeSpan.FromHours(24);
    private readonly AliCodingProjectResolver _resolver;
    private readonly Action? _beforeTestProcessStart;

    internal AliDotNetEngineeringLoop(
        AliCodingProjectResolver resolver,
        Action? beforeTestProcessStart = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _beforeTestProcessStart = beforeTestProcessStart;
    }

    public async Task<DotNetTestResult> TestAsync(
        string targetPath,
        string? configuration,
        CancellationToken cancellationToken)
    {
        var target = _resolver.ResolveExistingTarget(targetPath);
        var normalizedConfiguration = NormalizeConfiguration(configuration);
        var started = Stopwatch.StartNew();
        var resultDirectory = Path.Combine(target.RootDirectory, ".ali", "test-results");
        WindowsOrchestrationFileBoundary.EnsureRegularDirectoryPath(
            resultDirectory,
            "The .NET test-results path is not a regular local directory.");
        var trxName = $"ali-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.trx";
        var trxPath = Path.Combine(resultDirectory, trxName);

        await TestLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var arguments = new[]
            {
                "test", target.PhysicalPath, "--configuration", normalizedConfiguration,
                "--logger", $"trx;LogFileName={trxName}", "--results-directory", resultDirectory,
                "--nologo"
            };
            var exactHost = AliCodingInvocationExecutionContext
                .ResolveDotNetHostBindingForExecution();
            var execution = _beforeTestProcessStart is null
                ? await AliBoundedProcessRunner.RunAsync(
                        exactHost,
                        target.RootDirectory,
                        arguments,
                        TestProcessTimeout,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await AliBoundedProcessRunner.RunAsync(
                        exactHost,
                        target.RootDirectory,
                        arguments,
                        TestProcessTimeout,
                        cancellationToken,
                        _beforeTestProcessStart)
                    .ConfigureAwait(false);

            started.Stop();
            return ParseResult(
                targetPath,
                normalizedConfiguration,
                trxPath,
                execution.ExitCode,
                execution.Output,
                started.ElapsedMilliseconds,
                execution.TimedOut);
        }
        finally
        {
            TestLock.Release();
        }
    }

    public async Task<DotNetVerificationResult> VerifyAsync(
        string targetPath,
        string? configuration,
        Func<string, string?, CancellationToken, Task<DotNetBuildResult>> build,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.StartNew();
        var buildResult = await build(targetPath, configuration, cancellationToken).ConfigureAwait(false);
        if (!buildResult.Success)
        {
            started.Stop();
            var failure = new EngineeringFailure("build", null, buildResult.Summary, buildResult.Output, null, null);
            return new DotNetVerificationResult(false, targetPath, buildResult.Configuration,
                "Verification stopped because the build failed.", buildResult, null, [failure], started.ElapsedMilliseconds);
        }

        var tests = await TestAsync(targetPath, configuration, cancellationToken).ConfigureAwait(false);
        started.Stop();
        return new DotNetVerificationResult(tests.Success, targetPath, buildResult.Configuration,
            tests.Success ? "Build and tests passed." : "Build passed, but one or more tests failed.",
            buildResult, tests, tests.Failures, started.ElapsedMilliseconds);
    }

    private static DotNetTestResult ParseResult(
        string targetPath,
        string configuration,
        string trxPath,
        int exitCode,
        string output,
        long duration,
        bool timedOut)
    {
        if (!File.Exists(trxPath))
        {
            return new DotNetTestResult(false, targetPath, configuration, 0, 0, 0, 0,
                timedOut
                    ? "The bounded test process timed out before producing a TRX result artifact."
                    : "The test host did not produce a TRX result artifact.",
                [new EngineeringFailure(
                    "test",
                    null,
                    timedOut ? "The bounded test process timed out." : "No TRX result was produced.",
                    Compact(output),
                    null,
                    null)],
                null, Compact(output), duration, timedOut);
        }

        var document = XDocument.Load(trxPath);
        var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
        var total = ReadInt(counters, "total");
        var passed = ReadInt(counters, "passed");
        var failed = ReadInt(counters, "failed");
        var skipped = ReadInt(counters, "notExecuted");
        var failures = document.Descendants()
            .Where(element => element.Name.LocalName == "UnitTestResult" &&
                string.Equals((string?)element.Attribute("outcome"), "Failed", StringComparison.OrdinalIgnoreCase))
            .Select(element => new EngineeringFailure(
                "test",
                (string?)element.Attribute("testName"),
                element.Descendants().FirstOrDefault(item => item.Name.LocalName == "Message")?.Value ?? "Test failed.",
                element.Descendants().FirstOrDefault(item => item.Name.LocalName == "StackTrace")?.Value,
                null,
                null))
            .ToArray();
        var success = exitCode == 0 && failed == 0 && total > 0;
        return new DotNetTestResult(success, targetPath, configuration, total, passed, failed, skipped,
            success ? $"All {passed} discovered test(s) passed."
                : total == 0 ? "No tests were discovered; verification cannot claim success."
                : $"{failed} of {total} discovered test(s) failed.",
            failures, trxPath, Compact(output), duration, timedOut);
    }

    private static int ReadInt(XElement? element, string name) =>
        int.TryParse((string?)element?.Attribute(name), out var value) ? value : 0;

    private static string NormalizeConfiguration(string? value) => string.IsNullOrWhiteSpace(value) ? "Debug" : value.Trim() switch
    {
        var text when text.Equals("Debug", StringComparison.OrdinalIgnoreCase) => "Debug",
        var text when text.Equals("Release", StringComparison.OrdinalIgnoreCase) => "Release",
        _ => throw new ArgumentException("Configuration must be Debug or Release.", nameof(value))
    };

    private static string Compact(string output)
    {
        var normalized = output.ReplaceLineEndings(Environment.NewLine).Trim();
        return normalized.Length <= MaximumOutputCharacters ? normalized : normalized[^MaximumOutputCharacters..];
    }
}
