using System.Text;
using System.Text.Json;

namespace Ali.Modules.Runtime;

internal static class LocalRuntimeModelInventory
{
    internal const int MaximumResponseBytes = 1_048_576;

    internal static Uri BuildModelsUri(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var builder = new UriBuilder(endpoint)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith("/", StringComparison.Ordinal))
        {
            builder.Path += "/";
        }

        return new Uri(builder.Uri, "models");
    }

    internal static async Task<string> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw TooLarge();
        }

        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[8192];

        while (true)
        {
            var remaining = MaximumResponseBytes - output.Length;
            var bytesRequested = (int)Math.Min(buffer.Length, remaining + 1);
            var bytesRead = await input.ReadAsync(
                buffer.AsMemory(0, bytesRequested),
                cancellationToken).ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            if (bytesRead > remaining)
            {
                throw TooLarge();
            }

            output.Write(buffer, 0, bytesRead);
        }

        return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    internal static bool ListsExactModel(string json, string model)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root.EnumerateArray().Any(item => ModelEntryMatches(item, model));
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (IsInventoryCollection(property.Name)
                && property.Value.ValueKind == JsonValueKind.Array
                && property.Value.EnumerateArray().Any(item => ModelEntryMatches(item, model)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ModelEntryMatches(JsonElement element, string model)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return string.Equals(element.GetString(), model, StringComparison.OrdinalIgnoreCase);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (IsModelIdentityProperty(property.Name)
                && property.Value.ValueKind == JsonValueKind.String
                && string.Equals(property.Value.GetString(), model, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInventoryCollection(string name) =>
        name.Equals("data", StringComparison.OrdinalIgnoreCase)
        || name.Equals("models", StringComparison.OrdinalIgnoreCase)
        || name.Equals("all_models_loaded", StringComparison.OrdinalIgnoreCase);

    private static bool IsModelIdentityProperty(string name) =>
        name.Equals("id", StringComparison.OrdinalIgnoreCase)
        || name.Equals("model", StringComparison.OrdinalIgnoreCase)
        || name.Equals("model_name", StringComparison.OrdinalIgnoreCase)
        || name.Equals("name", StringComparison.OrdinalIgnoreCase)
        || name.Equals("key", StringComparison.OrdinalIgnoreCase);

    private static HttpRequestException TooLarge() =>
        new($"Local runtime model inventory response exceeded Ali's {MaximumResponseBytes / 1_048_576} MiB safety limit.");
}
