using System.Globalization;
using System.Text;
using Ali.Modules.Runtime.Models;
using Microsoft.Extensions.AI;
using Microsoft.ML.Tokenizers;

namespace Ali.Modules.Orchestration.Planning;

internal sealed record AliPlanningInputCharge(
    long ChargedTokens,
    string CounterMode,
    bool CanSafelyCharge,
    string? FailureCode = null);

internal interface IAliPlanningInputCounter
{
    AliPlanningInputCharge Count(
        ModelProfile profile,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        AIFunctionDeclaration? protocol);
}

internal enum AliModelInputPurpose
{
    Planning,
    Completion
}

internal sealed record AliPlanningInputAdmissionResult(
    bool IsAdmitted,
    int ConfiguredContextTokens,
    int ConfiguredOutputTokenLimit,
    long? CalculatedInputCharge,
    long InputBudget,
    string CounterMode,
    string? FailureCode,
    AliModelInputPurpose Purpose = AliModelInputPurpose.Planning)
{
    internal string ToUserVisibleMessage()
    {
        var charge = CalculatedInputCharge is { } calculated
            ? calculated.ToString("N0", CultureInfo.InvariantCulture) + " tokens"
            : string.Equals(
                FailureCode,
                "invalid-model-context-settings",
                StringComparison.Ordinal)
                ? "not calculated because the configured limits are invalid"
                : "not calculated safely because the input counter rejected the request";
        var measurement = "Configured context: "
            + ConfiguredContextTokens.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; configured output reserve: "
            + ConfiguredOutputTokenLimit.ToString("N0", CultureInfo.InvariantCulture)
            + " tokens; calculated input charge: " + charge
            + "; counter mode: " + CounterMode + ". ";
        return Purpose switch
        {
            AliModelInputPurpose.Planning =>
                "Ali paused this turn before contacting the model because the complete planning "
                + "input was not admitted. " + measurement
                + "The exact request, accepted conversation text and roles, steering, durable state "
                + "and evidence, capability binding, and attachment binding digest remain unchanged; "
                + "no model input was truncated or omitted. Raw attachment bytes are current-turn "
                + "material and must be reattached exactly before an explicit resume.",
            AliModelInputPurpose.Completion =>
                "Ali paused before contacting the answer composer because the complete completion "
                + "dossier was not admitted. " + measurement
                + "The immutable request, accepted work graph, and accepted evidence remain durably "
                + "preserved. No composer call ran, no final answer was generated, prepared, or "
                + "published, and no completion input was truncated or omitted.",
            _ => throw new InvalidOperationException("The model-input purpose is invalid.")
        };
    }
}

/// <summary>
/// Performs a fail-closed, read-only admission check immediately before a planning model call.
/// It never mutates the configured limits or any model-visible material.
/// </summary>
internal sealed class AliPlanningInputAdmission
{
    private readonly IAliPlanningInputCounter _counter;

    internal AliPlanningInputAdmission(IAliPlanningInputCounter? counter = null)
    {
        _counter = counter ?? AliModelAwarePlanningInputCounter.Instance;
    }

