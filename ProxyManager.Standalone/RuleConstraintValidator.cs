using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ProxyManager.Standalone.Localization;

namespace ProxyManager.Standalone;

/// <summary>
/// 规则条件（域名 / IP / 端口）的共享格式校验。
/// 语义与 <see cref="SingBoxConfigBuilder"/> 构建期解析保持一致：exact 或 *.suffix 域名、
/// IPv4/IPv6 字面量或 CIDR、单端口或升序范围。供条件编辑器实时校验与 AI 草案校验复用。
/// </summary>
public static class RuleConstraintValidator
{
    private const int MaxHostListLength = 1_000;
    private const int MaxIpListLength = 1_000;
    private const int MaxPortListLength = 500;

    // 与 AiRuleDraftValidator / SingBoxConfigBuilder 的分隔符定义保持一致
    private static readonly char[] Separators = [',', ';', '|', '\n', '\r', '\t', ' '];

    /// <summary>exact 域名或 *.suffix（去掉前缀后按 DNS 域名校验）；null/空视为通过。</summary>
    public static bool IsValidHostList(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return true;
        if (raw.Length > MaxHostListLength) return false;
        foreach (var original in Split(raw))
        {
            var host = original;
            if (host.StartsWith("*.", StringComparison.Ordinal)) host = host[2..];
            if (host.Length is < 1 or > 253 || host.Contains("..", StringComparison.Ordinal) ||
                Uri.CheckHostName(host) != UriHostNameType.Dns)
                return false;

            var labels = host.Split('.');
            if (labels.Length < 2 || labels.Any(label => label.Length is < 1 or > 63 ||
                label.StartsWith('-') || label.EndsWith('-') ||
                label.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-'))))
                return false;
        }
        return true;
    }

    /// <summary>每项须为 IPv4/IPv6 字面量或 CIDR（前缀纯数字且在地址族范围内）；null/空视为通过。</summary>
    public static bool IsValidIpList(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return true;
        if (raw.Length > MaxIpListLength) return false;
        foreach (var token in Split(raw))
        {
            var slash = token.IndexOf('/');
            if (slash < 0)
            {
                if (!IPAddress.TryParse(token, out _)) return false;
                continue;
            }

            var addressText = token[..slash];
            var prefixText = token[(slash + 1)..];
            if (!IPAddress.TryParse(addressText, out var address)) return false;
            if (prefixText.Length == 0 || !prefixText.All(char.IsAsciiDigit)) return false;
            if (!int.TryParse(prefixText, NumberStyles.None, CultureInfo.InvariantCulture, out var prefix)) return false;
            var maxPrefix = address.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32;
            if (prefix > maxPrefix) return false;
        }
        return true;
    }

    /// <summary>每项须为单端口 1-65535 或 a-b / a:b 且 a&lt;=b；null/空视为通过。</summary>
    public static bool IsValidPortList(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return true;
        if (raw.Length > MaxPortListLength) return false;
        foreach (var token in Split(raw))
        {
            var separator = token.Contains('-') ? '-' : token.Contains(':') ? ':' : '\0';
            if (separator == '\0')
            {
                if (!TryParsePort(token, out _)) return false;
                continue;
            }

            var parts = token.Split(separator, 2);
            if (parts.Length != 2 || !TryParsePort(parts[0], out var start) ||
                !TryParsePort(parts[1], out var end) || start > end)
                return false;
        }
        return true;
    }

    /// <summary>返回本地化错误文案列表（空列表 = 全部通过），用于编辑器错误区展示。</summary>
    public static IReadOnlyList<string> Explain(string? hosts, string? ips, string? ports)
    {
        var errors = new List<string>();
        if (!IsValidHostList(hosts)) errors.Add(Strings.RuleEditBadHosts);
        if (!IsValidIpList(ips)) errors.Add(Strings.RuleEditBadIps);
        if (!IsValidPortList(ports)) errors.Add(Strings.RuleEditBadPorts);
        return errors;
    }

    private static IEnumerable<string> Split(string? raw) =>
        (raw ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        return int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port)
               && port is >= 1 and <= 65535;
    }
}
