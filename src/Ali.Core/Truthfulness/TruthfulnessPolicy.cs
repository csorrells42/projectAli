using Ali.Core.Evidence;

namespace Ali.Core.Truthfulness;

public static class TruthfulnessPolicy
{
    public const string LocalOnlyInferenceRule =
        "Ali must not send prompts, screenshots, code, memories, conversations, or attachments to a third-party model API.";

    public const string NoFakeSuccessRule =
        "Ali must not claim an action succeeded without a verified receipt.";

    public static EvidenceStatus EvidenceFromReceipt(ActionReceipt? receipt)
    {
        if (receipt is null)
        {
            return EvidenceStatus.Unknown;
        }

        return receipt.EvidenceStatus == EvidenceStatus.Verified
            ? EvidenceStatus.Verified
            : EvidenceStatus.Unverified;
    }

    public static string DescribeActionStatus(ActionReceipt? receipt)
    {
        if (receipt is null)
        {
            return "Unknown: no action receipt exists.";
        }

        return receipt.Succeeded
            ? $"Verified: {receipt.Summary}"
            : $"Verified failure: {receipt.Summary}";
    }
}
