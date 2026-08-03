using Ali.Modules.Embeddings;
using Ali.Modules.RAG;
using Ali.Modules.Runtime;
using Ali.UI.ViewModels;

namespace Ali.Framework.Tests;

public sealed class ProviderNeutralRuntimeUiTests
{
    [Fact]
    public void SettingsUi_ExposesExplicitEmbeddingSelectionAndConnectivityTest()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));

        Assert.Contains("EmbeddingProviderChoices", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding EmbeddingProvider}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EmbeddingDimensions", xaml, StringComparison.Ordinal);
        Assert.Contains("EmbeddingContextTokens", xaml, StringComparison.Ordinal);
        Assert.Contains("EmbeddingDocumentPromptMode", xaml, StringComparison.Ordinal);
        Assert.Contains("EmbeddingQueryPromptMode", xaml, StringComparison.Ordinal);
        Assert.Contains("SemanticToolRetrievalEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("TestEmbeddingCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("Mem0, semantic tool retrieval, and local knowledge", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Mem0 uses GPT-OSS through Lemonade", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Lemonade creates embeddings", xaml, StringComparison.Ordinal);

        var viewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "LocalKnowledgeSettingsViewModel.cs"));
        Assert.Contains("EmbeddingEndpoint = EmbeddingEndpoint,", viewModel, StringComparison.Ordinal);
        Assert.Contains("EmbeddingModel = EmbeddingModel,", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddingEndpoint = EmbeddingEndpoint.Trim()", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("EmbeddingModel = EmbeddingModel.Trim()", viewModel, StringComparison.Ordinal);
        Assert.Contains("RequireSharedEmbeddingConfiguration(settings)", viewModel, StringComparison.Ordinal);
        Assert.Contains("ProbeConfiguredContextAsync(configuration)", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("RuntimeState = result.Success", viewModel, StringComparison.Ordinal);
        Assert.Contains("HandleEmbeddingError", viewModel, StringComparison.Ordinal);

        var embeddingRuntime = File.ReadAllText(FindRepositoryFile(
            "src", "Modules", "Embeddings", "LocalEmbeddingRuntime.cs"));
        Assert.Contains("search_document: ", embeddingRuntime, StringComparison.Ordinal);
        Assert.Contains("search_query: ", embeddingRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("nomic-embed-text", embeddingRuntime, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("127.0.0.1:1234", embeddingRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:11434", embeddingRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:8080", embeddingRuntime, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1:13305", embeddingRuntime, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedEmbeddingUi_RejectsAnEndpointMem0CannotUseExactly()
    {
        var settings = new LocalVectorLibrarySettings
        {
            EmbeddingProvider = LocalEmbeddingProviders.Custom,
            EmbeddingEndpoint = "http://127.0.0.1:1234/v1/Embeddings",
            EmbeddingModel = "exact-model",
            EmbeddingDimensions = 768
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalKnowledgeSettingsViewModel.RequireSharedEmbeddingConfiguration(settings));

        Assert.Contains("Mem0-compatible", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/embeddings", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalOnlyHttpClient_DisablesSystemProxyAndAutomaticRedirects()
    {
        using var handler = LocalOnlyHttpClientFactory.CreateHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);

        var desktop = File.ReadAllText(FindRepositoryFile("src", "AliServices.cs"));
        var headless = File.ReadAllText(FindRepositoryFile("src", "Modules", "Mcp", "HeadlessMcpToolRuntime.cs"));
        var main = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));
        Assert.Contains("LocalOnlyHttpClientFactory.Create(\"AliLocalDesktop/1.0\")", desktop, StringComparison.Ordinal);
        Assert.Contains("LocalOnlyHttpClientFactory.Create(\"AliMcpHost/1.0\")", headless, StringComparison.Ordinal);
        Assert.Contains("LocalOnlyHttpClientFactory.Create(", main, StringComparison.Ordinal);
        Assert.Contains("AliRuntimeInventory/1.0", main, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeUi_PersistsExplicitNativeToolCallSelection()
    {
        var xaml = File.ReadAllText(FindRepositoryFile("src", "UI", "SettingsWindow.xaml"));
        var viewModel = File.ReadAllText(FindRepositoryFile("src", "UI", "ViewModels", "MainWindowViewModel.cs"));

        Assert.Contains("RuntimeToolCallsEnabled", xaml, StringComparison.Ordinal);
        Assert.Contains("SupportsToolCalls: RuntimeToolCallsEnabled", viewModel, StringComparison.Ordinal);
        Assert.Contains("RuntimeToolCallsEnabled = options.SupportsToolCalls", viewModel, StringComparison.Ordinal);
        Assert.Contains("RuntimeToolCallsEnabled = choice.SupportsToolCalls", viewModel, StringComparison.Ordinal);
        Assert.Contains("provider-owned lifecycle", viewModel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("allowRemoteHttps: RuntimeAllowRemoteHttps", viewModel, StringComparison.Ordinal);
        Assert.Contains("RuntimeAllowRemoteHttps", xaml, StringComparison.Ordinal);
        Assert.Contains("RuntimeApiKey", xaml, StringComparison.Ordinal);
        Assert.Contains("Windows current-user protection", xaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Active runtime unloaded", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("Previous runtime released", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("release could not be verified", viewModel, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = Path.Combine([current.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            current = current.Parent;
        }
        throw new FileNotFoundException(Path.Combine(segments));
    }
}
