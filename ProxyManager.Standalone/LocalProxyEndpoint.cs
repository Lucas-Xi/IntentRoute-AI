using System.Net;

namespace ProxyManager.Standalone;

/// <summary>
/// Enforces IntentRoute AI's supported upstream boundary: a proxy listener must
/// already exist on a literal loopback IP address. Hostnames, LAN addresses,
/// and public endpoints are deliberately outside the v0.3 product contract.
/// </summary>
public static class LocalProxyEndpoint
{
    public static bool TryNormalize(string? host, int port, out string normalizedHost, out string error)
    {
        normalizedHost = string.Empty;
        error = string.Empty;

        if (port is < 1 or > 65535)
        {
            error = "Proxy port must be between 1 and 65535.";
            return false;
        }

        var candidate = host?.Trim();
        if (string.IsNullOrEmpty(candidate) || !IPAddress.TryParse(candidate, out var address))
        {
            error = "Proxy host must be a literal loopback IP address such as 127.0.0.1 or ::1.";
            return false;
        }

        if (address.IsIPv4MappedToIPv6 || !IPAddress.IsLoopback(address))
        {
            error = "Only a proxy listener on a literal loopback IP address is supported.";
            return false;
        }

        normalizedHost = address.ToString();
        return true;
    }

    public static string NormalizeOrThrow(string? host, int port)
    {
        if (TryNormalize(host, port, out var normalizedHost, out var error))
            return normalizedHost;

        throw new ArgumentException(error, nameof(host));
    }
}
