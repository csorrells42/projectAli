using Ali.Modules.Coordinator;
using System.Text;
using System.Text.RegularExpressions;

namespace Ali.UI.ViewModels;

public sealed class AgentActivityItemViewModel
{
    private const string TechnicalPayloadOmission = "Technical payload omitted from the human activity view.";
    private const int MaximumDisplayCharacters = 320;
    private const int MaximumPathFormattingInputCharacters = 2_048;
    private static readonly TimeSpan PathRegexTimeout = TimeSpan.FromMilliseconds(25);
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

        var boundedInput = value.Length <= MaximumPathFormattingInputCharacters
            ? value
            : value[..MaximumPathFormattingInputCharacters];
        var normalized = string.Join(
            " ",
            boundedInput.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
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

        var toolName = string.IsNullOrWhiteSpace(receipt.DisplayName)
            ? HumanizeIdentifier(receipt.ToolName)
            : NormalizeHumanDisplayText(receipt.DisplayName, 160);
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
            return ShortenFilePathsDeterministically(value);
        }
    }

    internal static string ShortenFilePathsDeterministically(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var boundedValue = value.Length <= MaximumPathFormattingInputCharacters
            ? value
            : value[..MaximumPathFormattingInputCharacters];
        StringBuilder? shortened = null;
        var copyStart = 0;
        var index = 0;
        while (index < boundedValue.Length)
        {
            if (IsFallbackBoundary(boundedValue, index)
                && IsHttpUrlStart(boundedValue, index))
            {
                index = FindFallbackCandidateEnd(boundedValue, index, isUrl: true);
                continue;
            }

            var character = boundedValue[index];
            if (IsPathQuote(character)
                && IsFallbackBoundary(boundedValue, index))
            {
                var quoteEnd = boundedValue.IndexOf(character, index + 1);
                if (quoteEnd >= 0)
                {
                    var candidateStart = index + 1;
                    var candidate = boundedValue[candidateStart..quoteEnd];
                    if (TryGetFallbackReplacement(candidate, out var replacement))
                    {
                        shortened ??= new StringBuilder(boundedValue.Length);
                        shortened.Append(boundedValue.AsSpan(copyStart, candidateStart - copyStart));
                        shortened.Append(replacement);
                        copyStart = quoteEnd;
                    }

                    index = quoteEnd + 1;
                    continue;
                }

                // Preserve an unmatched opening quote and scan its bounded remainder normally.
                index++;
                continue;
            }

            if (!IsFallbackBoundary(boundedValue, index)
                || !IsPotentialFallbackPathStart(character))
            {
                index++;
                continue;
            }

            var candidateEnd = FindFallbackCandidateEnd(boundedValue, index, isUrl: false);
            if (candidateEnd <= index)
            {
                index++;
                continue;
            }

            var pathCandidate = boundedValue[index..candidateEnd];
            if (TryGetFallbackReplacement(pathCandidate, out var pathReplacement))
            {
                shortened ??= new StringBuilder(boundedValue.Length);
                shortened.Append(boundedValue.AsSpan(copyStart, index - copyStart));
                shortened.Append(pathReplacement);
                copyStart = candidateEnd;
            }

            index = candidateEnd;
        }

        if (shortened is null)
        {
            return boundedValue;
        }

        shortened.Append(boundedValue.AsSpan(copyStart));
        return shortened.ToString();
    }

    private static bool TryGetFallbackReplacement(string candidate, out string replacement)
    {
        replacement = string.Empty;
        var coreLength = candidate.Length;
        while (coreLength > 0 && TrailingPathPunctuation.Contains(candidate[coreLength - 1]))
        {
            coreLength--;
        }

        if (!TryGetFilenameOnly(candidate[..coreLength], out var filename))
        {
            return false;
        }

        replacement = filename + candidate[coreLength..];
        return true;
    }

    private static int FindFallbackCandidateEnd(string value, int start, bool isUrl)
    {
        var isDrivePath = !isUrl
            && start + 2 < value.Length
            && char.IsAsciiLetter(value[start])
            && value[start + 1] == ':'
            && value[start + 2] is '\\' or '/';
        var isUncPath = !isUrl
            && start + 1 < value.Length
            && value[start] == '\\'
            && value[start + 1] == '\\';
        var index = start;
        while (index < value.Length)
        {
            var character = value[index];
            if (char.IsWhiteSpace(character))
            {
                if (!isUrl
                    && (isDrivePath || isUncPath)
                    && !LooksLikeFilePathEndingAt(value, start, index)
                    && !StartsWithFallbackConnector(value, index))
                {
                    index++;
                    continue;
                }

                break;
            }

            if (IsPathQuote(character)
                || (!isUrl && IsFallbackHardDelimiter(character))
                || (!isUrl && character == ':' && !(isDrivePath && index == start + 1)))
            {
                break;
            }

            index++;
        }

        return index;
    }

    private static bool LooksLikeFilePathEndingAt(string value, int start, int end)
    {
        var extensionLength = 0;
        var index = end - 1;
        while (index >= start
            && extensionLength < 16
            && char.IsAsciiLetterOrDigit(value[index]))
        {
            extensionLength++;
            index--;
        }

        return extensionLength > 0
            && index > start
            && value[index] == '.'
            && value[index - 1] is not '\\' and not '/';
    }

    private static bool StartsWithFallbackConnector(string value, int index) =>
        value.AsSpan(index).StartsWith(" to ", StringComparison.OrdinalIgnoreCase)
        || value.AsSpan(index).StartsWith(" from ", StringComparison.OrdinalIgnoreCase)
        || value.AsSpan(index).StartsWith(" and ", StringComparison.OrdinalIgnoreCase);

    private static bool IsHttpUrlStart(string value, int index) =>
        value.AsSpan(index).StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.AsSpan(index).StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static bool IsFallbackBoundary(string value, int index) =>
        index == 0
        || char.IsWhiteSpace(value[index - 1])
        || value[index - 1] is '"' or '\'' or '`' or '(' or '[' or '{' or '<'
            or '=' or ':' or ',' or ';' or '!' or '?';

    private static bool IsPotentialFallbackPathStart(char character) =>
        char.IsLetterOrDigit(character)
        || character is '/' or '\\' or '.' or '_';

    private static bool IsPathQuote(char character) => character is '"' or '\'' or '`';

    private static bool IsFallbackHardDelimiter(char character) =>
        character is ',' or ';' or '!' or '?' or ')' or ']' or '}' or '<' or '>' or '=' or '|';

    private static string ShortenFilePathsSegment(string value)
    {
        var shortened = ShortenSingleQuotedAbsolutePaths(value);
        shortened = QuotedPathPattern.Replace(shortened, ReplaceQuotedPath);
        shortened = WindowsAbsoluteFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = UnixAbsoluteFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = WindowsAbsoluteDirectoryPathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = WindowsAbsolutePathPattern.Replace(shortened, ReplaceUnquotedPath);
        shortened = UnixAbsolutePathPattern.Replace(shortened, ReplaceUnquotedPath);
        return RelativeFilePathPattern.Replace(shortened, ReplaceUnquotedPath);
    }

    private static string ShortenSingleQuotedAbsolutePaths(string value)
    {
        StringBuilder? shortened = null;
        var copyStart = 0;
        var quoteStart = value.IndexOf('\'');
        while (quoteStart >= 0)
        {
            var quoteEnd = value.IndexOf('\'', quoteStart + 1);
            if (quoteEnd < 0)
            {
                break;
            }

            var candidate = value[(quoteStart + 1)..quoteEnd];
            if (IsAbsoluteFileSystemPath(candidate)
                && TryGetFilenameOnly(candidate, out var filename))
            {
                shortened ??= new StringBuilder(value.Length);
                shortened.Append(value[copyStart..(quoteStart + 1)]);
                shortened.Append(filename);
                copyStart = quoteEnd;
                quoteStart = value.IndexOf('\'', quoteEnd + 1);
                continue;
            }

            quoteStart = value.IndexOf('\'', quoteStart + 1);
        }

        if (shortened is null)
        {
            return value;
        }

        shortened.Append(value[copyStart..]);
        return shortened.ToString();
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
        var isAbsolute = IsAbsoluteFileSystemPath(candidate);
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

    private static bool IsAbsoluteFileSystemPath(string candidate) =>
        candidate.StartsWith("/", StringComparison.Ordinal)
        || candidate.StartsWith("\\\\", StringComparison.Ordinal)
        || candidate.Length >= 3
        && char.IsAsciiLetter(candidate[0])
        && candidate[1] == ':'
        && candidate[2] is '\\' or '/';

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
