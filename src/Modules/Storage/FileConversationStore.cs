using System.Text.Json;
using System.Text.Json.Serialization;
using Ali.Modules.Conversation;
using Ali.Modules.Runtime;
using Ali.Modules.Voice;

namespace Ali.Modules.Storage;

public sealed class FileConversationStore : IConversationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _rootDirectory;
    private readonly string _indexPath;
    private readonly string _conversationsDirectory;

    public FileConversationStore(string localAliRoot)
    {
        _rootDirectory = Path.Combine(localAliRoot, "Conversations");
        _indexPath = Path.Combine(_rootDirectory, "conversations-index.json");
        _conversationsDirectory = Path.Combine(_rootDirectory, "conversations");
    }

    public string RootDirectory => _rootDirectory;

    public string IndexPath => _indexPath;

    public string ConversationsDirectory => _conversationsDirectory;

    public ConversationListResult ListSummaries()
    {
        EnsureDirectories();
        var warnings = new List<string>();

        var index = ReadIndex(warnings);
        if (index is null)
        {
            index = RebuildIndex(warnings);
            WriteIndex(index);
        }

        var conversations = index.Conversations
            .OrderByDescending(conversation => conversation.UpdatedAt)
            .ThenBy(conversation => conversation.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ConversationListResult(conversations, warnings);
    }

    public ConversationListResult Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return ListSummaries();
        }

        EnsureDirectories();
        var warnings = new List<string>();
        var needle = query.Trim();
        var matches = new List<StoredConversationSummary>();
        foreach (var summary in ListSummaries().Conversations)
        {
            if (Contains(summary.Title, needle) || Contains(summary.Preview, needle))
            {
                matches.Add(summary);
                continue;
            }

            var conversation = Load(summary.ConversationId);
            if (conversation is null)
            {
                warnings.Add($"Skipped unreadable conversation {summary.ConversationId} during search.");
                continue;
            }

            if (conversation.Messages.Any(message => Contains(message.Text, needle)))
            {
                matches.Add(summary);
            }
        }

        return new ConversationListResult(
            matches
                .OrderByDescending(conversation => conversation.UpdatedAt)
                .ThenBy(conversation => conversation.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            warnings);
    }

    public StoredConversation? Load(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return null;
        }

        var path = GetConversationPath(conversationId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return ReadJson<StoredConversation>(path);
        }
        catch (Exception ex) when (IsJsonOrIoException(ex))
        {
            return null;
        }
    }

    public StoredConversation Save(StoredConversation conversation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversation.ConversationId);
        EnsureDirectories();

        var normalized = NormalizeConversation(conversation);
        WriteJson(GetConversationPath(normalized.ConversationId), normalized);

        var index = ReadIndex(new List<string>()) ?? RebuildIndex(new List<string>());
        var summary = CreateSummary(normalized);
        var summaries = index.Conversations
            .Where(existing => !existing.ConversationId.Equals(normalized.ConversationId, StringComparison.OrdinalIgnoreCase))
            .Append(summary)
            .OrderByDescending(existing => existing.UpdatedAt)
            .ThenBy(existing => existing.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        WriteIndex(new ConversationIndex(summaries));
        return normalized;
    }

    public StoredConversationSummary? Rename(string conversationId, string title)
    {
        var conversation = Load(conversationId);
        if (conversation is null)
        {
            return null;
        }

        var renamed = conversation with
        {
            Title = NormalizeTitle(title)
        };

        Save(renamed);
        return CreateSummary(renamed);
    }

    public bool Delete(string conversationId)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return false;
        }

        EnsureDirectories();
        var path = GetConversationPath(conversationId);
        var existed = File.Exists(path);
        if (existed)
        {
            File.Delete(path);
        }

        var index = ReadIndex(new List<string>());
        if (index is not null)
        {
            var summaries = index.Conversations
                .Where(existing => !existing.ConversationId.Equals(conversationId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            WriteIndex(new ConversationIndex(summaries));
        }

        return existed;
    }

    public ConversationEraseResult EraseAll()
    {
        EnsureDirectories();
        var warnings = new List<string>();
        var deleted = 0;

        foreach (var filePath in Directory.EnumerateFiles(_conversationsDirectory, "*.json"))
        {
            try
            {
                File.Delete(filePath);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Could not delete {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        try
        {
            if (File.Exists(_indexPath))
            {
                File.Delete(_indexPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"Could not delete conversation index: {ex.Message}");
        }

        return new ConversationEraseResult(deleted, warnings);
    }

    private ConversationIndex? ReadIndex(List<string> warnings)
    {
        if (!File.Exists(_indexPath))
        {
            return null;
        }

        try
        {
            return ReadJson<ConversationIndex>(_indexPath);
        }
        catch (Exception ex) when (IsJsonOrIoException(ex))
        {
            warnings.Add($"Conversation index was unreadable and will be rebuilt: {ex.Message}");
            return null;
        }
    }

    private ConversationIndex RebuildIndex(List<string> warnings)
    {
        EnsureDirectories();
        var summaries = new List<StoredConversationSummary>();
        foreach (var filePath in Directory.EnumerateFiles(_conversationsDirectory, "*.json"))
        {
            try
            {
                var conversation = ReadJson<StoredConversation>(filePath);
                if (conversation is null)
                {
                    warnings.Add($"Skipped empty conversation file {Path.GetFileName(filePath)}.");
                    continue;
                }

                summaries.Add(CreateSummary(NormalizeConversation(conversation)));
            }
            catch (Exception ex) when (IsJsonOrIoException(ex))
            {
                warnings.Add($"Skipped corrupt conversation file {Path.GetFileName(filePath)}: {ex.Message}");
            }
        }

        return new ConversationIndex(
            summaries
                .OrderByDescending(summary => summary.UpdatedAt)
                .ThenBy(summary => summary.Title, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    private StoredConversation NormalizeConversation(StoredConversation conversation)
    {
        var messages = conversation.Messages
            .Select(message => message with
            {
                ConversationId = conversation.ConversationId,
                Text = message.Text ?? string.Empty,
                Attachments = SanitizeAttachments(message.Attachments)
            })
            .OrderBy(message => message.CreatedAt)
            .ToList();

        var createdAt = conversation.CreatedAt == default
            ? messages.FirstOrDefault()?.CreatedAt ?? DateTimeOffset.UtcNow
            : conversation.CreatedAt;
        var updatedAt = conversation.UpdatedAt == default
            ? messages.LastOrDefault()?.CreatedAt ?? createdAt
            : conversation.UpdatedAt;

        return conversation with
        {
            Title = NormalizeTitle(conversation.Title),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            Messages = messages,
            RetainsRawAudio = conversation.RetainsRawAudio || messages.Any(message => message.SourceVoiceMetadata?.RawAudioRetained == true),
            RetainsRawImageData = false
        };
    }

    private static IReadOnlyList<StoredAttachmentMetadata>? SanitizeAttachments(
        IReadOnlyList<StoredAttachmentMetadata>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return null;
        }

        return attachments
            .Select(attachment => attachment with
            {
                FileName = Path.GetFileName(attachment.FileName),
                ContentType = string.IsNullOrWhiteSpace(attachment.ContentType) ? "application/octet-stream" : attachment.ContentType
            })
            .ToList();
    }

    private static StoredConversationSummary CreateSummary(StoredConversation conversation)
    {
        var messages = conversation.Messages ?? Array.Empty<StoredChatMessage>();
        var preview = messages
            .Where(message => message.Role == ChatRole.User && !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => CollapsePreview(message.Text))
            .FirstOrDefault() ?? string.Empty;

        return new StoredConversationSummary(
            conversation.ConversationId,
            NormalizeTitle(conversation.Title),
            conversation.CreatedAt,
            conversation.UpdatedAt,
            messages.Count,
            messages.Any(message => message.Attachments?.Count > 0 || message.SourceAttachmentCount > 0),
            messages.Any(message => message.Origin == ChatMessageOrigin.Voice || message.SourceInputOrigin == VoiceInputOrigin.Voice),
            conversation.RetainsRawAudio,
            false,
            preview);
    }

    private static string CollapsePreview(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= 96 ? collapsed : $"{collapsed[..95].TrimEnd()}...";
    }

    private static string NormalizeTitle(string title) =>
        string.IsNullOrWhiteSpace(title) ? "Untitled chat" : title.Trim();

    private string GetConversationPath(string conversationId)
    {
        var safeFileName = Path.GetFileName(conversationId.Trim());
        return Path.Combine(_conversationsDirectory, $"{safeFileName}.json");
    }

    private void WriteIndex(ConversationIndex index) => WriteJson(_indexPath, index);

    private void EnsureDirectories()
    {
        Directory.CreateDirectory(_rootDirectory);
        Directory.CreateDirectory(_conversationsDirectory);
    }

    private static T? ReadJson<T>(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<T>(stream, JsonOptions);
    }

    private static void WriteJson<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(tempPath))
            {
                JsonSerializer.Serialize(stream, value, JsonOptions);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static bool Contains(string? haystack, string needle) =>
        !string.IsNullOrWhiteSpace(haystack)
        && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool IsJsonOrIoException(Exception ex) =>
        ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException;

    private sealed record ConversationIndex(IReadOnlyList<StoredConversationSummary> Conversations);
}
