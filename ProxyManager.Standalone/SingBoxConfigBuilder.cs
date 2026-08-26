using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ProxyManager.Standalone;

/// <summary>
/// Builds a sing-box 1.13+ JSON configuration from <see cref="AppConfig"/>.
/// Passwords are written only into the internal config payload used for the runtime file;
/// public surfaces expose redacted JSON and never echo secrets in errors.
/// </summary>
public static class SingBoxConfigBuilder
{
    public const string DirectTag = "direct";
    public const string TunInboundTag = "tun-in";
    public const string TunIpv4Address = "172.19.0.1/30";
    public const string TunIpv6Address = "fdfe:dcba:9876::1/126";

    private static readonly char[] ListSeparators = [',', ';', '|', '\n', '\r', '\t', ' '];

    public static SingBoxConfigBuildResult Build(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var enabledServers = (config.ProxyServers ?? [])
                .Where(s => s != null && s.Enabled)
                .ToList();

            var outboundTagsByServerId = new Dictionary<string, string>(StringComparer.Ordinal);
            var outbounds = new JArray
            {
                new JObject
                {
                    ["type"] = "direct",
                    ["tag"] = DirectTag
                }
            };

            string? defaultProxyTag = null;
            for (var index = 0; index < enabledServers.Count; index++)
            {
                var server = enabledServers[index];
                var normalizedHost = ValidateServer(server);
                var tag = MakeOutboundTag(server, index);
                if (!string.IsNullOrEmpty(server.Id))
                    outboundTagsByServerId[server.Id] = tag;
                outbounds.Add(BuildProxyOutbound(server, tag, normalizedHost));
                defaultProxyTag ??= tag;
            }

            if (config.GlobalMode == GlobalMode.ProxyAll && defaultProxyTag == null)
                return SingBoxConfigBuildResult.Fail("GlobalMode is ProxyAll but no enabled proxy server is available.");

            var routeRules = new JArray();
            var orderedRules = (config.Rules ?? [])
                .Where(r => r != null && r.IsEnabled)
                .OrderBy(r => r.Priority)
                .ThenBy(r => r.CreatedAt, StringComparer.Ordinal)
                .ToList();

            foreach (var rule in orderedRules)
            {
                var built = TryBuildRouteRule(rule, outboundTagsByServerId, defaultProxyTag);
                if (!built.Success)
                    return SingBoxConfigBuildResult.Fail(built.Error!);
                if (built.Rule != null)
                    routeRules.Add(built.Rule);
            }

            var finalOutbound = config.GlobalMode == GlobalMode.ProxyAll
                ? defaultProxyTag!
                : DirectTag;

            var root = new JObject
            {
                ["log"] = new JObject
                {
                    ["level"] = "info",
                    ["timestamp"] = true
                },
                ["inbounds"] = new JArray
                {
                    new JObject
                    {
                        ["type"] = "tun",
                        ["tag"] = TunInboundTag,
                        ["address"] = new JArray(TunIpv4Address, TunIpv6Address),
                        ["auto_route"] = true,
                        ["strict_route"] = true,
                        ["stack"] = "system"
                    }
                },
                ["outbounds"] = outbounds,
                ["route"] = new JObject
                {
                    ["auto_detect_interface"] = true,
                    ["rules"] = routeRules,
                    ["final"] = finalOutbound
                }
            };

            var fullJson = root.ToString(Formatting.Indented);
            var redactedJson = RedactSecrets(root).ToString(Formatting.Indented);
            return SingBoxConfigBuildResult.Ok(fullJson, redactedJson);
        }
        catch (SingBoxConfigException ex)
        {
            return SingBoxConfigBuildResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            return SingBoxConfigBuildResult.Fail("Failed to build sing-box config: " + SanitizeError(ex.Message));
        }
    }

    private static JObject BuildProxyOutbound(ProxyServer server, string tag, string normalizedHost)
    {
        var password = ResolvePassword(server.Password);
        JObject outbound = server.ProxyType switch
        {
            ProxyType.Socks5 => new JObject
            {
                ["type"] = "socks",
                ["tag"] = tag,
                ["server"] = normalizedHost,
                ["server_port"] = server.Port,
                ["version"] = "5"
            },
            ProxyType.Http => new JObject
            {
                ["type"] = "http",
                ["tag"] = tag,
                ["server"] = normalizedHost,
                ["server_port"] = server.Port
            },
            ProxyType.Https => new JObject
            {
                ["type"] = "http",
                ["tag"] = tag,
                ["server"] = normalizedHost,
                ["server_port"] = server.Port,
                ["tls"] = new JObject
                {
                    ["enabled"] = true
                }
            },
            _ => throw new SingBoxConfigException($"Unsupported proxy type for server '{SafeName(server)}'.")
        };

        if (!string.IsNullOrEmpty(server.Username))
            outbound["username"] = server.Username;
        if (!string.IsNullOrEmpty(password))
            outbound["password"] = password;

        return outbound;
    }

    private static RouteRuleBuild TryBuildRouteRule(
        ProxyRule rule,
        IReadOnlyDictionary<string, string> outboundTagsByServerId,
        string? defaultProxyTag)
    {
        if (string.IsNullOrWhiteSpace(rule.ExeName))
            return RouteRuleBuild.Fail("Rule executable name is required; use '*' explicitly for a global rule.");

        if (!string.IsNullOrWhiteSpace(rule.ProxyChainId))
            return RouteRuleBuild.Fail(
                $"Rule '{SafeRuleName(rule)}' references proxy chain '{rule.ProxyChainId}', which is not supported.");

        if (HasNontrivialExeWildcard(rule.ExeName))
            return RouteRuleBuild.Fail(
                $"Rule '{SafeRuleName(rule)}' uses unsupported executable wildcard '{rule.ExeName}'. Only exact process names are allowed.");

        var route = new JObject();

        if (!string.IsNullOrWhiteSpace(rule.ExeName) && rule.ExeName.Trim() != "*")
            route["process_name"] = new JArray(rule.ExeName.Trim());

        var hosts = ParseHosts(rule.TargetHosts, rule);
        if (!hosts.Success) return RouteRuleBuild.Fail(hosts.Error!);
        if (hosts.Exact.Count > 0) route["domain"] = new JArray(hosts.Exact);
        if (hosts.Suffixes.Count > 0) route["domain_suffix"] = new JArray(hosts.Suffixes);

        var ips = ParseIpCidrs(rule.TargetIPs, rule);
        if (!ips.Success) return RouteRuleBuild.Fail(ips.Error!);
        if (ips.Values.Count > 0) route["ip_cidr"] = new JArray(ips.Values);

        var ports = ParsePorts(rule.TargetPorts, rule);
        if (!ports.Success) return RouteRuleBuild.Fail(ports.Error!);
        if (ports.Ports.Count > 0) route["port"] = new JArray(ports.Ports.Select(p => (JToken)p));
        if (ports.Ranges.Count > 0) route["port_range"] = new JArray(ports.Ranges);

        var network = ParseNetwork(rule.Protocol, rule);
        if (!network.Success) return RouteRuleBuild.Fail(network.Error!);
        if (network.Networks.Count == 1)
            route["network"] = network.Networks[0];
        else if (network.Networks.Count > 1)
            route["network"] = new JArray(network.Networks);

        switch (rule.Mode)
        {
            case ProxyMode.Direct:
                route["action"] = "route";
                route["outbound"] = DirectTag;
                break;
            case ProxyMode.Block:
                route["action"] = "reject";
                break;
            case ProxyMode.Proxy:
                {
                    string? outboundTag = null;
                    if (!string.IsNullOrWhiteSpace(rule.ProxyId))
                    {
                        if (!outboundTagsByServerId.TryGetValue(rule.ProxyId.Trim(), out outboundTag))
                            return RouteRuleBuild.Fail(
                                $"Rule '{SafeRuleName(rule)}' references missing or disabled proxy server '{rule.ProxyId}'.");
                    }
                    else
                    {
                        outboundTag = defaultProxyTag;
                        if (outboundTag == null)
                            return RouteRuleBuild.Fail(
                                $"Rule '{SafeRuleName(rule)}' requires a proxy but no enabled proxy server is available.");
                    }

                    route["action"] = "route";
                    route["outbound"] = outboundTag;
                    break;
                }
            default:
                return RouteRuleBuild.Fail($"Rule '{SafeRuleName(rule)}' has unsupported mode.");
        }

        return RouteRuleBuild.Ok(route);
    }

    private static bool HasNontrivialExeWildcard(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || exeName.Trim() == "*") return false;
        // Exact process names only; any *, ?, or regex-like metacharacters are rejected.
        return exeName.IndexOfAny(['*', '?', '[', ']']) >= 0;
    }

    private static HostParseResult ParseHosts(string? raw, ProxyRule rule)
    {
        var exact = new List<string>();
        var suffixes = new List<string>();
        foreach (var token in SplitList(raw))
        {
            if (token.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = token[2..].Trim().TrimStart('.');
                if (string.IsNullOrEmpty(suffix) || suffix.IndexOfAny(['*', '?']) >= 0)
                    return HostParseResult.Fail(
                        $"Rule '{SafeRuleName(rule)}' has unsupported host pattern '{token}'. Use exact hosts or *.suffix.");
                suffixes.Add(suffix);
            }
            else if (token.IndexOfAny(['*', '?']) >= 0)
            {
                return HostParseResult.Fail(
                    $"Rule '{SafeRuleName(rule)}' has unsupported host pattern '{token}'. Use exact hosts or *.suffix.");
            }
            else
            {
                exact.Add(token.Trim().TrimEnd('.'));
            }
        }

        return HostParseResult.Ok(exact, suffixes);
    }

    private static IpParseResult ParseIpCidrs(string? raw, ProxyRule rule)
    {
        var values = new List<string>();
        foreach (var token in SplitList(raw))
        {
            if (token.Contains('/'))
            {
                var parts = token.Split('/', 2);
                if (!IPAddress.TryParse(parts[0], out var parsedIp) ||
                    !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix) ||
                    prefix < 0 ||
                    (parsedIp.AddressFamily == AddressFamily.InterNetwork && prefix > 32) ||
                    (parsedIp.AddressFamily == AddressFamily.InterNetworkV6 && prefix > 128))
                {
                    return IpParseResult.Fail($"Rule '{SafeRuleName(rule)}' has invalid IP/CIDR '{token}'.");
                }

                values.Add(token);
            }
            else if (IPAddress.TryParse(token, out var ip))
            {
                values.Add(ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"{token}/128" : $"{token}/32");
            }
            else
            {
                return IpParseResult.Fail($"Rule '{SafeRuleName(rule)}' has invalid IP/CIDR '{token}'.");
            }
        }

        return IpParseResult.Ok(values);
    }

    private static PortParseResult ParsePorts(string? raw, ProxyRule rule)
    {
        var ports = new List<int>();
        var ranges = new List<string>();
        foreach (var token in SplitList(raw))
        {
            if (token.Contains('-') || token.Contains(':'))
            {
                var sep = token.Contains('-') ? '-' : ':';
                var parts = token.Split(sep, 2);
                if (!TryParsePort(parts[0], out var start) || !TryParsePort(parts[1], out var end) || start > end)
                    return PortParseResult.Fail($"Rule '{SafeRuleName(rule)}' has invalid port range '{token}'.");
                ranges.Add($"{start}:{end}");
            }
            else
            {
                if (!TryParsePort(token, out var port))
                    return PortParseResult.Fail($"Rule '{SafeRuleName(rule)}' has invalid port '{token}'.");
                ports.Add(port);
            }
        }

        return PortParseResult.Ok(ports, ranges);
    }

    private static NetworkParseResult ParseNetwork(string? protocol, ProxyRule rule)
    {
        if (string.IsNullOrWhiteSpace(protocol) ||
            protocol.Equals("Both", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("Any", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("TCP/UDP", StringComparison.OrdinalIgnoreCase) ||
            protocol.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            return NetworkParseResult.Ok([]);
        }

        if (protocol.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            return NetworkParseResult.Ok(["tcp"]);
        if (protocol.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            return NetworkParseResult.Ok(["udp"]);

        return NetworkParseResult.Fail(
            $"Rule '{SafeRuleName(rule)}' has unsupported protocol '{protocol}'. Use TCP, UDP, or Both.");
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }

    private static IEnumerable<string> SplitList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) yield break;
        foreach (var part in raw.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
                yield return part.Trim();
        }
    }

    private static string ValidateServer(ProxyServer server)
    {
        if (!LocalProxyEndpoint.TryNormalize(server.Host, server.Port, out var normalizedHost, out var error))
            throw new SingBoxConfigException($"Proxy server '{SafeName(server)}' is invalid: {error}");
        return normalizedHost;
    }

    private static string MakeOutboundTag(ProxyServer server, int index)
    {
        var id = string.IsNullOrWhiteSpace(server.Id) ? $"server-{index + 1}" : server.Id;
        var safe = Regex.Replace(id, @"[^A-Za-z0-9_-]", "_");
        return "proxy-" + safe;
    }

    private static string ResolvePassword(string? plaintext)
    {
        // AppConfigStore decrypts values at the persistence boundary. In-memory
        // AppConfig instances always carry plaintext, including legitimate
        // passwords whose literal value begins with the on-disk "dpapi:" marker.
        return plaintext ?? string.Empty;
    }

    private static JObject RedactSecrets(JObject root)
    {
        var clone = (JObject)root.DeepClone();
        if (clone["outbounds"] is JArray outbounds)
        {
            foreach (var item in outbounds.OfType<JObject>())
            {
                if (item["password"] != null)
                    item["password"] = "***";
            }
        }

        return clone;
    }

    private static string SafeName(ProxyServer server) =>
        string.IsNullOrWhiteSpace(server.Name) ? (server.Id ?? "server") : server.Name;

    private static string SafeRuleName(ProxyRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.ExeName)) return rule.ExeName;
        if (!string.IsNullOrWhiteSpace(rule.Note)) return rule.Note;
        return rule.Id;
    }

    private static string SanitizeError(string message)
    {
        if (string.IsNullOrEmpty(message)) return "unknown error";
        // Strip anything that looks like a password assignment/value.
        message = Regex.Replace(message, @"(?i)(password\s*[=:]\s*)([^\s,;]+)", "$1***");
        message = Regex.Replace(message, @"(?i)(""password""\s*:\s*)""[^""]*""", "$1\"***\"");
        return message;
    }

    private sealed class SingBoxConfigException(string message) : Exception(message);

    private readonly struct RouteRuleBuild
    {
        public bool Success { get; init; }
        public JObject? Rule { get; init; }
        public string? Error { get; init; }
        public static RouteRuleBuild Ok(JObject rule) => new() { Success = true, Rule = rule };
        public static RouteRuleBuild Fail(string error) => new() { Success = false, Error = error };
    }

    private readonly struct HostParseResult
    {
        public bool Success { get; init; }
        public List<string> Exact { get; init; }
        public List<string> Suffixes { get; init; }
        public string? Error { get; init; }
        public static HostParseResult Ok(List<string> exact, List<string> suffixes) =>
            new() { Success = true, Exact = exact, Suffixes = suffixes };
        public static HostParseResult Fail(string error) =>
            new() { Success = false, Exact = [], Suffixes = [], Error = error };
    }

    private readonly struct IpParseResult
    {
        public bool Success { get; init; }
        public List<string> Values { get; init; }
        public string? Error { get; init; }
        public static IpParseResult Ok(List<string> values) => new() { Success = true, Values = values };
        public static IpParseResult Fail(string error) => new() { Success = false, Values = [], Error = error };
    }

    private readonly struct PortParseResult
    {
        public bool Success { get; init; }
        public List<int> Ports { get; init; }
        public List<string> Ranges { get; init; }
        public string? Error { get; init; }
        public static PortParseResult Ok(List<int> ports, List<string> ranges) =>
            new() { Success = true, Ports = ports, Ranges = ranges };
        public static PortParseResult Fail(string error) =>
            new() { Success = false, Ports = [], Ranges = [], Error = error };
    }

    private readonly struct NetworkParseResult
    {
        public bool Success { get; init; }
        public List<string> Networks { get; init; }
        public string? Error { get; init; }
        public static NetworkParseResult Ok(List<string> networks) => new() { Success = true, Networks = networks };
        public static NetworkParseResult Fail(string error) => new() { Success = false, Networks = [], Error = error };
    }
}

/// <summary>
/// Result of building a sing-box configuration. Full JSON with secrets is internal-only for <see cref="SingBoxRuntime"/>.
/// </summary>
public sealed class SingBoxConfigBuildResult
{
    private SingBoxConfigBuildResult(bool success, string? configJson, string? redactedJson, string? error)
    {
        Success = success;
        ConfigJson = configJson;
        RedactedJson = redactedJson;
        Error = error;
    }

    public bool Success { get; }

    /// <summary>Full config JSON including secrets. Intended for writing the managed config file only.</summary>
    internal string? ConfigJson { get; }

    /// <summary>Config JSON with passwords replaced by ***. Safe to display or log.</summary>
    public string? RedactedJson { get; }

    public string? Error { get; }

    public static SingBoxConfigBuildResult Ok(string configJson, string redactedJson) =>
        new(true, configJson, redactedJson, null);

    public static SingBoxConfigBuildResult Fail(string error) =>
        new(false, null, null, error);
}
