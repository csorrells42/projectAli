using Ali.Modules.Coordinator;
using System.Text;
using System.Text.RegularExpressions;

namespace Ali.UI.ViewModels;

public sealed class AgentActivityItemViewModel
{
    private const string TechnicalPayloadOmission = "Technical payload omitted from the human activity view.";
    private const string FormattingFallback = "Activity update available; technical formatting was omitted safely.";
    private const int MaximumDisplayCharacters = 320;
    private const int MaximumPathFormattingInputCharacters = 2_048;
    private static readonly TimeSpan PathRegexTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly Regex SingleQuotedAbsolutePathPattern = CreatePathRegex(
        @"(?<![\p{L}\p{N}_])(?<quote>')(?<path>(?:[A-Za-z]:[\\/]|\\\\|/)[^'\r\n]+)\k<quote>(?![\p{L}\p{N}_])");
    private static readonly Regex QuotedPathPattern = CreatePathRegex(
        @"(?<quote>[""`])(?<path>[^""`\r\n]*[\\/][^""`\r\n]+)\k<quote>");
    private static readonly Regex WindowsPathConnectorPattern = CreatePathRegex(
        @"\s+(?i:to|from|and)\s+(?=(?:[A-Za-z]:[\\/]|\\\\))");
    private static readonly Regex WindowsAbsoluteFilePathPattern = CreatePathRegex(
        @"(?<![\p{L}\p{N}_])(?<path>(?:[A-Za-z]:[\\/]|\\\\)(?:[^\\/""'`<>\r\n|]+[\\/])*[^\\/""'`<>\r\n|]+?\.[A-Za-z0-9]{1,16})(?=$|[\s.,;:!?\)\]\}])");
    private static readonly Regex UnixAbsoluteFilePathPattern = CreatePathRegex(
        @"(?<![:/\p{L}\p{N}_])(?<path>/(?:[^/""'`<>\r\n|]+/)*[^/""'`<>\r\n|]+?\.[A-Za-z0-9]{1,16})(?=$|[\s.,;:!?\)\]\}])");
    private static readonly Regex WindowsAbsoluteDirectoryPathPattern = CreatePathRegex(
        @"(?<![\p{L}\p{N}_])(?<path>(?:[A-Za-z]:[\\/]|\\\\)(?:[^\\/:""'`<>\r\n|]+[\\/])+[^\\/:""'`<>\r\n|]+?)(?=$|[.,;!?\)\]\}])");
    private static readonly Regex WindowsAbsolutePathPattern = CreatePathRegex(
        @"(?<![\p{L}\p{N}_])(?<path>(?:[A-Za-z]:[\\/]|\\\\)[^\s""'`<>|]+)");
    private static readonly Regex UnixAbsolutePathPattern = CreatePathRegex(
        @"(?<![:/\p{L}\p{N}_])(?<path>/(?:[^\s/""'`<>|]+/)+[^\s""'`<>|]+)");
    private static readonly Regex RelativeFilePathPattern = CreatePathRegex(
        @"(?<![.:/\\\p{L}\p{N}_-])(?<path>(?:[^\s/\\""'`<>|]+[\\/])+(?:[^\s/\\""'`<>|]+\.[A-Za-z0-9]{1,16}))");
    private static readonly char[] TrailingPathPunctuation = ['.', ',', ';', ':', '!', '?', ')', ']', '}'];

    public AgentActivityItemViewModel(AssistantStreamChunk chunk)
    {
        Kind = chunk.ActivityKind ?? AgentActivityKind.Status;
        var isApprovalPayload = chunk.ApprovalPrompt is { } approvalPrompt
            && string.Equals(chunk.ActivityDetail, approvalPrompt.Arguments, StringComparison.Ordinal);
        Title = NormalizeHumanText(chunk.Text, MaximumDisplayCharacters);
        Detail = NormalizeHumanDetail(chunk.ActivityDetail, isApprovalPayload);
        DisplayTitle = NormalizeHumanDisplayText(chunk.Text, MaximumDisplayCharacters);
        DisplayDetail = NormalizeHumanDisplayDetail(chunk.ActivityDetail, isApprovalPayload);
        ExecutionReceipt = chunk.ExecutionReceipt;
        ReceiptText = BuildReceiptText(chunk.ExecutionReceipt);
        ActivityKey = chunk.ActivityKey;
        AssistantMessageId = chunk.AssistantMessageId;
        ElapsedMilliseconds = chunk.ElapsedMilliseconds;
        CreatedAt = DateTimeOffset.Now;
    }

