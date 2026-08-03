using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Work;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class EffectNormalizationRegistryTests
{
    [Fact]
    public void ExplicitAlternateTools_ShareIdentityOnlyForDeclaredFamilyAndNormalizedTarget()
    {
        var registry = new EffectNormalizationRegistry(
        [
            new FileEffectAdapter(
                "file-content-v1",
                "write-file",
                "replace-file")
        ]);

        var write = registry.Prepare(
            "write-file",
            Json("""{"path":"src/a.cs","content":"first"}"""));
        var replace = registry.Prepare(
            "replace-file",
            Json("""{"replacement":"second","path":"src/a.cs"}"""));
        var otherTarget = registry.Prepare(
            "replace-file",
            Json("""{"replacement":"second","path":"src/b.cs"}"""));

        Assert.True(write.AdapterDeclaredSemanticEquivalence);
        Assert.True(replace.AdapterDeclaredSemanticEquivalence);
        Assert.Equal("file-content-v1", write.EffectFamily);
        Assert.Equal(write.EffectIdentity.Fingerprint, replace.EffectIdentity.Fingerprint);
        Assert.NotEqual(replace.EffectIdentity.Fingerprint, otherTarget.EffectIdentity.Fingerprint);
    }

    [Fact]
    public void SameNormalizedTarget_DoesNotCrossAdapterDeclaredEffectFamilies()
    {
        var registry = new EffectNormalizationRegistry(
        [
            new FileEffectAdapter("file-content-v1", "write-file"),
            new FileEffectAdapter("file-delete-v1", "delete-file")
        ]);

        var write = registry.Prepare(
            "write-file",
            Json("""{"path":"src/a.cs"}"""));
        var delete = registry.Prepare(
            "delete-file",
            Json("""{"path":"src/a.cs"}"""));

        Assert.NotEqual(write.EffectIdentity.Fingerprint, delete.EffectIdentity.Fingerprint);
    }

    [Fact]
    public void AdapterNormalizedNoEffect_IgnoresRawTimeAndDisplayNoise()
    {
        var registry = new EffectNormalizationRegistry(
        [
            new FileEffectAdapter("file-content-v1", "write-file", "replace-file")
        ]);
        var firstPrepared = registry.Prepare(
            "write-file",
            Json("""{"path":"src/a.cs","content":"first"}"""));
        var secondPrepared = registry.Prepare(
            "replace-file",
            Json("""{"path":"src/a.cs","replacement":"second"}"""));
        var firstResult = Json(
            """{"changed":false,"state":"read-only","timestamp":"2026-08-02T01:00:00Z","display":"first prose"}""");
        var secondResult = Json(
            """{"display":"different prose","timestamp":"2026-08-02T02:00:00Z","state":"read-only","changed":false}""");
        var firstInvocation = ToolInvocationOutcome.Returned(
            JsonSerializer.SerializeToUtf8Bytes(firstResult),
            reportedSuccess: false);
        var secondInvocation = ToolInvocationOutcome.Returned(
            JsonSerializer.SerializeToUtf8Bytes(secondResult),
            reportedSuccess: false);

        var first = registry.NormalizeOutcome(
            firstPrepared,
            firstResult,
            firstInvocation,
            EffectResultKind.NoEffect);
        var second = registry.NormalizeOutcome(
            secondPrepared,
            secondResult,
            secondInvocation,
            EffectResultKind.NoEffect);

        Assert.NotEqual(firstInvocation.ResultDigest, secondInvocation.ResultDigest);
        Assert.True(first.AdapterDeclaredSemanticEquivalence);
        Assert.Equal(first.Identity.OutcomeFingerprint, second.Identity.OutcomeFingerprint);
        Assert.Equal(first.Identity.NoEffectFingerprint, second.Identity.NoEffectFingerprint);
        Assert.DoesNotContain("timestamp", first.NormalizedDomainOutcome.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("display", first.NormalizedDomainOutcome.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AdapterNormalizedNoEffect_ChangesWhenStableDomainStateChanges()
    {
        var registry = new EffectNormalizationRegistry(
        [
            new FileEffectAdapter("file-content-v1", "write-file")
        ]);
        var prepared = registry.Prepare(
            "write-file",
            Json("""{"path":"src/a.cs"}"""));
        var unchangedResult = Json("""{"changed":false,"state":"read-only"}""");
        var missingResult = Json("""{"changed":false,"state":"missing"}""");

        var unchanged = registry.NormalizeOutcome(
            prepared,
            unchangedResult,
            ToolInvocationOutcome.Returned("unchanged"u8, reportedSuccess: false),
            EffectResultKind.NoEffect);
        var missing = registry.NormalizeOutcome(
            prepared,
            missingResult,
            ToolInvocationOutcome.Returned("missing"u8, reportedSuccess: false),
            EffectResultKind.NoEffect);

        Assert.NotEqual(unchanged.Identity.NoEffectFingerprint, missing.Identity.NoEffectFingerprint);
    }

    [Fact]
    public void UnknownTools_FallBackToExactToolAndCanonicalArguments()
    {
        var registry = EffectNormalizationRegistry.Empty;

        var first = registry.Prepare(
            "unknown-a",
            Json("""{"count":2.0,"target":"src/a.cs"}"""));
        var reordered = registry.Prepare(
            "unknown-a",
            Json("""{"target":"src/a.cs","count":2}"""));
        var otherTool = registry.Prepare(
            "unknown-b",
            Json("""{"target":"src/a.cs","count":2}"""));
        var otherArguments = registry.Prepare(
            "unknown-a",
            Json("""{"target":"src/b.cs","count":2}"""));

        Assert.False(first.AdapterDeclaredSemanticEquivalence);
        Assert.Equal(first.EffectIdentity.Fingerprint, reordered.EffectIdentity.Fingerprint);
        Assert.NotEqual(first.EffectIdentity.Fingerprint, otherTool.EffectIdentity.Fingerprint);
        Assert.NotEqual(first.EffectIdentity.Fingerprint, otherArguments.EffectIdentity.Fingerprint);
    }

    [Fact]
    public void Registry_DoesNotInferAnAdapterFromNamesOrArgumentText()
    {
        var registry = new EffectNormalizationRegistry(
        [
            new FileEffectAdapter("file-content-v1", "write-file")
        ]);

        var nameLookalike = registry.Prepare(
            "prefix-write-file-suffix",
            Json("""{"path":"src/a.cs"}"""));
        var argumentMention = registry.Prepare(
            "unregistered",
            Json("""{"suggestedTool":"write-file","family":"file-content-v1"}"""));

        Assert.False(nameLookalike.AdapterDeclaredSemanticEquivalence);
        Assert.False(argumentMention.AdapterDeclaredSemanticEquivalence);
    }

    [Fact]
    public void UnknownTools_DoNotNormalizeVolatileResultsOrClaimSemanticEquivalence()
    {
        var registry = EffectNormalizationRegistry.Empty;
        var prepared = registry.Prepare("unknown", Json("""{"target":"a"}"""));
        var firstResult = Json("""{"timestamp":"2026-08-02T01:00:00Z","changed":false}""");
        var secondResult = Json("""{"timestamp":"2026-08-02T01:00:01Z","changed":false}""");
        var firstInvocation = ToolInvocationOutcome.Returned(
            JsonSerializer.SerializeToUtf8Bytes(firstResult),
            reportedSuccess: false);
        var secondInvocation = ToolInvocationOutcome.Returned(
            JsonSerializer.SerializeToUtf8Bytes(secondResult),
            reportedSuccess: false);

        var first = registry.NormalizeOutcome(
            prepared,
            firstResult,
            firstInvocation,
            EffectResultKind.NoEffect);
        var second = registry.NormalizeOutcome(
            prepared,
            secondResult,
            secondInvocation,
            EffectResultKind.NoEffect);

        Assert.False(first.AdapterDeclaredSemanticEquivalence);
        Assert.NotEqual(first.Identity.NoEffectFingerprint, second.Identity.NoEffectFingerprint);
    }

    [Fact]
    public void Registry_RejectsAmbiguousDuplicateExactToolBindings()
    {
        var error = Assert.Throws<ArgumentException>(() =>
            new EffectNormalizationRegistry(
            [
                new FileEffectAdapter("file-content-v1", "write-file"),
                new FileEffectAdapter("other-family-v1", "write-file")
            ]));

        Assert.Contains("more than one", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisteredAdapterFailure_IsFailClosedRatherThanUsingExactFallback()
    {
        var registry = new EffectNormalizationRegistry([new UndefinedTargetAdapter()]);

        Assert.Throws<ArgumentException>(() =>
            registry.Prepare("registered-tool", Json("{}")));
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class FileEffectAdapter(
        string effectFamily,
        params string[] toolNames) : IEffectNormalizationAdapter
    {
        public IReadOnlyCollection<string> ToolNames { get; } = Array.AsReadOnly(toolNames);

        public string EffectFamily { get; } = effectFamily;

        public AdapterNormalizedEffectTarget NormalizeTarget(
            EffectTargetNormalizationRequest request)
        {
            var arguments = request.Arguments.Deserialize<FileArguments>()
                ?? throw new InvalidDataException("Missing file arguments.");
            ArgumentException.ThrowIfNullOrWhiteSpace(arguments.Path);
            return new AdapterNormalizedEffectTarget(
                JsonSerializer.SerializeToElement(new FileTarget(arguments.Path)));
        }

        public AdapterNormalizedDomainOutcome NormalizeDomainOutcome(
            EffectOutcomeNormalizationRequest request)
        {
            var result = request.Result.Deserialize<FileResult>()
                ?? throw new InvalidDataException("Missing file result.");
            ArgumentException.ThrowIfNullOrWhiteSpace(result.State);
            return new AdapterNormalizedDomainOutcome(
                result.Changed ? "content-changed" : "content-unchanged",
                JsonSerializer.SerializeToElement(
                    new FileDomainState(result.Changed, result.State)));
        }
    }

    private sealed class UndefinedTargetAdapter : IEffectNormalizationAdapter
    {
        public IReadOnlyCollection<string> ToolNames { get; } = ["registered-tool"];

        public string EffectFamily => "registered-family-v1";

        public AdapterNormalizedEffectTarget NormalizeTarget(
            EffectTargetNormalizationRequest request) => new(default);

        public AdapterNormalizedDomainOutcome NormalizeDomainOutcome(
            EffectOutcomeNormalizationRequest request) =>
            new("unreachable", JsonSerializer.SerializeToElement(new { }));
    }

    private sealed record FileArguments(
        [property: JsonPropertyName("path")] string Path);

    private sealed record FileResult(
        [property: JsonPropertyName("changed")] bool Changed,
        [property: JsonPropertyName("state")] string State);

    private sealed record FileTarget(
        [property: JsonPropertyName("path")] string Path);

    private sealed record FileDomainState(
        [property: JsonPropertyName("changed")] bool Changed,
        [property: JsonPropertyName("state")] string State);
}
