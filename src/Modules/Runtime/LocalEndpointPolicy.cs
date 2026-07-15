using System.Net;

namespace Ali.Modules.Runtime;

public static class LocalEndpointPolicy
{
    public static EndpointValidationResult Validate(Uri endpoint, bool allowPrivateLan)
    {
        if (!endpoint.IsAbsoluteUri)
        {
            return EndpointValidationResult.Deny("Runtime endpoint must be an absolute URI.");
        }

        if (endpoint.Scheme is not ("http" or "https"))
        {
            return EndpointValidationResult.Deny("Runtime endpoint must use HTTP or HTTPS.");
        }

        if (IsLoopbackHost(endpoint.Host))
        {
            return EndpointValidationResult.Allow("Loopback endpoint is allowed.");
        }

        if (!allowPrivateLan)
        {
            return EndpointValidationResult.Deny("Only loopback endpoints are allowed until private LAN pairing is implemented.");
        }

        if (IPAddress.TryParse(endpoint.Host, out var ipAddress) && IsPrivateAddress(ipAddress))
        {
            return EndpointValidationResult.Allow("Private LAN endpoint is allowed by settings.");
        }

        return EndpointValidationResult.Deny("Public or unresolved runtime endpoints are refused in local-only mode.");
    }

    private static bool IsLoopbackHost(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address));

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                   || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                   || (bytes[0] == 192 && bytes[1] == 168)
                   || (bytes[0] == 169 && bytes[1] == 254);
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC;
        }

        return false;
    }
}

public sealed record EndpointValidationResult(
    bool IsAllowed,
    string Reason)
{
    public static EndpointValidationResult Allow(string reason) => new(true, reason);

    public static EndpointValidationResult Deny(string reason) => new(false, reason);
}
