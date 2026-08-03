namespace Ali.Modules.UserMemory;

/// <summary>
/// Immutable advisory presence state captured from independent live producers.
/// Presence never selects, authenticates, or grants authority to a participant.
/// </summary>
public sealed record ParticipantPresenceSnapshot(
    string Generation,
    IReadOnlyList<ParticipantPresenceObservation> Observations)
{
    public static ParticipantPresenceSnapshot None { get; } = new(
        SelectedParticipantRosterAuthority.NoPresenceGeneration,
        []);

    public ParticipantPresenceSnapshot Normalize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Generation);
        ArgumentNullException.ThrowIfNull(Observations);
        if (Observations.Count > ParticipantMemoryLimits.MaximumParticipantsPerTurn)
        {
            throw new ArgumentOutOfRangeException(
                nameof(Observations),
                $"A presence snapshot may contain at most {ParticipantMemoryLimits.MaximumParticipantsPerTurn} observations.");
        }

        var normalized = Observations.Select(observation =>
        {
            ArgumentNullException.ThrowIfNull(observation);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.SessionReferenceId);
            if (observation.Confidence.HasValue
                && (!double.IsFinite(observation.Confidence.Value)
                    || observation.Confidence is < 0 or > 1))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(Observations),
                    "Presence confidence must be between zero and one.");
            }

            return observation with
            {
                SessionReferenceId = observation.SessionReferenceId.Trim(),
                RegisteredProfileId = string.IsNullOrWhiteSpace(observation.RegisteredProfileId)
                    ? null
                    : observation.RegisteredProfileId.Trim(),
                DisplayLabel = string.IsNullOrWhiteSpace(observation.DisplayLabel)
                    ? "Unknown participant"
                    : observation.DisplayLabel.Trim(),
                Source = string.IsNullOrWhiteSpace(observation.Source)
                    ? "not-observed"
                    : observation.Source.Trim()
            };
        }).ToArray();

        return this with
        {
            Generation = Generation.Trim(),
            Observations = normalized
        };
    }
}

public interface IParticipantPresenceSnapshotSource
{
    ParticipantPresenceSnapshot Capture();
}

/// <summary>
/// Late-bound production bridge. The desktop interaction runtime can attach after
/// coordinator composition without making memory depend on camera or microphone
/// startup. A detached or failed advisory source yields an empty presence snapshot.
/// </summary>
public sealed class ParticipantPresenceSnapshotBridge : IParticipantPresenceSnapshotSource
{
    private Binding? _binding;
    private long _nextBindingId;

    public IDisposable Attach(IParticipantPresenceSnapshotSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var binding = new Binding(Interlocked.Increment(ref _nextBindingId), source);
        Volatile.Write(ref _binding, binding);
        return new Attachment(this, binding.Id);
    }

    public ParticipantPresenceSnapshot Capture()
    {
        var source = Volatile.Read(ref _binding)?.Source;
        if (source is null)
        {
            return ParticipantPresenceSnapshot.None;
        }

        try
        {
            return source.Capture().Normalize();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // Advisory presence must never destabilize turn admission. The distinct
            // generation invalidates any roster admitted while the source was live.
            return new ParticipantPresenceSnapshot(
                "participant-presence-source-unavailable-v1",
                []);
        }
    }

    private void Detach(long bindingId)
    {
        var current = Volatile.Read(ref _binding);
        if (current is not null && current.Id == bindingId)
        {
            Interlocked.CompareExchange(ref _binding, null, current);
        }
    }

    private sealed record Binding(long Id, IParticipantPresenceSnapshotSource Source);

    private sealed class Attachment(
        ParticipantPresenceSnapshotBridge owner,
        long bindingId) : IDisposable
    {
        private ParticipantPresenceSnapshotBridge? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Detach(bindingId);
    }
}
