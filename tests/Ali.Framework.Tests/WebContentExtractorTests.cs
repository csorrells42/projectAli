using Ali.Modules.Internet.Extraction;

namespace Ali.Framework.Tests;

public sealed class WebContentExtractorTests
{
    [Fact]
    public async Task ExtractAsync_RemovesActiveContentAndPreservesUsefulStructure()
    {
        const string html = """
            <html><head><title>Useful page</title><script>alert('no')</script></head>
            <body><nav>Menu</nav><article><h1>Useful page</h1><p>A reliable paragraph with
            enough text to be useful to a reader and to the local model.</p>
            <table><tr><th>Item</th><th>Value</th></tr><tr><td>Speed</td><td>Fast</td></tr></table>
            <a href="https://example.com/source">Source</a></article></body></html>
            """;

        var result = await new WebContentExtractor().ExtractAsync(
            new Uri("https://example.com/article"),
            html,
            CancellationToken.None);

        Assert.Contains("Useful page", result.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", result.Markdown, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reliable paragraph", result.PlainText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Speed", result.Markdown, StringComparison.OrdinalIgnoreCase);
    }
}
