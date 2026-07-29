using Ali.Modules.Coding.Architecture;
using Ali.Modules.Coding.Engineering;
using Ali.Modules.Coding.Quality;
using Ali.Modules.Coding.Release;
using Ali.Modules.Coding.Verification;

namespace Ali.Modules.Coding.Delivery;

public sealed record DeliveryStage(string Name, bool Success, string Evidence, long DurationMilliseconds);
public sealed record AutonomousDeliveryResult(bool Success, string Summary, string TargetPath, IReadOnlyList<DeliveryStage> Stages, string? ReleaseDirectory);

/// <summary>
/// Final evidence gate for an agent-authored change. The model owns decomposition and edits;
/// this module prevents an unfinished change from being reported complete.
/// </summary>
internal sealed class AliAutonomousDelivery(
    AliArchitectureEngineering architecture,
    AliQualityEngineering quality,
    AliDotNetEngineeringLoop engineering,
    AliRoslynCodingTools roslyn,
    AliApplicationVerification verification,
    AliReleaseEngineering release)
{
    public async Task<AutonomousDeliveryResult> VerifyDeliveryAsync(
        string projectPath,
        string? testTargetPath,
        string? configuration,
        bool verifyApplication,
        bool publishRelease,
        CancellationToken cancellationToken)
    {
        var stages = new List<DeliveryStage>();
        var architectureStarted = DateTime.UtcNow;
        var graph = await architecture.InspectAsync(projectPath, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliveryStage("architecture", graph.Success, graph.Summary, Elapsed(architectureStarted)));

        var qualityStarted = DateTime.UtcNow;
        var qualityResult = await quality.ScanAsync(projectPath, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliveryStage("quality", qualityResult.Success, $"{qualityResult.Summary} SARIF: {qualityResult.SarifPath}", Elapsed(qualityStarted)));
        if (!qualityResult.Success) return Failed(projectPath, stages, "Delivery stopped at the quality evidence gate.");

        var engineeringStarted = DateTime.UtcNow;
        var verificationTarget = string.IsNullOrWhiteSpace(testTargetPath) ? projectPath : testTargetPath;
        var engineeringResult = await engineering.VerifyAsync(verificationTarget, configuration, roslyn.BuildAsync, cancellationToken).ConfigureAwait(false);
        stages.Add(new DeliveryStage("build-and-test", engineeringResult.Success,
            $"{engineeringResult.Summary} Test artifact: {engineeringResult.Tests?.ResultsPath ?? "none"}", Elapsed(engineeringStarted)));
        if (!engineeringResult.Success) return Failed(projectPath, stages, "Delivery stopped at the build-and-test evidence gate.");

        if (verifyApplication)
        {
            var applicationStarted = DateTime.UtcNow;
            var application = await verification.SmokeTestAsync(projectPath, configuration, null, cancellationToken).ConfigureAwait(false);
            stages.Add(new DeliveryStage("application-smoke", application.Success,
                $"{application.Summary} Screenshot: {application.ScreenshotPath ?? "not applicable"}", Elapsed(applicationStarted)));
            if (!application.Success) return Failed(projectPath, stages, "Delivery stopped at the actual-application evidence gate.");
        }

        string? releaseDirectory = null;
        if (publishRelease)
        {
            var releaseStarted = DateTime.UtcNow;
            var published = await release.PublishAsync(projectPath, "win-x64", true, cancellationToken).ConfigureAwait(false);
            stages.Add(new DeliveryStage("release", published.Success,
                $"{published.Summary} Manifest: {published.ManifestPath}", Elapsed(releaseStarted)));
            if (!published.Success) return Failed(projectPath, stages, "Delivery stopped at the release evidence gate.");
            releaseDirectory = published.PublishDirectory;
        }

        return new AutonomousDeliveryResult(true, "Every requested delivery evidence gate passed.", projectPath, stages, releaseDirectory);
    }

    private static AutonomousDeliveryResult Failed(string target, IReadOnlyList<DeliveryStage> stages, string summary) => new(false, summary, target, stages, null);
    private static long Elapsed(DateTime started) => (long)(DateTime.UtcNow - started).TotalMilliseconds;
}
