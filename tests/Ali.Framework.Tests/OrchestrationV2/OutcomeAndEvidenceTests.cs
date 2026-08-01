using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Orchestration.Contracts;
using Ali.Modules.Orchestration.Evidence;

namespace Ali.Framework.Tests.OrchestrationV2;

public sealed class OutcomeAndEvidenceTests
{
    [Fact]
    public void ReturnedSuccessFalse_IsACompletedInvocationWithAFailedDomainOutcome()
    {
        var outcome = ToolInvocationOutcome.Returned("redacted"u8, reportedSuccess: false);

        Assert.Equal(InvocationStatus.Returned, outcome.InvocationStatus);
        Assert.Equal(DomainOutcome.Failed, outcome.DomainOutcome);
    }

    [Fact]
    public void DeniedAndException_AreNotCollapsedIntoDomainFailure()
    {
        var denied = ToolInvocationOutcome.Denied("approval-denied");
        var faulted = ToolInvocationOutcome.Threw(new InvalidOperationException("secret detail"));

        Assert.Equal(InvocationStatus.Denied, denied.InvocationStatus);
        Assert.Equal(DomainOutcome.Unreported, denied.DomainOutcome);
        Assert.Equal(InvocationStatus.Threw, faulted.InvocationStatus);
        Assert.Equal(DomainOutcome.Unreported, faulted.DomainOutcome);
        Assert.DoesNotContain("secret detail", JsonSerializer.Serialize(faulted), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ledger_RoundTripsFullEvidence_WhilePlaintextContainsOnlyProtectedDigests()
    {
        using var directory = new TemporaryDirectory();
        const string secret = "evidence-secret-canary-7e2b2e0d";
        const string secretArgumentName = "secret-argument-name-canary";
        var identity = new TurnIdentity(
            "user-secret-canary",
            "conversation-secret-canary",
            "message-secret-canary");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var draft = CreateDraft(
            "call-1",
            "workspace_write",
            Json($"{{\"{secretArgumentName}\":\"present\",\"path\":\"C:/private/{secret}.txt\",\"value\":\"{secret}\"}}"),
            JsonSerializer.SerializeToElement(new { success = false, error = secret }),
            ToolInvocationOutcome.Returned(Encoding.UTF8.GetBytes(secret), reportedSuccess: false));

        var stored = await ledger.AppendAsync(identity, draft, TestContext.Current.CancellationToken);
        var replay = Assert.Single(await ledger.ReplayAsync(identity, TestContext.Current.CancellationToken));
        var protectedContent = await ledger.ReadProtectedAsync(
            identity,
            stored.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, replay.Cursor);
        Assert.Equal(DomainOutcome.Failed, replay.Evidence.DomainOutcome);
        Assert.Equal(InvocationStatus.Returned, replay.Evidence.InvocationStatus);
        Assert.Contains(secret, protectedContent.Arguments.GetRawText(), StringComparison.Ordinal);
        Assert.True(protectedContent.Arguments.TryGetProperty(secretArgumentName, out _));
        Assert.Contains(secret, protectedContent.Result.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(replay.Evidence), StringComparison.Ordinal);
        Assert.Equal(64, replay.Evidence.ArgumentsDigest.Length);
        Assert.Equal(64, replay.Evidence.RecordMac.Length);

        var canaryBytes = Encoding.UTF8.GetBytes(secret);
        var argumentNameCanaryBytes = Encoding.UTF8.GetBytes(secretArgumentName);
        var identityCanary = Encoding.UTF8.GetBytes("user-secret-canary");
        foreach (var path in Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(bytes.AsSpan().IndexOf(canaryBytes) < 0, $"Secret leaked to {path}.");
            Assert.True(bytes.AsSpan().IndexOf(argumentNameCanaryBytes) < 0, $"Argument name leaked to {path}.");
            Assert.True(bytes.AsSpan().IndexOf(identityCanary) < 0, $"Turn identity leaked to {path}.");
        }
    }

    [Fact]
    public async Task CanonicalArguments_AreOrderIndependentValueSpecificAndKeyed()
    {
        using var directory = new TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        using var firstDocument = JsonDocument.Parse("{\"b\":2,\"a\":\"low-entropy\"}");
        using var secondDocument = JsonDocument.Parse("{\"a\":\"low-entropy\",\"b\":2}");
        using var changedDocument = JsonDocument.Parse("{\"a\":\"different\",\"b\":2}");

        var first = await ledger.AppendAsync(
            identity,
            CreateDraft("call-1", "tool", firstDocument.RootElement, Json("{}")),
            TestContext.Current.CancellationToken);
        var second = await ledger.AppendAsync(
            identity,
            CreateDraft("call-2", "tool", secondDocument.RootElement, Json("{}")),
            TestContext.Current.CancellationToken);
        var changed = await ledger.AppendAsync(
            identity,
            CreateDraft("call-3", "tool", changedDocument.RootElement, Json("{}")),
            TestContext.Current.CancellationToken);

        Assert.Equal(first.Evidence.ArgumentsDigest, second.Evidence.ArgumentsDigest);
        Assert.NotEqual(first.Evidence.ArgumentsDigest, changed.Evidence.ArgumentsDigest);
        var unkeyed = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("{\"a\":\"low-entropy\",\"b\":2}"))).ToLowerInvariant();
        Assert.NotEqual(unkeyed, first.Evidence.ArgumentsDigest);
    }

