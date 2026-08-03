namespace Ali.Modules.UserMemory;

/// <summary>
/// Process-local issuer and verifier for short-lived participant-memory receipts.
/// Model arguments can copy receipt-shaped JSON, but only records issued through this
/// authority are admitted by the service.
/// </summary>
public sealed class ParticipantMemoryReceiptAuthority
{
    private const int MaximumReceipts = 4_096;
    private const int MaximumConsentSessionBindings = 4_096;
    private readonly Dictionary<string, object> _receipts = new(StringComparer.Ordinal);
    private readonly Queue<string> _oldest = new();
    private readonly Dictionary<string, ConsentSessionBinding> _consentSessionBindings =
        new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public ParticipantMemoryPermissionReceipt IssuePermission(
        string principalParticipantReference,
        IReadOnlyList<string> operations,
        string sourceCallId,
        string source,
        DateTimeOffset issuedUtc,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalParticipantReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCallId);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
        var normalizedOperations = NormalizeOperations(operations);
        if (normalizedOperations.Count == 0)
        {
            throw new ArgumentException("At least one exact permission operation is required.", nameof(operations));
        }

        return Store(new ParticipantMemoryPermissionReceipt(
            $"permission:{Guid.NewGuid():N}",
            principalParticipantReference.Trim(),
            normalizedOperations,
            issuedUtc,
            issuedUtc.Add(lifetime),
            sourceCallId.Trim(),
            source.Trim()));
    }

    public ParticipantMemoryConsentReceipt IssueConsent(
        ParticipantMemoryPermissionReceipt permission,
        string operation,
        string proposalFingerprint,
        string consentSessionId,
        ParticipantMemoryVisibility visibility,
        IReadOnlyList<string> audienceParticipantReferences,
        string sourceTurnId,
        DateTimeOffset issuedUtc,
        TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposalFingerprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(consentSessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTurnId);
        if (!IsIssued(permission)
            || !permission.IsCurrent(issuedUtc)
            || !permission.Grants(operation))
        {
            throw new InvalidOperationException(
                "Consent can be issued only from a trusted exact-operation permission receipt.");
        }
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        return Store(new ParticipantMemoryConsentReceipt(
            $"consent:{Guid.NewGuid():N}",
            permission.PrincipalParticipantReference,
            operation.Trim(),
            proposalFingerprint.Trim(),
            consentSessionId.Trim(),
            visibility,
            NormalizeReferences(audienceParticipantReferences),
            issuedUtc,
            issuedUtc.Add(lifetime),
            sourceTurnId.Trim()));
    }

    internal ParticipantMemoryAuthenticationReceipt IssueTestAuthentication(
        string principalParticipantReference,
        IReadOnlyList<string> operations,
        DateTimeOffset issuedUtc,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalParticipantReference);
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
        var normalizedOperations = NormalizeOperations(operations);
        if (normalizedOperations.Count == 0)
        {
            throw new ArgumentException("At least one exact authentication operation is required.", nameof(operations));
        }
        return Store(new ParticipantMemoryAuthenticationReceipt(
            $"authentication:{Guid.NewGuid():N}",
            principalParticipantReference.Trim(),
            ParticipantMemoryAuthenticationKind.TrustedTestFactor,
            issuedUtc,
            issuedUtc.Add(lifetime),
            normalizedOperations));
    }

    internal ParticipantMemoryAuthenticationReceipt IssueAuthentication(
        string principalParticipantReference,
        ParticipantMemoryAuthenticationKind kind,
        IReadOnlyList<string> operations,
        DateTimeOffset issuedUtc,
        TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalParticipantReference);
        if (kind is not ParticipantMemoryAuthenticationKind.WindowsHello
            and not ParticipantMemoryAuthenticationKind.Passkey
            and not ParticipantMemoryAuthenticationKind.LocalCredential)
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                "Production authentication must use an independent trusted factor.");
        }
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }
        var normalizedOperations = NormalizeOperations(operations);
        if (normalizedOperations.Count == 0)
        {
            throw new ArgumentException("At least one exact authentication operation is required.", nameof(operations));
        }
        return Store(new ParticipantMemoryAuthenticationReceipt(
            $"authentication:{Guid.NewGuid():N}",
            principalParticipantReference.Trim(),
            kind,
            issuedUtc,
            issuedUtc.Add(lifetime),
            normalizedOperations));
    }

    public bool IsIssued(ParticipantMemoryPermissionReceipt receipt) => IsExact(receipt);

    public bool IsIssued(ParticipantMemoryConsentReceipt receipt) => IsExact(receipt);

    public bool IsIssued(ParticipantMemoryAuthenticationReceipt receipt) => IsExact(receipt);

    public bool TryBindConsentSession(
        IReadOnlyList<ParticipantMemoryConsentReceipt> receipts,
        string mutationRequestId)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentException.ThrowIfNullOrWhiteSpace(mutationRequestId);
        var sessions = receipts
            .Select(receipt => receipt.ConsentSessionId?.Trim() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (receipts.Count == 0 || sessions.Length != 1 || sessions[0].Length == 0)
        {
            return false;
        }
        var exactRequestId = mutationRequestId.Trim();
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            if (receipts.Any(receipt => !_receipts.TryGetValue(receipt.ReceiptId, out var issued)
                    || !Equals(issued, receipt)
                    || !receipt.IsCurrent(now)))
            {
                return false;
            }
            if (_consentSessionBindings.TryGetValue(sessions[0], out var existing))
            {
                if (ConsentSessionBindingIsLive(sessions[0], existing, now))
                {
                    return string.Equals(
                        existing.MutationRequestId,
                        exactRequestId,
                        StringComparison.Ordinal);
                }
                _consentSessionBindings.Remove(sessions[0]);
            }
            if (_consentSessionBindings.Count >= MaximumConsentSessionBindings)
            {
                PruneConsentSessionBindings(now);
                if (_consentSessionBindings.Count >= MaximumConsentSessionBindings)
                {
                    return false;
                }
            }
            _consentSessionBindings.Add(
                sessions[0],
                new ConsentSessionBinding(
                    exactRequestId,
                    receipts.Any(receipt => receipt.ExpiresUtc is null)
                        ? null
                        : receipts.Max(receipt => receipt.ExpiresUtc)));
            return true;
        }
    }

    private bool ConsentSessionBindingIsLive(
        string consentSessionId,
        ConsentSessionBinding binding,
        DateTimeOffset now) =>
        (binding.ExpiresUtc is null || binding.ExpiresUtc > now)
        && _receipts.Values
            .OfType<ParticipantMemoryConsentReceipt>()
            .Any(receipt => receipt.IsCurrent(now)
                && string.Equals(
                    receipt.ConsentSessionId,
                    consentSessionId,
                    StringComparison.Ordinal));

    private void PruneConsentSessionBindings(DateTimeOffset now)
    {
        var liveSessions = _receipts.Values
            .OfType<ParticipantMemoryConsentReceipt>()
            .Where(receipt => receipt.IsCurrent(now))
            .Select(receipt => receipt.ConsentSessionId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var sessionId in _consentSessionBindings
            .Where(pair => (pair.Value.ExpiresUtc is not null
                    && pair.Value.ExpiresUtc <= now)
                || !liveSessions.Contains(pair.Key))
            .Select(pair => pair.Key)
            .ToArray())
        {
            _consentSessionBindings.Remove(sessionId);
        }
    }

    private T Store<T>(T receipt) where T : notnull
    {
        var id = receipt switch
        {
            ParticipantMemoryPermissionReceipt value => value.ReceiptId,
            ParticipantMemoryConsentReceipt value => value.ReceiptId,
            ParticipantMemoryAuthenticationReceipt value => value.ReceiptId,
            _ => throw new InvalidOperationException("Unsupported participant-memory receipt type.")
        };
        lock (_sync)
        {
            while (_receipts.Count >= MaximumReceipts)
            {
                _receipts.Remove(_oldest.Dequeue());
            }
            _receipts.Add(id, receipt);
            _oldest.Enqueue(id);
        }
        return receipt;
    }

    private bool IsExact<T>(T receipt) where T : notnull
    {
        var id = receipt switch
        {
            ParticipantMemoryPermissionReceipt value => value.ReceiptId,
            ParticipantMemoryConsentReceipt value => value.ReceiptId,
            ParticipantMemoryAuthenticationReceipt value => value.ReceiptId,
            _ => string.Empty
        };
        lock (_sync)
        {
            return _receipts.TryGetValue(id, out var issued)
                && Equals(issued, receipt);
        }
    }

    private sealed record ConsentSessionBinding(
        string MutationRequestId,
        DateTimeOffset? ExpiresUtc);

    private static IReadOnlyList<string> NormalizeOperations(IReadOnlyList<string> values) =>
        (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.OrdinalIgnoreCase)
        .Take(ParticipantMemoryLimits.MaximumAudienceKeys)
        .ToArray();

    private static IReadOnlyList<string> NormalizeReferences(IReadOnlyList<string> values) =>
        (values ?? [])
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .Take(ParticipantMemoryLimits.MaximumAudienceKeys)
        .ToArray();
}
