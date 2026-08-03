using Ali.Modules.Interaction;
using Ali.Modules.UserMemory;

namespace Ali.Framework.Tests;

public sealed class ParticipantPresenceFeedTests
{
    [Fact]
    public void RosterAuthority_KeepsSelectionAuthoritativeAndInvalidatesChangedPresence()
    {
        var alice = new ActiveUser("alice", "Alice", false, "test");
        var bob = new ActiveUser("bob", "Bob", false, "test");
        var activeUsers = new FixedActiveUserSession(alice, [alice, bob], "selection:1");
        var presence = new MutablePresenceSource(new ParticipantPresenceSnapshot(
            "presence:1",
            [
                new ParticipantPresenceObservation(
                    "camera:bob",
                    "bob",
                    "Bob",
                    ParticipantPresenceState.Present,
                    "target-selection",
                    .93)
            ]));
        var authority = new SelectedParticipantRosterAuthority(activeUsers, "tenant", presence);

        var roster = authority.CaptureAtAdmission(
            "turn:1",
            "conversation:1",
            activeUsers.CaptureSelectionSnapshot(),
            activeUsers.CaptureSelectionRevision(),
            DateTimeOffset.UtcNow);

        Assert.Equal("alice", roster.SelectedParticipantReference);
        Assert.Equal(ParticipantReferenceKind.Registered, roster.Find("bob")?.Kind);
        Assert.Equal(ParticipantPresenceState.Present, roster.Find("bob")?.Presence);
        Assert.Equal("target-selection", roster.Find("bob")?.ObservationSource);
        Assert.True(authority.CheckCurrent(roster).IsCurrent);

        presence.Current = new ParticipantPresenceSnapshot("presence:2", []);

        var stale = authority.CheckCurrent(roster);
        Assert.False(stale.IsCurrent);
        Assert.Equal("presence:2", stale.CurrentPresenceGeneration);
        var recaptured = authority.CaptureAtAdmission(
            "turn:2",
            "conversation:1",
            activeUsers.CaptureSelectionSnapshot(),
            activeUsers.CaptureSelectionRevision(),
            DateTimeOffset.UtcNow);
        Assert.Equal("alice", recaptured.SelectedParticipantReference);
        Assert.Null(recaptured.Find("bob"));
        Assert.True(authority.CheckCurrent(recaptured).IsCurrent);
    }

    [Fact]
    public void Projection_ExpiresTargetAndSpeakerIndependentlyWithoutRetainingStaleObservations()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var target = new TargetPresencePublication(
            7,
            now,
            true,
            "bob",
            "Bob",
            .8,
            .94,
            12);
        var speaker = new SpeakerPresencePublication(
            9,
            now,
            false,
            "",
            .61,
            false);

        var fresh = AliParticipantPresenceProjection.Capture(3, target, speaker, now);
        var afterTargetExpiry = AliParticipantPresenceProjection.Capture(
            3,
            target,
            speaker,
            now + AliParticipantPresenceProjection.TargetFreshness + TimeSpan.FromMilliseconds(1));
        var fullyStale = AliParticipantPresenceProjection.Capture(
            3,
            target,
            speaker,
            now + AliParticipantPresenceProjection.SpeakerFreshness + TimeSpan.FromMilliseconds(1));
        var fullyStaleAgain = AliParticipantPresenceProjection.Capture(
            3,
            target,
            speaker,
            now + AliParticipantPresenceProjection.SpeakerFreshness + TimeSpan.FromSeconds(1));

