using AngleSharp.Html.Parser;
using Meziantou.Framework.Sanitizers;
using ReverseMarkdown;
using SmartReader;

namespace Ali.Modules.Internet.Extraction;

public sealed record WebContent(
    Uri Source,
    string Title,
    string Markdown,
    string PlainText,
    string? Author = null,
    string? SiteName = null,
    DateTimeOffset? PublishedAt = null,
    bool UsedReadability = false);

public interface IWebContentExtractor
{
    Task<WebContent> ExtractAsync(Uri source, string html, CancellationToken cancellationToken);
}

/// <summary>
/// Turns arbitrary web pages into bounded, safe, model-ready Markdown. Network access remains
/// outside this class so extraction can be tested deterministically and cannot fetch hidden URLs.
/// </summary>
public sealed class WebContentExtractor : IWebContentExtractor
{
    private const int MaximumInputCharacters = 4_000_000;
    private const int MaximumOutputCharacters = 120_000;
    private readonly HtmlSanitizer _sanitizer = new();
    private readonly Converter _markdownConverter = new();

    public async Task<WebContent> ExtractAsync(
        Uri source,
        string html,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(html);
        cancellationToken.ThrowIfCancellationRequested();

        var boundedHtml = html.Length <= MaximumInputCharacters
            ? html
            : html[..MaximumInputCharacters];
        var reader = new Reader(source.AbsoluteUri, boundedHtml)
        {
            ContinueIfNotReadable = true,
            PreCleanPage = true
        };
        var article = await reader.GetArticleAsync(cancellationToken).ConfigureAwait(false);

        var readableHtml = article.IsReadable && !string.IsNullOrWhiteSpace(article.Content)
            ? article.Content
            : await ExtractDocumentBodyAsync(boundedHtml, cancellationToken).ConfigureAwait(false);
        var safeHtml = _sanitizer.SanitizeHtmlFragment(readableHtml ?? string.Empty);
        var markdown = Bound(_markdownConverter.Convert(safeHtml));
        var plainText = Bound(article.IsReadable && !string.IsNullOrWhiteSpace(article.TextContent)
            ? article.TextContent
            : await ExtractPlainTextAsync(safeHtml, cancellationToken).ConfigureAwait(false));

        return new WebContent(
            source,
            FirstNonBlank(article.Title, source.Host),
            markdown,
            plainText,
            FirstNonBlank(article.Author, article.Byline, null),
            FirstNonBlank(article.SiteName, source.Host),
            ParsePublicationDate(article.PublicationDate),
            article.IsReadable);
    }

    private static async Task<string> ExtractDocumentBodyAsync(string html, CancellationToken cancellationToken)
    {
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        return document.Body?.InnerHtml ?? document.DocumentElement?.InnerHtml ?? string.Empty;
    }

    private static async Task<string> ExtractPlainTextAsync(string html, CancellationToken cancellationToken)
    {
        var document = await new HtmlParser().ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
        return document.Body?.TextContent ?? document.DocumentElement?.TextContent ?? string.Empty;
    }

    private static string Bound(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= MaximumOutputCharacters
            ? normalized
            : normalized[..MaximumOutputCharacters] + "\n\n[Content truncated by Ali.]";
    }

    private static string FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : second?.Trim() ?? string.Empty;

    private static string? FirstNonBlank(string? first, string? second, string? fallback) =>
        !string.IsNullOrWhiteSpace(first)
            ? first.Trim()
            : !string.IsNullOrWhiteSpace(second)
                ? second.Trim()
                : fallback;

    private static DateTimeOffset? ParsePublicationDate(object? value) =>
        value is not null && DateTimeOffset.TryParse(value.ToString(), out var parsed)
            ? parsed
            : null;
}
