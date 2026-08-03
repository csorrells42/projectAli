namespace Ali.Modules.Coding.RoslynActions;

public sealed record AliRoslynActionVerification(
    bool Success,
    string HandleId,
    string HandleState,
    string? VerificationReceiptId,
    string? VerificationDigest,
    bool RoslynSucceeded,
    bool BuildSucceeded,
    int TestsRun,
    bool TestsSucceeded,
    string OutcomeCode,
    string Summary)
{
    internal static AliRoslynActionVerification Failed(
        string handleId,
        string handleState,
        string outcomeCode,
        string summary,
        bool roslynSucceeded = false,
        bool buildSucceeded = false,
        int testsRun = 0,
        bool testsSucceeded = false) =>
        new(
            false,
            handleId,
            handleState,
            null,
            null,
            roslynSucceeded,
            buildSucceeded,
            testsRun,
            testsSucceeded,
            outcomeCode,
            summary);

    internal static AliRoslynActionVerification FromVerifiedHandle(
        AliRoslynActionHandle handle)
    {
        var receipt = handle.Verification
            ?? throw new InvalidDataException("The verified handle has no verification receipt.");
        return new(
            true,
            handle.Id,
            handle.State.ToString(),
            receipt.Id,
            receipt.VerificationDigest,
            receipt.RoslynSucceeded,
            receipt.BuildSucceeded,
            receipt.TestsRun,
            receipt.TestsSucceeded,
            "already-verified",
            "The protected handle already has a current successful staged verification receipt.");
    }
}
