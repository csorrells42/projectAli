namespace Ali.Modules.Runtime;

internal static class RemoteRuntimeHttpClientFactory
{
    internal static HttpClient Create(string userAgent, TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = true
            // The platform's ordinary certificate-chain and hostname validation remains active.
        };
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(2)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent);
        return client;
    }
}
