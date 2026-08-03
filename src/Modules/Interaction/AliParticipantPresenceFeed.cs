using Ali.Modules.UserMemory;
using AvatarBuilder.Modules.Audio.SpeakerRecognition;
using AvatarBuilder.Modules.Contracts;
using AvatarBuilder.Modules.Pipeline;
using AvatarBuilder.Modules.Vision.TargetSelection;
using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Interaction;

/// <summary>
/// Independent, read-only subscriptions to the live target and speaker outputs.
/// Each subscription drains one producer-owned latest-value slot into one immutable
/// local fact. It cannot queue work, invoke a producer callback, or back-pressure a
/// camera/audio pipeline.
/// </summary>
internal sealed class AliParticipantPresenceFeed :
    IParticipantPresenceSnapshotSource,
    IDisposable
{
    private readonly object _targetGate = new();
    private readonly TimeProvider _timeProvider;
    private readonly LatestPublishedFactReader<SpeakerRecognitionOutput, SpeakerPresencePublication>
        _speaker;
    private TargetSubscriptionState _targetState = new(0, null);
    private int _disposed;

    internal AliParticipantPresenceFeed(
        IModuleOutputSource<SpeakerRecognitionOutput> speaker,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(speaker);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _speaker = new LatestPublishedFactReader<SpeakerRecognitionOutput, SpeakerPresencePublication>(
            speaker,
            CopySpeaker,
            "Ali participant speaker presence");
    }

    internal void AttachTargetSource(IModuleOutputSource<TargetSelectionOutput>? target)
    {
        LatestPublishedFactReader<TargetSelectionOutput, TargetPresencePublication>? replacement = null;
        if (target is not null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            replacement = new LatestPublishedFactReader<TargetSelectionOutput, TargetPresencePublication>(
                target,
                CopyTarget,
                "Ali participant target presence");
        }

        LatestPublishedFactReader<TargetSelectionOutput, TargetPresencePublication>? previous;
        lock (_targetGate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                replacement?.Dispose();
                if (target is not null)
                {
                    throw new ObjectDisposedException(nameof(AliParticipantPresenceFeed));
                }
                return;
            }

            var current = Volatile.Read(ref _targetState);
            previous = current.Reader;
            Volatile.Write(
                ref _targetState,
                new TargetSubscriptionState(checked(current.Epoch + 1), replacement));
        }
        previous?.Dispose();
    }

    public ParticipantPresenceSnapshot Capture()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return ParticipantPresenceSnapshot.None;
        }

        var targetState = Volatile.Read(ref _targetState);
        return AliParticipantPresenceProjection.Capture(
            targetState.Epoch,
            targetState.Reader?.Latest,
            _speaker.Latest,
            _timeProvider.GetUtcNow());
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        LatestPublishedFactReader<TargetSelectionOutput, TargetPresencePublication>? target;
        lock (_targetGate)
        {
            var current = Volatile.Read(ref _targetState);
            target = current.Reader;
            Volatile.Write(ref _targetState, new TargetSubscriptionState(current.Epoch + 1, null));
        }
        target?.Dispose();
        _speaker.Dispose();
    }

    private static TargetPresencePublication CopyTarget(TargetSelectionOutput output) => new(
        output.SequenceId,
        AsUtc(output.ProducedAtUtc),
        output.HasTarget,
        output.CurrentTargetUserId,
        output.DisplayName,
        output.LockQuality,
        output.IdentityConfidence,
        output.MediaPipeTrackGeneration);

    private static SpeakerPresencePublication CopySpeaker(SpeakerRecognitionOutput output) => new(
        output.SequenceId,
        AsUtc(output.ProducedAtUtc),
        output.Evidence.IsKnown,
        output.Evidence.PersonIdentityId,
        output.Evidence.Similarity,
        output.Evidence.IsEnrollmentUtterance);

    private static DateTimeOffset AsUtc(DateTime value) =>
        new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed record TargetSubscriptionState(
        long Epoch,
        LatestPublishedFactReader<TargetSelectionOutput, TargetPresencePublication>? Reader);
}

internal sealed record TargetPresencePublication(
    long SequenceId,
    DateTimeOffset ProducedUtc,
    bool HasTarget,
    string RegisteredProfileId,
    string DisplayLabel,
    double LockQuality,
    double IdentityConfidence,
    long TrackGeneration);

internal sealed record SpeakerPresencePublication(
    long SequenceId,
    DateTimeOffset ProducedUtc,
    bool IsKnown,
    string RegisteredProfileId,
    double Similarity,
    bool IsEnrollmentUtterance);

internal static class AliParticipantPresenceProjection
{
    internal static readonly TimeSpan TargetFreshness = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan SpeakerFreshness = TimeSpan.FromSeconds(15);

