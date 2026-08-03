using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ali.Modules.Runtime;

namespace Ali.Modules.EngineeringCertification;

/// <summary>
/// Discovers every model reported by enabled configured OpenAI-compatible runtimes. Candidate ids,
/// families, and provider behavior are never inferred from model names.
/// </summary>
internal sealed class OpenAiCertificationCandidateSource(HttpClient httpClient)
    : IEngineeringCertificationCandidateSource
{
    internal const int MaximumRuntimes = 32;
    internal const int MaximumCandidates = 64;
    internal const int MaximumModelIdCharacters = 512;
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<EngineeringCandidateDiscoveryResult> DiscoverAsync(
        IReadOnlyList<ConfiguredCertificationRuntime> runtimes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        if (runtimes.Count > MaximumRuntimes)
        {
            throw new InvalidDataException(
                $"Certification candidate discovery is bounded to {MaximumRuntimes} configured runtimes.");
        }

        var candidates = new List<EngineeringCertificationCandidate>();
        var issues = new List<EngineeringCandidateDiscoveryIssue>();
        foreach (var runtime in runtimes.Where(runtime => runtime.Enabled)
                     .OrderBy(runtime => runtime.RuntimeId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            runtime.Validate();
            var policy = LocalEndpointPolicy.Validate(
                runtime.Endpoint,
                runtime.AllowPrivateLanEndpoint,
                runtime.AllowRemoteHttpsEndpoint);
            if (!policy.IsAllowed)
            {
                issues.Add(new(runtime.RuntimeId, policy.Reason));
                continue;
            }

            try
            {
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    LocalRuntimeModelInventory.BuildModelsUri(runtime.Endpoint));
                if (!string.IsNullOrWhiteSpace(runtime.ApiKey))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", runtime.ApiKey);
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var body = await LocalRuntimeModelInventory.ReadBoundedBodyAsync(
                    response.Content,
                    cancellationToken).ConfigureAwait(false);
                var modelIds = ParseModelIds(body);
                if (modelIds.Count == 0)
                {
                    issues.Add(new(runtime.RuntimeId, "The configured runtime returned no model ids."));
                    continue;
                }

                foreach (var modelId in modelIds)
                {
                    if (candidates.Count >= MaximumCandidates)
                    {
                        issues.Add(new(
                            runtime.RuntimeId,
                            $"Candidate results were bounded at {MaximumCandidates} models."));
                        break;
                    }

                    candidates.Add(CreateCandidate(runtime, modelId));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
            {
                issues.Add(new(runtime.RuntimeId, Bound(ex.Message, 512)));
            }
        }

        var unique = candidates
            .GroupBy(candidate => candidate.BindingDigest, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.RuntimeId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.ModelId, StringComparer.Ordinal)
            .ToArray();
        return new EngineeringCandidateDiscoveryResult(unique, issues.ToArray());
    }

    internal static IReadOnlyList<string> ParseModelIds(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        AddModelIds(document.RootElement, ids);
        return ids.Order(StringComparer.Ordinal).ToArray();
    }

    private static void AddModelIds(JsonElement root, ISet<string> ids)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                AddModelId(item, ids);
            }
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in root.EnumerateObject())
        {
            if ((property.Name.Equals("data", StringComparison.OrdinalIgnoreCase)
                 || property.Name.Equals("models", StringComparison.OrdinalIgnoreCase)
                 || property.Name.Equals("all_models_loaded", StringComparison.OrdinalIgnoreCase))
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in property.Value.EnumerateArray())
                {
                    AddModelId(item, ids);
                }
            }
        }
    }

    private static void AddModelId(JsonElement item, ISet<string> ids)
    {
        if (item.ValueKind == JsonValueKind.String)
        {
            Add(item.GetString(), ids);
            return;
        }
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in item.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && (property.Name.Equals("id", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("model_name", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("name", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("key", StringComparison.OrdinalIgnoreCase)))
            {
                Add(property.Value.GetString(), ids);
                return;
            }
        }
    }

    private static void Add(string? value, ISet<string> ids)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > MaximumModelIdCharacters)
        {
            return;
        }
        ids.Add(normalized);
    }

    private static EngineeringCertificationCandidate CreateCandidate(
        ConfiguredCertificationRuntime runtime,
        string modelId)
    {
        var binding = string.Join("\n", runtime.RuntimeId, runtime.Endpoint.AbsoluteUri, modelId);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)))
            .ToLowerInvariant();
        return new EngineeringCertificationCandidate(
            $"candidate-{digest[..16]}",
            runtime.RuntimeId,
            runtime.Endpoint,
            modelId,
            digest);
    }

    private static string Bound(string value, int maximum) =>
        value.Length <= maximum ? value : value[..maximum];
}