    internal AliPlanningInputAdmissionResult Evaluate(
        ModelProfile profile,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        AIFunctionDeclaration protocol)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        return EvaluateCore(
            profile,
            messages,
            selectedTools,
            protocol,
            AliModelInputPurpose.Planning);
    }

    internal AliPlanningInputAdmissionResult EvaluateCompletion(
        ModelProfile profile,
        IReadOnlyList<ChatMessage> messages) =>
        EvaluateCore(
            profile,
            messages,
            Array.Empty<AIFunctionDeclaration>(),
            protocol: null,
            purpose: AliModelInputPurpose.Completion);

    private AliPlanningInputAdmissionResult EvaluateCore(
        ModelProfile profile,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        AIFunctionDeclaration? protocol,
        AliModelInputPurpose purpose)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(selectedTools);

        if (profile.ContextTokens <= 0
            || profile.OutputTokenLimit <= 0
            || profile.OutputTokenLimit >= profile.ContextTokens)
        {
            return new AliPlanningInputAdmissionResult(
                IsAdmitted: false,
                profile.ContextTokens,
                profile.OutputTokenLimit,
                CalculatedInputCharge: null,
                InputBudget: Math.Max(0L, (long)profile.ContextTokens - profile.OutputTokenLimit),
                CounterMode: "configuration-validation",
                FailureCode: "invalid-model-context-settings",
                Purpose: purpose);
        }

        AliPlanningInputCharge charge;
        try
        {
            charge = _counter.Count(profile, messages, selectedTools, protocol);
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            return new AliPlanningInputAdmissionResult(
                IsAdmitted: false,
                profile.ContextTokens,
                profile.OutputTokenLimit,
                CalculatedInputCharge: null,
                InputBudget: (long)profile.ContextTokens - profile.OutputTokenLimit,
                CounterMode: "counter-failed-closed",
                FailureCode: "model-input-counter-failed",
                Purpose: purpose);
        }

        var inputBudget = (long)profile.ContextTokens - profile.OutputTokenLimit;
        var chargeShapeValid = charge.ChargedTokens >= 0
            && !string.IsNullOrWhiteSpace(charge.CounterMode);
        var admitted = chargeShapeValid
            && charge.CanSafelyCharge
            && charge.ChargedTokens <= inputBudget;
        return new AliPlanningInputAdmissionResult(
            admitted,
            profile.ContextTokens,
            profile.OutputTokenLimit,
            chargeShapeValid && charge.CanSafelyCharge
                ? charge.ChargedTokens
                : null,
            inputBudget,
            string.IsNullOrWhiteSpace(charge.CounterMode)
                ? "invalid-counter-result"
                : charge.CounterMode,
            admitted
                ? null
                : !chargeShapeValid
                    ? "invalid-model-input-counter-result"
                    : charge.FailureCode ?? "model-input-exceeds-configured-context",
            Purpose: purpose);
    }
}

/// <summary>
/// GPT-OSS text is counted with its o200k tokenizer. Fixed segment reserves cover Harmony/chat
/// framing that is not represented by <see cref="ChatMessage"/>. Binary transport is charged at
/// one token per Base64 character, a conservative upper bound that avoids allocating Base64 text.
/// Unknown model families use one admission unit per UTF-8 byte, also an upper bound for a
/// byte-level tokenizer; this mode intentionally does not claim to be an exact tokenizer count.
/// </summary>
internal sealed class AliModelAwarePlanningInputCounter : IAliPlanningInputCounter
{
    internal const int ChatProtocolReserveTokens = 512;
    internal const int MessageSegmentOverheadTokens = 16;
    internal const int ContentSegmentOverheadTokens = 8;
    internal const int ToolSegmentOverheadTokens = 32;
    internal const int BinarySegmentOverheadTokens = 32;

    private static readonly Lazy<GptOssTokenizerState> GptOssTokenizer = new(
        CreateGptOssTokenizer,
        LazyThreadSafetyMode.ExecutionAndPublication);

    internal static AliModelAwarePlanningInputCounter Instance { get; } = new();

    private AliModelAwarePlanningInputCounter()
    {
    }

    public AliPlanningInputCharge Count(
        ModelProfile profile,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        AIFunctionDeclaration? protocol)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(selectedTools);

        if (IsGptOss(profile))
        {
            GptOssTokenizerState state;
            try
            {
                state = GptOssTokenizer.Value;
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                                              and not OutOfMemoryException)
            {
                return new AliPlanningInputCharge(
                    0,
                    "gpt-oss-tokenizer-unavailable",
                    CanSafelyCharge: false,
                    FailureCode: "gpt-oss-tokenizer-unavailable");
            }

            return CountCore(
                messages,
                selectedTools,
                protocol,
                state.Mode,
                text => state.Tokenizer.CountTokens(text));
        }

