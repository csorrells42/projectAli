using System.Security.Cryptography;
using System.Text;

namespace Ali.Modules.Orchestration.Contracts;

public enum InvocationStatus
{
    Returned,
    Denied,
    Threw,
    Cancelled
}

public enum DomainOutcome
{
    Unreported,
    Succeeded,
    Failed,
    PartiallySucceeded
}

public sealed record ToolInvocationOutcome
{
    private ToolInvocationOutcome(
        InvocationStatus invocationStatus,
        DomainOutcome domainOutcome,
        string resultDigest,
        string? failureCode)
    {
        InvocationStatus = invocationStatus;
        DomainOutcome = domainOutcome;
        ResultDigest = resultDigest;
        FailureCode = failureCode;
    }

    public InvocationStatus InvocationStatus { get; }

    public DomainOutcome DomainOutcome { get; }

    public string ResultDigest { get; }

    public string? FailureCode { get; }

    public static ToolInvocationOutcome Returned(
        ReadOnlySpan<byte> redactedResult,
        bool? reportedSuccess = null,
        bool partial = false)
    {
        var domainOutcome = partial
            ? DomainOutcome.PartiallySucceeded
            : reportedSuccess switch
            {
                true => DomainOutcome.Succeeded,
                false => DomainOutcome.Failed,
                null => DomainOutcome.Unreported
            };
        return new ToolInvocationOutcome(
            InvocationStatus.Returned,
            domainOutcome,
            Digest(redactedResult),
            null);
    }

    public static ToolInvocationOutcome Denied(string? failureCode = null) =>
        new(InvocationStatus.Denied, DomainOutcome.Unreported, Digest([]), failureCode);

    public static ToolInvocationOutcome Threw(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ToolInvocationOutcome(
            InvocationStatus.Threw,
            DomainOutcome.Unreported,
            Digest(Encoding.UTF8.GetBytes(exception.GetType().FullName ?? exception.GetType().Name)),
            exception.GetType().FullName);
    }

    public static ToolInvocationOutcome Cancelled() =>
        new(InvocationStatus.Cancelled, DomainOutcome.Unreported, Digest([]), null);

    public string Fingerprint()
    {
        var material = $"{(int)InvocationStatus}|{(int)DomainOutcome}|{ResultDigest}|{FailureCode ?? string.Empty}";
        return Digest(Encoding.UTF8.GetBytes(material));
    }

    private static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
