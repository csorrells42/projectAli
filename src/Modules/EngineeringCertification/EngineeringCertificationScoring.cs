using System.Globalization;
using System.Text;

namespace Ali.Modules.EngineeringCertification;

internal static class EngineeringCertificationScoring
{
    internal const string Disclaimer =
        "Certification scores are mechanical results for this versioned engineering fixture suite. "
        + "They are not user-request routing rules and do not guarantee general intelligence, safety, or fitness for unrelated work.";

    internal static EngineeringCertificationScore Score(
        EngineeringCertificationTask task,
        EngineeringAgentExecutionReceipt agent,
        EngineeringVerificationBaseline baseline,
        EngineeringVerificationReceipt verification,
        TimeSpan elapsed)
    {
        task.Validate();
        var observedTools = new HashSet<string>(agent.ToolIds, StringComparer.Ordinal);
        var missingTools = task.RequiredToolIds.Where(tool => !observedTools.Contains(tool)).ToArray();
        var introducedDiagnostics = verification.IntroducedRoslynDiagnostics(baseline);
        var totalTokens = agent.TotalTokens;
        var components = new[]
        {
            Component("build-success", true, verification.BuildSucceeded,
                verification.BuildSucceeded ? "Release build succeeded." : "Release build did not succeed."),
            Component("correct-engineering-primitive", true,
                string.Equals(agent.SelectedPrimitiveId, task.ExpectedPrimitiveId, StringComparison.Ordinal),
                $"Expected '{task.ExpectedPrimitiveId}'; observed '{agent.SelectedPrimitiveId ?? "none"}'."),
            Component("no-hallucinated-apis", true, verification.HallucinatedApiDiagnostics.Count == 0,
                $"Independent compiler evidence identified {verification.HallucinatedApiDiagnostics.Count} hallucinated API diagnostic(s)."),
            Component("no-roslyn-diagnostics-introduced", true, introducedDiagnostics == 0,
                $"Introduced Roslyn error/warning diagnostics: {introducedDiagnostics}."),
            Component("unit-tests-success", true, verification.UnitTestsSucceeded,
                verification.UnitTestsSucceeded ? "Unit tests succeeded." : "Unit tests did not succeed."),
            Component("correct-tool-selection", true, missingTools.Length == 0,
                missingTools.Length == 0
                    ? "Every required typed tool id was observed."
                    : $"Missing required typed tool ids: {string.Join(", ", missingTools)}."),
            Component("failure-recovery", task.InjectFirstRequiredToolFailure,
                agent.RecoveredAfterInjectedFailure == true,
                task.InjectFirstRequiredToolFailure
                    ? $"Typed recovery receipt: {agent.RecoveredAfterInjectedFailure?.ToString() ?? "missing"}."
                    : "No failure was injected for this task."),
            Component("completion-time", true, elapsed <= task.CompletionBudget,
                $"Elapsed {elapsed.TotalMilliseconds:0} ms; budget {task.CompletionBudget.TotalMilliseconds:0} ms."),
            Component("tokens-consumed", true, totalTokens is not null && totalTokens <= task.TokenBudget,
                totalTokens is null
                    ? $"Typed token usage was unavailable; budget {task.TokenBudget}."
                    : $"Consumed {totalTokens} token(s); budget {task.TokenBudget}.")
        };
        var applicable = components.Count(component => component.Applicable);
        var passed = components.Count(component => component.Applicable && component.Passed);
        var percent = applicable == 0
            ? 0
            : decimal.Round(100m * passed / applicable, 2, MidpointRounding.AwayFromZero);
        return new EngineeringCertificationScore(percent, components);
    }

