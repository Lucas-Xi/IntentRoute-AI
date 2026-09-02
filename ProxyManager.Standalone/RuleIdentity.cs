namespace ProxyManager.Standalone;

/// <summary>
/// 规则身份键：进程 + 规范化后的域名 / IP / 端口 / 协议 + 模式。
/// 同一进程可以有多条不同约束的规则；只有完整身份相同才视为重复。
/// 与 AI 草案去重、规则导入跳过使用同一把钥匙。
/// </summary>
public static class RuleIdentity
{
    private static readonly char[] Separators = [',', ';', '|', '\n', '\r', '\t', ' '];

    public static string CreateKey(ProxyRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return string.Join("\u001f",
            (rule.ExeName ?? string.Empty).Trim().ToLowerInvariant(),
            NormalizeList(rule.TargetHosts, lowerCase: true),
            NormalizeList(rule.TargetIPs, lowerCase: false),
            NormalizeList(rule.TargetPorts, lowerCase: false),
            (rule.Protocol ?? string.Empty).Trim(),
            rule.Mode.ToString());
    }

    public static string NormalizeList(string? raw, bool lowerCase)
    {
        var values = Split(raw)
            .Select(value => lowerCase ? value.ToLowerInvariant() : value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase);
        return string.Join(", ", values);
    }

    private static IEnumerable<string> Split(string? raw) =>
        (raw ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
