namespace Ali.Modules.Runtime;

internal static class LocalOnlyHttpClientFactory
{
    internal static HttpClient Create(string userAgent, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        var client = new HttpClient(CreateHandler(), disposeHandler: true);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        if (timeout.HasValue)
        {
            client.Timeout = timeout.Value;
        }

        return client;
    }

    internal static HttpClientHandler CreateHandler() =>
        new()
        {
            UseProxy = false,
            AllowAutoRedirect = false
        };
}