    internal static EngineeringCertificationComparisonReport BuildComparison(
        EngineeringCertificationSuite suite,
        string suiteDigest,
        string runId,
        IReadOnlyList<EngineeringCertificationCandidate> candidates,
        IReadOnlyList<EngineeringCandidateDiscoveryIssue> discoveryIssues,
        IReadOnlyList<EngineeringCertificationTaskEvidence> evidence,
        DateTimeOffset generatedAtUtc)
    {
        var reports = candidates.Select(candidate =>
        {
            var tasks = evidence
                .Where(item => string.Equals(
                    item.CandidateBindingDigest,
                    candidate.BindingDigest,
                    StringComparison.Ordinal))
                .ToArray();
            var summaries = tasks
                .SelectMany(item => item.Score.Components)
                .GroupBy(component => component.Id, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => new EngineeringCertificationComponentSummary(
                        group.Count(component => component.Applicable),
                        group.Count(component => component.Applicable && component.Passed)),
                    StringComparer.Ordinal);
            long? totalTokens = tasks.All(item => item.Agent.TotalTokens is not null)
                ? tasks.Sum(item => item.Agent.TotalTokens!.Value)
                : null;
            return new EngineeringCertificationCandidateReport(
                candidate.CandidateId,
                candidate.RuntimeId,
                candidate.ModelId,
                candidate.BindingDigest,
                tasks.Length,
                tasks.Length == 0
                    ? 0
                    : decimal.Round(tasks.Average(item => item.Score.Percent), 2, MidpointRounding.AwayFromZero),
                tasks.Sum(item => item.ElapsedMilliseconds),
                totalTokens,
                summaries);
        })
        .OrderByDescending(report => report.MeanScore)
        .ThenBy(report => report.ModelId, StringComparer.Ordinal)
        .ToArray();

        return new EngineeringCertificationComparisonReport(
            suite.Version,
            suiteDigest,
            runId,
            generatedAtUtc,
            reports,
            discoveryIssues,
            Disclaimer);
    }

    internal static string RenderComparisonMarkdown(EngineeringCertificationComparisonReport report)
    {
        var output = new StringBuilder();
        output.AppendLine($"# Engineering certification comparison — {report.SuiteVersion}");
        output.AppendLine();
        output.AppendLine($"- Suite digest: `{report.SuiteDigest}`");
        output.AppendLine($"- Run id: `{report.RunId}`");
        output.AppendLine($"- Generated UTC: `{report.GeneratedAtUtc:O}`");
        output.AppendLine();
        output.AppendLine("| Candidate model | Runtime | Tasks | Mean score | Time (s) | Tokens |");
        output.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var candidate in report.Candidates)
        {
            output.Append("| ").Append(Escape(candidate.ModelId))
                .Append(" | ").Append(Escape(candidate.RuntimeId))
                .Append(" | ").Append(candidate.CompletedTasks.ToString(CultureInfo.InvariantCulture))
                .Append(" | ").Append(candidate.MeanScore.ToString("0.00", CultureInfo.InvariantCulture)).Append("%")
                .Append(" | ").Append((candidate.TotalElapsedMilliseconds / 1000m).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" | ").Append(candidate.TotalTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable")
                .AppendLine(" |");
        }
        if (report.DiscoveryIssues.Count > 0)
        {
            output.AppendLine().AppendLine("## Discovery issues").AppendLine();
            foreach (var issue in report.DiscoveryIssues)
            {
                output.Append("- ").Append(Escape(issue.RuntimeId)).Append(": ")
                    .AppendLine(Escape(issue.Message));
            }
        }
        output.AppendLine().AppendLine("## Scope").AppendLine();
        output.AppendLine(report.Disclaimer);
        return output.ToString();
    }

    internal static string RenderCandidateMarkdown(
        EngineeringCertificationComparisonReport comparison,
        EngineeringCertificationCandidateReport candidate)
    {
        var output = new StringBuilder();
        output.AppendLine($"# Engineering certification — {candidate.ModelId}");
        output.AppendLine();
        output.AppendLine($"- Suite: `{comparison.SuiteVersion}`");
        output.AppendLine($"- Suite digest: `{comparison.SuiteDigest}`");
        output.AppendLine($"- Candidate binding: `{candidate.BindingDigest}`");
        output.AppendLine($"- Completed tasks: `{candidate.CompletedTasks}`");
        output.AppendLine($"- Mean mechanical score: `{candidate.MeanScore:0.00}%`");
        output.AppendLine();
        output.AppendLine("| Component | Passed | Applicable |");
        output.AppendLine("|---|---:|---:|");
        foreach (var component in candidate.Components.OrderBy(component => component.Key, StringComparer.Ordinal))
        {
            output.Append("| ").Append(Escape(component.Key))
                .Append(" | ").Append(component.Value.PassedTasks)
                .Append(" | ").Append(component.Value.ApplicableTasks)
                .AppendLine(" |");
        }
        output.AppendLine().AppendLine(comparison.Disclaimer);
        return output.ToString();
    }

    private static EngineeringCertificationScoreComponent Component(
        string id,
        bool applicable,
        bool passed,
        string evidence) =>
        new(id, applicable, passed, evidence);

    private static string Escape(string value) => value.Replace("|", "\\|", StringComparison.Ordinal);
}
