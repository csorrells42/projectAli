using System.ComponentModel;
using Ali.Modules.Coordinator;

namespace Ali.Modules.Internet;

public sealed record CoordinatorNavigationLinkResult(
    bool Success,
    string Status,
    string Url,
    string Origin,
    string Destination,
    IReadOnlyList<string> Waypoints,
    string TravelMode,
    string EvidenceBoundary);

/// <summary>
/// Builds a Google Maps handoff without pretending that Ali calculated or verified a route.
/// Google Maps resolves the live places, road network, traffic, steps, distance, and ETA only
/// when the user opens the generated URL.
/// </summary>
internal sealed class AliNavigationTools
{
    private const int MaximumWaypoints = 3;
    private readonly Func<CoordinatorTurnContext?> turnAccessor;
    private readonly Func<WebSourceBackendSettings> settingsProvider;
    private static readonly HashSet<string> SupportedTravelModes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "driving",
            "walking",
            "bicycling",
            "transit"
        };

    public AliNavigationTools(Func<CoordinatorTurnContext?> turnAccessor)
        : this(turnAccessor, static () => new WebSourceBackendSettings())
    {
    }

    public AliNavigationTools(
        Func<CoordinatorTurnContext?> turnAccessor,
        Func<WebSourceBackendSettings> settingsProvider)
    {
        ArgumentNullException.ThrowIfNull(turnAccessor);
        ArgumentNullException.ThrowIfNull(settingsProvider);
        this.turnAccessor = turnAccessor;
        this.settingsProvider = settingsProvider;
    }

    public CoordinatorNavigationLinkResult CreateGoogleMapsDirectionsLink(
        [Description("Explicit route origin as an address, place name, or Google place query. Never invent an address.")]
        string origin,
        [Description("Explicit final destination as an address, place name, or Google place query. Use the origin again for a round trip.")]
        string destination,
        [Description("Optional ordered stops. Use place queries such as 'Publix near Stuart, Florida' when an exact verified address is unavailable.")]
        string[]? waypoints,
        [Description("Travel mode: driving, walking, bicycling, or transit. Defaults to driving.")]
        string? travelMode)
    {
        var normalizedOrigin = RequireLocation(origin, nameof(origin));
        var normalizedDestination = RequireLocation(destination, nameof(destination));
        var normalizedWaypoints = (waypoints ?? [])
            .Select(value => RequireLocation(value, nameof(waypoints)))
            .ToArray();
        if (normalizedWaypoints.Length > MaximumWaypoints)
        {
            throw new ArgumentException(
                $"Google Maps links support at most {MaximumWaypoints} ordered waypoints in Ali's portable handoff.",
                nameof(waypoints));
        }

        var normalizedMode = string.IsNullOrWhiteSpace(travelMode)
            ? "driving"
            : travelMode.Trim().ToLowerInvariant();
        if (!SupportedTravelModes.Contains(normalizedMode))
        {
            throw new ArgumentException(
                $"Unsupported travel mode '{travelMode}'. Use driving, walking, bicycling, or transit.",
                nameof(travelMode));
        }

        var query = new List<string>
        {
            "api=1",
            "origin=" + Uri.EscapeDataString(normalizedOrigin),
            "destination=" + Uri.EscapeDataString(normalizedDestination),
            "travelmode=" + Uri.EscapeDataString(normalizedMode)
        };
        if (normalizedWaypoints.Length > 0)
        {
            query.Add("waypoints=" + Uri.EscapeDataString(string.Join('|', normalizedWaypoints)));
        }

        var navigationBase = RequireConfiguredNavigationBase(
            settingsProvider()?.GoogleMapsDirectionsBaseUrl);
        var url = navigationBase.AbsoluteUri.TrimEnd('?') + "?" + string.Join('&', query);
        if (turnAccessor() is { } turn)
        {
            turn.UsedEvidenceTool = true;
            turn.UsedNavigationTool = true;
        }

        return new CoordinatorNavigationLinkResult(
            true,
            "Created a Google Maps directions link from the supplied place queries. Google Maps will resolve live places and calculate the route when the user opens it.",
            url,
            normalizedOrigin,
            normalizedDestination,
            normalizedWaypoints,
            normalizedMode,
            "This tool constructs a handoff URL only. It does not verify business addresses or nearest-place ranking and does not return turn-by-turn steps, mileage, traffic, or travel time. Never invent those details.");
    }

    private static string RequireLocation(string? value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
        {
            throw new ArgumentException("A non-empty place or address is required.", parameterName);
        }

        if (normalized.Length > 300)
        {
            throw new ArgumentException("A place or address cannot exceed 300 characters.", parameterName);
        }

        return normalized;
    }

    private static Uri RequireConfiguredNavigationBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var endpoint)
            || !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new InvalidOperationException(
                "Internet setting 'googleMapsDirectionsBaseUrl' must be an absolute HTTPS URL without a query or fragment; no navigation link was created.");
        }

        return endpoint;
    }
}
