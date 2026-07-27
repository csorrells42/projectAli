using Microsoft.Extensions.DependencyInjection;

namespace Ali.Modules.Internet;

/// <summary>
/// Creates web-only clients with Microsoft's standard timeout, retry, rate-limit, and circuit-breaker pipeline.
/// Model generation and local embedding traffic deliberately use a separate non-retrying client.
/// </summary>
public static class InternetHttpClientFactory
{
    private const string ClientName = "Ali.Internet";
    private static readonly Lazy<ServiceProvider> Services = new(CreateServices);

    public static HttpClient CreateClient() =>
        Services.Value.GetRequiredService<IHttpClientFactory>().CreateClient(ClientName);

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services
            .AddHttpClient(ClientName, client =>
            {
                // Each provider request has its own bounded cancellation token. The standard
                // resilience pipeline owns attempt and total timeouts.
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AliLocalDesktop/1.0");
            })
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.MaxRetryAttempts = 2;
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(35);
                // Microsoft validates that circuit-breaker sampling spans at least
                // two complete attempts. Keep the longer web-navigation attempt
                // timeout while giving the breaker enough evidence to act on.
                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(70);
            });
        return services.BuildServiceProvider();
    }
}