        return CountCore(
            messages,
            selectedTools,
            protocol,
            "utf8-byte-upper-bound",
            static text => Encoding.UTF8.GetByteCount(text));
    }

    private static AliPlanningInputCharge CountCore(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunctionDeclaration> selectedTools,
        AIFunctionDeclaration? protocol,
        string mode,
        Func<string, int> countText)
    {
        long charge = ChatProtocolReserveTokens;
        try
        {
            foreach (var message in messages)
            {
                if (message is null)
                {
                    return Unsafe("null-message");
                }

                Add(MessageSegmentOverheadTokens);
                AddText(message.Role.ToString());
                AddText(message.AuthorName);
                foreach (var content in message.Contents)
                {
                    if (content is null)
                    {
                        return Unsafe("null-content");
                    }

                    Add(ContentSegmentOverheadTokens);
                    switch (content)
                    {
                        case TextContent text:
                            AddText(text.Text);
                            break;
                        case DataContent data:
                            AddText(data.MediaType ?? string.Empty);
                            AddText(data.Name ?? string.Empty);
                            Add(BinarySegmentOverheadTokens);
                            Add(Base64CharacterCount(data.Data.Length));
                            break;
                        default:
                            return Unsafe(
                                "unsupported-content-type-"
                                + content.GetType().Name.ToLowerInvariant());
                    }
                }
            }

            foreach (var tool in selectedTools)
            {
                if (tool is null)
                {
                    return Unsafe("null-selected-tool");
                }

                AddTool(tool);
            }

            // Planning sends the orchestration protocol either as a required native function or
            // as the compatibility response schema, so charge it once in both planning modes.
            // The final composer sends neither tools nor a protocol and therefore passes null.
            if (protocol is not null)
            {
                AddTool(protocol);
            }
            return new AliPlanningInputCharge(charge, mode, CanSafelyCharge: true);
        }
        catch (OverflowException)
        {
            return new AliPlanningInputCharge(
                long.MaxValue,
                mode,
                CanSafelyCharge: false,
                FailureCode: "model-input-charge-overflow");
        }

        AliPlanningInputCharge Unsafe(string reason) =>
            new(
                charge,
                mode,
                CanSafelyCharge: false,
                FailureCode: "model-input-unchargeable-" + reason);

        void Add(long value)
        {
            charge = checked(charge + value);
        }

        void AddText(string? value)
        {
            Add(countText(value ?? string.Empty));
        }

        void AddTool(AIFunctionDeclaration tool)
        {
            Add(ToolSegmentOverheadTokens);
            AddText(tool.Name);
            AddText(tool.Description);
            AddText(tool.JsonSchema.GetRawText());
        }
    }

    private static long Base64CharacterCount(int byteCount)
    {
        if (byteCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(byteCount));
        }

        return checked(((long)byteCount + 2L) / 3L * 4L);
    }

    private static bool IsGptOss(ModelProfile profile) =>
        ContainsGptOss(profile.PackageId)
        || ContainsGptOss(profile.Family)
        || ContainsGptOss(profile.DisplayName);

    private static bool ContainsGptOss(string? value) =>
        value?.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase) == true;

    private static GptOssTokenizerState CreateGptOssTokenizer()
    {
        try
        {
            return new GptOssTokenizerState(
                TiktokenTokenizer.CreateForModel("gpt-oss-20b"),
                "gpt-oss-o200k-harmony-text-exact-with-conservative-protocol-reserve");
        }
        catch (Exception exception) when (exception is not OperationCanceledException
                                          and not OutOfMemoryException)
        {
            try
            {
                return new GptOssTokenizerState(
                    TiktokenTokenizer.CreateForEncoding("o200k_harmony"),
                    "gpt-oss-explicit-o200k-harmony-text-exact-with-conservative-protocol-reserve");
            }
            catch (Exception encodingException) when (
                encodingException is not OperationCanceledException
                and not OutOfMemoryException)
            {
                // Alias lookup is an optional convenience. Microsoft.ML.Tokenizers 2.0 can
                // still provide the exact ordinary o200k BPE ranks when this pinned runtime
                // cannot resolve either Harmony name. Harmony special-token and chat framing
                // then remain covered by the explicit fixed/segment reserves above. If base
                // construction also fails, Count() rejects the request closed instead of guessing.
                return new GptOssTokenizerState(
                    TiktokenTokenizer.CreateForEncoding("o200k_base"),
                    "gpt-oss-o200k-base-text-exact-with-conservative-harmony-reserve");
            }
        }
    }

    private sealed record GptOssTokenizerState(Tokenizer Tokenizer, string Mode);
}