    [Fact]
    public async Task NoEffectFingerprint_IgnoresToolAliasAndTime_ButTracksEffectState()
    {
        using var directory = new TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var first = CreateDraft("call-1", "tool_alias_a", Json("{}"), Json("{}")) with
        {
            StableOutcomeCode = "locked",
            Artifacts = [new EvidenceArtifactDraft("artifact-1", "file", "before", "after")]
        };
        var second = first with
        {
            CallId = "call-2",
            ToolName = "tool_alias_b",
            Arguments = Json("{\"different-adapter-shape\":true}"),
            StartedAtUtc = first.StartedAtUtc.AddMinutes(2),
            CompletedAtUtc = first.CompletedAtUtc.AddMinutes(2)
        };
        var changed = second with
        {
            CallId = "call-3",
            NormalizedEffectResult = Json("{\"state\":\"materially-different\"}")
        };

        var firstRecord = await ledger.AppendAsync(identity, first, TestContext.Current.CancellationToken);
        var secondRecord = await ledger.AppendAsync(identity, second, TestContext.Current.CancellationToken);
        var changedRecord = await ledger.AppendAsync(identity, changed, TestContext.Current.CancellationToken);

        Assert.Equal(firstRecord.Evidence.NoEffectFingerprint, secondRecord.Evidence.NoEffectFingerprint);
        Assert.NotEqual(secondRecord.Evidence.NoEffectFingerprint, changedRecord.Evidence.NoEffectFingerprint);
    }