        Assert.Equal(2, fresh.Observations.Count);
        Assert.Contains(fresh.Observations, item =>
            item.RegisteredProfileId == "bob"
            && item.Source == "target-selection"
            && item.Confidence == .94);
        Assert.Contains(fresh.Observations, item =>
            item.RegisteredProfileId is null
            && item.Source == "speaker-recognition");
        Assert.Single(afterTargetExpiry.Observations);
        Assert.Equal("speaker-recognition", afterTargetExpiry.Observations[0].Source);
        Assert.Empty(fullyStale.Observations);
        Assert.NotEqual(fresh.Generation, afterTargetExpiry.Generation);
        Assert.NotEqual(afterTargetExpiry.Generation, fullyStale.Generation);
        Assert.Equal(fullyStale.Generation, fullyStaleAgain.Generation);
    }

    [Fact]
    public void Projection_CameraEpochChangesGenerationAndEnrollmentAudioIsNotPresence()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var enrollment = new SpeakerPresencePublication(
            20,
            now,
            true,
            "alice",
            .99,
            true);

        var firstCamera = AliParticipantPresenceProjection.Capture(1, null, enrollment, now);
        var replacementCamera = AliParticipantPresenceProjection.Capture(2, null, enrollment, now);

        Assert.Empty(firstCamera.Observations);
        Assert.Empty(replacementCamera.Observations);
        Assert.NotEqual(firstCamera.Generation, replacementCamera.Generation);
    }

    [Fact]
    public void Projection_SequenceOnlyChangesDoNotInvalidateMateriallyIdenticalPresence()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var firstTarget = new TargetPresencePublication(
            10,
            now,
            true,
            "bob",
            "Bob",
            .80,
            .90,
            20);
        var nextTargetFrame = firstTarget with
        {
            SequenceId = 11,
            ProducedUtc = now + TimeSpan.FromMilliseconds(20),
            LockQuality = .81,
            IdentityConfidence = .91
        };
        var firstSpeaker = new SpeakerPresencePublication(
            30,
            now,
            true,
            "bob",
            .90,
            false);
        var nextSpeakerFrame = firstSpeaker with
        {
            SequenceId = 31,
            ProducedUtc = now + TimeSpan.FromMilliseconds(20),
            Similarity = .91
        };

        var first = AliParticipantPresenceProjection.Capture(
            4,
            firstTarget,
            firstSpeaker,
            now);
        var next = AliParticipantPresenceProjection.Capture(
            4,
            nextTargetFrame,
            nextSpeakerFrame,
            now + TimeSpan.FromMilliseconds(20));
        var changedIdentity = AliParticipantPresenceProjection.Capture(
            4,
            nextTargetFrame with { RegisteredProfileId = "carol" },
            nextSpeakerFrame,
            now + TimeSpan.FromMilliseconds(20));

        Assert.Equal(first.Generation, next.Generation);
        Assert.NotEqual(next.Generation, changedIdentity.Generation);
    }

    [Fact]
    public void Projection_NewTargetTrackInvalidatesSameUnmatchedProfileIdAndSanitizesNaN()
    {
        var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
        var first = new TargetPresencePublication(
            1,
            now,
            true,
            "unregistered-profile",
            "Guest",
            double.NaN,
            double.NaN,
            40);
        var nextTrack = first with { SequenceId = 2, TrackGeneration = 41 };

        var firstSnapshot = AliParticipantPresenceProjection.Capture(1, first, null, now);
        var nextSnapshot = AliParticipantPresenceProjection.Capture(1, nextTrack, null, now);

        Assert.NotEqual(firstSnapshot.Generation, nextSnapshot.Generation);
        Assert.Equal(0d, Assert.Single(firstSnapshot.Observations).Confidence);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ParticipantPresenceSnapshot(
            "nan",
            [new("source", null, "Guest", ParticipantPresenceState.Present, "test", double.NaN)])
            .Normalize());
    }

    [Fact]
    public void RosterSessionScope_EvictsOldestOpaqueReferenceAtItsBound()
    {
        var scope = new ParticipantRosterSessionScope("conversation:bounded");
        var first = scope.Resolve(ParticipantReferenceKind.Unknown, "camera:first");
        for (var index = 1; index <= ParticipantRosterSessionScope.MaximumReferences; index++)
        {
            _ = scope.Resolve(ParticipantReferenceKind.Unknown, $"camera:{index}");
        }

        var newest = scope.Resolve(
            ParticipantReferenceKind.Unknown,
            $"camera:{ParticipantRosterSessionScope.MaximumReferences}");
        var firstAfterEviction = scope.Resolve(
            ParticipantReferenceKind.Unknown,
            "camera:first");

        Assert.StartsWith("unknown:", first, StringComparison.Ordinal);
        Assert.DoesNotContain("camera:first", first, StringComparison.Ordinal);
        Assert.Equal(
            newest,
            scope.Resolve(
                ParticipantReferenceKind.Unknown,
                $"camera:{ParticipantRosterSessionScope.MaximumReferences}"));
        Assert.NotEqual(first, firstAfterEviction);
    }

    [Fact]
    public void PresenceBridge_StaleAttachmentCannotDetachReplacement()
    {
        var bridge = new ParticipantPresenceSnapshotBridge();
        var first = bridge.Attach(new MutablePresenceSource(new("presence:first", [])));
        var second = bridge.Attach(new MutablePresenceSource(new("presence:second", [])));

        first.Dispose();
        Assert.Equal("presence:second", bridge.Capture().Generation);

        second.Dispose();
        Assert.Equal(SelectedParticipantRosterAuthority.NoPresenceGeneration, bridge.Capture().Generation);
    }

    private sealed class MutablePresenceSource(ParticipantPresenceSnapshot current) :
        IParticipantPresenceSnapshotSource
    {
        public ParticipantPresenceSnapshot Current { get; set; } = current;

        public ParticipantPresenceSnapshot Capture() => Current;
    }

    private sealed class FixedActiveUserSession(
        ActiveUser current,
        IReadOnlyList<ActiveUser> available,
        string revision) : IActiveUserSession
    {
        public ActiveUser Current { get; } = current;

        public IReadOnlyList<ActiveUser> AvailableUsers { get; } = available;

        public bool RequiresSelection => false;

        public ActiveUserSelectionSnapshot CaptureSelectionSnapshot() =>
            ActiveUserSelectionSnapshot.Resolved(Current);

        public string CaptureSelectionRevision() => revision;

        public event EventHandler<ActiveUser>? Changed
        {
            add { }
            remove { }
        }

        public ActiveUser Select(string stableId) => Current;

        public void Refresh()
        {
        }
    }
}