    internal static ParticipantPresenceSnapshot Capture(
        long targetEpoch,
        TargetPresencePublication? target,
        SpeakerPresencePublication? speaker,
        DateTimeOffset now)
    {
        var targetFresh = IsFresh(target?.ProducedUtc, now, TargetFreshness);
        var speakerFresh = IsFresh(speaker?.ProducedUtc, now, SpeakerFreshness);
        var observations = new List<ParticipantPresenceObservation>(2);

        if (targetFresh && target is { HasTarget: true })
        {
            var registeredId = NormalizeOptional(target.RegisteredProfileId);
            observations.Add(new ParticipantPresenceObservation(
                target.TrackGeneration > 0
                    ? $"vision-target:{target.TrackGeneration}"
                    : "vision-target:untracked",
                registeredId,
                string.IsNullOrWhiteSpace(target.DisplayLabel)
                    ? "Visible participant"
                    : target.DisplayLabel.Trim(),
                ParticipantPresenceState.Present,
                "target-selection",
                registeredId is null
                    ? FiniteConfidence(target.LockQuality)
                    : FiniteConfidence(target.IdentityConfidence)));
        }

        if (speakerFresh && speaker is { IsEnrollmentUtterance: false })
        {
            var registeredId = speaker.IsKnown
                ? NormalizeOptional(speaker.RegisteredProfileId)
                : null;
            observations.Add(new ParticipantPresenceObservation(
                registeredId is null ? "speaker:unknown" : $"speaker:{registeredId}",
                registeredId,
                registeredId is null ? "Unknown speaker" : "Recognized speaker",
                ParticipantPresenceState.Present,
                "speaker-recognition",
                FiniteConfidence(speaker.Similarity)));
        }

        var generation = MaterialGeneration(
            targetEpoch,
            targetFresh ? target : null,
            speakerFresh ? speaker : null);
        return new ParticipantPresenceSnapshot(generation, observations).Normalize();
    }

    private static string MaterialGeneration(
        long targetEpoch,
        TargetPresencePublication? target,
        SpeakerPresencePublication? speaker)
    {
        var targetIdentity = target is not { HasTarget: true }
            ? "absent"
            : NormalizeOptional(target.RegisteredProfileId) is { } registeredTarget
                ? $"registered:{registeredTarget}:track:{target.TrackGeneration}"
                : target.TrackGeneration > 0
                    ? $"unknown-track:{target.TrackGeneration}"
                    : "unknown-untracked";
        var speakerIdentity = speaker is null or { IsEnrollmentUtterance: true }
            ? "absent"
            : speaker.IsKnown
                && NormalizeOptional(speaker.RegisteredProfileId) is { } registeredSpeaker
                    ? $"registered:{registeredSpeaker}"
                    : "unknown";
        var canonical = string.Join(
            '\n',
            $"camera-epoch:{targetEpoch}",
            $"target:{targetIdentity}",
            $"speaker:{speakerIdentity}");
        var fingerprint = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return $"participant-presence-live-v2:{fingerprint}";
    }

    private static bool IsFresh(
        DateTimeOffset? producedUtc,
        DateTimeOffset now,
        TimeSpan maximumAge)
    {
        if (producedUtc is null)
        {
            return false;
        }
        var age = now - producedUtc.Value;
        return age >= TimeSpan.Zero && age <= maximumAge;
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double FiniteConfidence(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0d, 1d) : 0d;
}

internal sealed class LatestPublishedFactReader<TOutput, TFact> : IDisposable
    where TOutput : ModuleOutput, IModuleSnapshot
    where TFact : class
{
    private readonly IModuleOutputSubscription<TOutput> _subscription;
    private readonly Func<TOutput, TFact> _copy;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private TFact? _latest;
    private int _disposed;

    internal LatestPublishedFactReader(
        IModuleOutputSource<TOutput> source,
        Func<TOutput, TFact> copy,
        string threadName)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(copy);
        _subscription = source.Subscribe();
        _copy = copy;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = threadName
        };
        _thread.Start();
    }

    internal TFact? Latest => Volatile.Read(ref _latest);

    private void ReadLoop()
    {
        using var cursor = new SnapshotCursor<TOutput>();
        var signals = new[] { _subscription.OutputAvailable, _stop };
        try
        {
            while (Volatile.Read(ref _disposed) == 0)
            {
                if (WaitHandle.WaitAny(signals) == 1)
                {
                    return;
                }

                while (_subscription.TryTake(cursor))
                {
                    var fact = _copy(cursor.Current);
                    cursor.Release();
                    Volatile.Write(ref _latest, fact);
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // The producer can disappear during application shutdown.
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Set();
        if (_thread != Thread.CurrentThread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
        _subscription.Dispose();
        _stop.Dispose();
        Volatile.Write(ref _latest, null);
    }
}