    [Fact]
    public async Task ValidatedEvidence_ExposesDefensiveReadOnlyArtifactCollections()
    {
        using var directory = new TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var sourceArtifacts = new[]
        {
            new EvidenceArtifactDraft("artifact-1", "file", "before", "after")
        };
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var append = ledger.AppendAsync(
            identity,
            CreateDraft("call", "tool", Json("{}"), Json("{}")) with
            {
                Artifacts = sourceArtifacts
            },
            TestContext.Current.CancellationToken);
        sourceArtifacts[0] = new EvidenceArtifactDraft("mutated", "file", null, null);

        var record = (await append).Evidence;
        Assert.Equal(64, Assert.Single(record.Artifacts).ArtifactIdDigest.Length);
        var protectedContent = await ledger.ReadProtectedAsync(
            identity,
            record.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal("artifact-1", Assert.Single(protectedContent.Identity.Artifacts).ArtifactId);
        Assert.Empty(typeof(EvidenceRecord).GetConstructors());
        var list = Assert.IsAssignableFrom<System.Collections.IList>(record.Artifacts);
        Assert.Throws<NotSupportedException>(() => list[0] = record.Artifacts[0]);
    }

    [Fact]
    public async Task CallerDerivedIdentifiers_AreProtectedAndProjectedAsDigests()
    {
        using var directory = new TemporaryDirectory();
        const string secret = "sk-proj-identifier-canary-4c9355a1";
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var draft = CreateDraft(secret, secret + "-tool", Json("{}"), Json("{}")) with
        {
            CapabilityGroup = secret + "-capability",
            ProviderId = secret + "-provider",
            RegistryRevision = secret + "-registry",
            Outcome = ToolInvocationOutcome.Denied(secret + " failure prose with spaces"),
            StableOutcomeCode = secret + "-outcome",
            Artifacts = [new EvidenceArtifactDraft(secret + "-artifact", "file", secret + "-before", secret + "-after")],
            Source = new EvidenceSourceMetadata(
                "tool",
                secret + "-source-provider",
                "trusted-local",
                DateTimeOffset.UtcNow,
                secret + "-state")
        };

        var stored = await ledger.AppendAsync(identity, draft, TestContext.Current.CancellationToken);
        var projection = stored.Evidence;
        Assert.All(
            new[]
            {
                projection.CallIdDigest,
                projection.ToolNameDigest,
                projection.CapabilityGroupDigest,
                projection.ProviderIdDigest,
                projection.RegistryRevisionDigest,
                projection.FailureCodeDigest!,
                projection.StableOutcomeCodeDigest,
                Assert.Single(projection.Artifacts).ArtifactIdDigest,
                projection.Source.ProviderIdDigest,
                projection.Source.StateRevisionDigest
            },
            value => Assert.Matches("^[0-9a-f]{64}$", value));

        var protectedContent = await ledger.ReadProtectedAsync(
            identity,
            projection.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(secret, protectedContent.Identity.CallId);
        Assert.Equal(secret + " failure prose with spaces", protectedContent.Identity.FailureCode);
        Assert.Equal(secret + "-artifact", Assert.Single(protectedContent.Identity.Artifacts).ArtifactId);

        var secretBytes = Encoding.UTF8.GetBytes(secret);
        foreach (var path in Directory.GetFiles(directory.Path, "*", SearchOption.AllDirectories))
        {
            var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.True(bytes.AsSpan().IndexOf(secretBytes) < 0, $"Caller-derived identifier leaked to {path}.");
        }
    }

    [Fact]
    public async Task Append_ClonesJsonBeforeAwaitingPersistence()
    {
        using var directory = new TemporaryDirectory();
        var identity = new TurnIdentity("user", "conversation", "message");
        var ledger = new EvidenceLedger(directory.Path, "profile-a");
        var document = JsonDocument.Parse("{\"secret\":\"survives-source-disposal\"}");
        var append = ledger.AppendAsync(
            identity,
            CreateDraft("call", "tool", document.RootElement, document.RootElement),
            TestContext.Current.CancellationToken);
        document.Dispose();

        var record = await append;
        var protectedContent = await ledger.ReadProtectedAsync(
            identity,
            record.Evidence.EvidenceId,
            TestContext.Current.CancellationToken);
        Assert.Equal(
            "survives-source-disposal",
            protectedContent.Arguments.GetProperty("secret").GetString());
    }

    internal static EvidenceDraft CreateDraft(
        string callId,
        string toolName,
        JsonElement arguments,
        JsonElement result,
        ToolInvocationOutcome? outcome = null)
    {
        var started = DateTimeOffset.UtcNow;
        return new EvidenceDraft
        {
            CallId = callId,
            ToolName = toolName,
            CapabilityGroup = "test-capability",
            ProviderId = "test-provider",
            RegistryRevision = "registry-revision",
            EffectKind = "update",
            Arguments = arguments,
            Result = result,
            NormalizedTarget = Json("{\"target\":\"test-target\"}"),
            NormalizedEffectResult = Json("{\"state\":\"unchanged\"}"),
            Outcome = outcome ?? ToolInvocationOutcome.Returned("result"u8),
            StableOutcomeCode = "none",
            StartedAtUtc = started,
            CompletedAtUtc = started.AddMilliseconds(1),
            Artifacts = [],
            Permission = new EvidencePermissionMetadata("approved-once", "once"),
            ProtectedPermissionReceipt = JsonSerializer.SerializeToElement(new { receipt = "private" }),
            Source = new EvidenceSourceMetadata("tool", "test-provider", "trusted-local", started),
            ProtectedProvenance = JsonSerializer.SerializeToElement(new { path = "C:/private" })
        };
    }

    internal static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    internal sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "Ali-EvidenceV2-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
