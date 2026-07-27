using Ali.Modules.Internet;

namespace Ali.Framework.Tests;

public sealed class InternetHttpClientFactoryTests
{
    [Fact]
    public void CreateClient_HasAValidResiliencePipeline()
    {
        using var client = InternetHttpClientFactory.CreateClient();

        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
        Assert.Contains(
            client.DefaultRequestHeaders.UserAgent,
            value => value.Product?.Name == "AliLocalDesktop");
    }
}