    public AgentActivityKind Kind { get; }

    public string Title { get; }

    public string Detail { get; }

    public string DisplayTitle { get; }

    public string DisplayDetail { get; }

    public AgentToolExecutionReceipt? ExecutionReceipt { get; }

    public string ReceiptText { get; }

    public string StatusLabel => ExecutionReceipt?.Outcome switch
    {
        AgentToolExecutionOutcome.Completed => "Returned",
        AgentToolExecutionOutcome.Failed => "Failed",
        AgentToolExecutionOutcome.Cancelled => "Cancelled",
        _ => Kind switch
        {
            AgentActivityKind.Planning => "Planning",
            AgentActivityKind.ToolCall => "Working",
            AgentActivityKind.ToolResult => "Tool update",
            AgentActivityKind.Approval => "Waiting for approval",
            AgentActivityKind.Warning => "Attention",
            AgentActivityKind.Error => "Failed",
            AgentActivityKind.Complete => "Finished",
            _ => "Update"
        }
    };

    public string Headline => string.IsNullOrWhiteSpace(DisplayTitle)
        ? StatusLabel
        : DisplayTitle.StartsWith(StatusLabel + ":", StringComparison.OrdinalIgnoreCase)
            || DisplayTitleAlreadyStatesTerminal()
                ? DisplayTitle
                : $"{StatusLabel}: {DisplayTitle}";

    public string SummaryText => LimitDisplayText(
        !string.IsNullOrWhiteSpace(ReceiptText)
            ? $"{ReceiptText} - {Headline}"
            : !string.IsNullOrWhiteSpace(DisplayDetail)
                ? $"{Headline} - {DisplayDetail}"
                : Headline,
        MaximumDisplayCharacters);

    public string DisplayText => SummaryText;

    public string? ActivityKey { get; }

    public string AssistantMessageId { get; }

    public DateTimeOffset CreatedAt { get; }

    public double? ElapsedMilliseconds { get; }

    public string Icon => Kind switch
    {
        AgentActivityKind.Planning => "\uE8C3",
        AgentActivityKind.ToolCall => "\uE90F",
        AgentActivityKind.ToolResult => "\uE73E",
        AgentActivityKind.Approval => "\uE72E",
        AgentActivityKind.Warning => "\uE7BA",
        AgentActivityKind.Error => "\uEA39",
        AgentActivityKind.Complete => "\uE930",
        _ => "\uE946"
    };

    public string Accent => Kind switch
    {
        AgentActivityKind.Approval => "#F7C873",
        AgentActivityKind.Warning => "#F7C873",
        AgentActivityKind.Error => "#F28B82",
        AgentActivityKind.Complete => "#8EE6B5",
        AgentActivityKind.ToolCall => "#8DDDF0",
        AgentActivityKind.ToolResult => "#A7E3B5",
        AgentActivityKind.Planning => "#C5B3FF",
        _ => "#B9D7EF"
    };

    public string TimingText => ElapsedMilliseconds is not { } elapsed
        ? CreatedAt.ToString("h:mm:ss tt")
        : elapsed < 1000
            ? $"{elapsed:0} ms"
            : $"{elapsed / 1000:0.00} s";

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    public bool HasDisplayDetail => !string.IsNullOrWhiteSpace(DisplayDetail);

    public bool HasReceipt => !string.IsNullOrWhiteSpace(ReceiptText);

    private bool DisplayTitleAlreadyStatesTerminal() => ExecutionReceipt?.Outcome switch
    {
        AgentToolExecutionOutcome.Completed =>
            DisplayTitle.StartsWith("Returned", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.Contains(" returned", StringComparison.OrdinalIgnoreCase),
        AgentToolExecutionOutcome.Failed =>
            DisplayTitle.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.Contains(" failed", StringComparison.OrdinalIgnoreCase),
        AgentToolExecutionOutcome.Cancelled =>
            DisplayTitle.StartsWith("Cancelled", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.StartsWith("Canceled", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.Contains(" cancelled", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.Contains(" canceled", StringComparison.OrdinalIgnoreCase),
        _ when Kind == AgentActivityKind.Error =>
            DisplayTitle.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
            || DisplayTitle.Contains(" failed", StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static string NormalizeHumanDetail(string? value, bool forceOmission = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (forceOmission || IsStructuredPayload(value))
        {
            return TechnicalPayloadOmission;
        }

        return NormalizeHumanText(value, 320);
    }

    private static string NormalizeHumanDisplayDetail(string? value, bool forceOmission = false)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (forceOmission || IsStructuredPayload(value))
        {
            return TechnicalPayloadOmission;
        }

        return NormalizeHumanDisplayText(value, 320);
    }

    private static string NormalizeHumanText(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = string.Join(
            " ",
            value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= maximumCharacters
            ? normalized
            : normalized[..maximumCharacters] + "...";
    }

    private static string NormalizeHumanDisplayText(string? value, int maximumCharacters)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var boundedInput = value.Length <= MaximumPathFormattingInputCharacters
            ? value
            : value[..MaximumPathFormattingInputCharacters];
        var normalized = string.Join(
            " ",
            boundedInput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var filenameFirst = ShortenFilePaths(normalized);
        return LimitDisplayText(filenameFirst, maximumCharacters);
    }

    private static bool IsStructuredPayload(string value)
    {
        var trimmed = value.TrimStart();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("```", StringComparison.Ordinal)
            || trimmed[0] == '{')
        {
            return true;
        }

        if (trimmed[0] != '[')
        {
            return false;
        }

        return !TryGetHumanBracketedRemainder(trimmed, out var remainder)
            || StartsWithStructuredPayloadMarker(remainder);
    }

    private static bool TryGetHumanBracketedRemainder(string value, out string remainder)
    {
        remainder = string.Empty;
        var closingBracket = value.IndexOf(']');
        if (closingBracket is <= 1 or > 33)
        {
            return false;
        }

        for (var index = 1; index < closingBracket; index++)
        {
            var character = value[index];
            if (!char.IsLetterOrDigit(character)
                && !char.IsWhiteSpace(character)
                && character is not ('/' or '-' or '_' or '.' or '%'))
            {
                return false;
            }
        }

        var remainderStart = closingBracket + 1;
        while (remainderStart < value.Length
            && (char.IsWhiteSpace(value[remainderStart])
                || value[remainderStart] is ':' or '-'))
        {
            remainderStart++;
        }

        remainder = value[remainderStart..];
        return true;
    }

    private static bool StartsWithStructuredPayloadMarker(string value)
    {
        var trimmed = value.TrimStart();
        return trimmed.Length > 0
            && (trimmed.StartsWith("Arguments:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("```", StringComparison.Ordinal)
                || trimmed[0] is '{' or '[');
    }

    private static string BuildReceiptText(AgentToolExecutionReceipt? receipt)
    {
        if (receipt is null)
        {
            return string.Empty;
        }

        var toolName = HumanizeIdentifier(receipt.ToolName);
        var outcome = receipt.Outcome switch
        {
            AgentToolExecutionOutcome.Completed => "returned",
            AgentToolExecutionOutcome.Failed => "failed",
            AgentToolExecutionOutcome.Cancelled => "was cancelled",
            _ => "reported an update"
        };
        var summary = NormalizeHumanDisplayDetail(receipt.Summary);
        return string.IsNullOrWhiteSpace(summary)
            ? $"Runtime receipt: {toolName} {outcome}."
            : $"Runtime receipt: {toolName} {outcome}. {summary}";
    }

    private static string HumanizeIdentifier(string? value)
    {
        var normalized = string.Join(
            " ",
            (value ?? string.Empty)
                .Split(['_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (normalized.Length == 0)
        {
            return "Tool";
        }

        return char.ToUpperInvariant(normalized[0]) + normalized[1..];
    }

    private static string ShortenFilePaths(string value)
    {
        try
        {
            var connectors = WindowsPathConnectorPattern.Matches(value);
            if (connectors.Count == 0)
            {
                return ShortenFilePathsSegment(value);
            }

            var shortened = new StringBuilder(value.Length);
            var segmentStart = 0;
            foreach (Match connector in connectors)
            {
                shortened.Append(ShortenFilePathsSegment(value[segmentStart..connector.Index]));
                shortened.Append(connector.Value);
                segmentStart = connector.Index + connector.Length;
            }

            shortened.Append(ShortenFilePathsSegment(value[segmentStart..]));
            return shortened.ToString();
        }
        catch (RegexMatchTimeoutException)
        {
            return FormattingFallback;
        }
    }

    private static string ShortenFilePathsSegment(string value)
    {
        var shortened = SingleQuotedAbsolutePathPattern.Replace(value, ReplaceQuotedPath);
        shortened = QuotedPathPattern.Replace(shortened, ReplaceQuotedPath);
        shortened = WindowsAbsoluteFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = UnixAbsoluteFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = WindowsAbsoluteDirectoryPathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = WindowsAbsolutePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = UnixAbsolutePathPattern.Replace(shortened, ReplaceUnquotedPath);
        return RelativeFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
    }

    private static string ReplaceQuotedPath(Match match)
    {
        var path = match.Groups["path"].Value;
        if (!TryGetFilenameOnly(path, out var filename))
        {
            return match.Value;
        }

        var quote = match.Groups["quote"].Value;
        return $"{quote}{filename}{quote}";
    }

    private static string ReplaceUnquotedPath(Match match)
    {
        var path = match.Groups["path"].Value;
        var coreLength = path.Length;
        while (coreLength > 0 && TrailingPathPunctuation.Contains(path[coreLength - 1]))
        {
            coreLength--;
        }

        var core = path[..coreLength];
        if (!TryGetFilenameOnly(core, out var filename))
        {
            return match.Value;
        }

        return filename + path[coreLength..];
    }

    private static bool TryGetFilenameOnly(string value, out string filename)
    {
        filename = string.Empty;
        var candidate = value.Trim();
        if (candidate.Length == 0
            || Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            return false;
        }

        var withoutTrailingSeparators = candidate.TrimEnd('\\', '/');
        var separatorIndex = withoutTrailingSeparators.LastIndexOfAny(['\\', '/']);
        if (separatorIndex < 0 || separatorIndex == withoutTrailingSeparators.Length - 1)
        {
            return false;
        }

        var leaf = withoutTrailingSeparators[(separatorIndex + 1)..];
        var isAbsolute = candidate.StartsWith("/", StringComparison.Ordinal)
            || candidate.StartsWith("\\\\", StringComparison.Ordinal)
            || candidate.Length >= 3
            && char.IsAsciiLetter(candidate[0])
            && candidate[1] == ':'
            && candidate[2] is '\\' or '/';
        var extensionIndex = leaf.LastIndexOf('.');
        var looksLikeRelativeFile = extensionIndex > 0
            && extensionIndex < leaf.Length - 1
            && leaf.Length - extensionIndex - 1 <= 16;
        if (!isAbsolute && !looksLikeRelativeFile)
        {
            return false;
        }

        if (leaf is "." or "..")
        {
            return false;
        }

        filename = leaf;
        return true;
    }

    private static string LimitDisplayText(string value, int maximumCharacters) =>
        value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + "...";

    private static Regex CreatePathRegex(string pattern) =>
        new(
            pattern,
            RegexOptions.Compiled | RegexOptions.CultureInvariant,
            PathRegexTimeout);
}
