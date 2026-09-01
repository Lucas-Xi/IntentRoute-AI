using System.Text.RegularExpressions;

namespace ProxyManager.Standalone;

/// <summary>
/// 与 UI 的 RuntimeLogLine 解耦的导出快照，便于单元测试构造。
/// </summary>
public readonly record struct RuntimeLogLineSnapshot(string Time, string Message);

/// <summary>
/// Classifies and filters the redacted sing-box console lines surfaced by
/// <see cref="SingBoxRuntime"/>. Lines arrive in logrus console format: an
/// optional "yyyy-MM-dd HH:mm:ss " (or ISO "T") timestamp followed by an
/// uppercase level token such as INFO[0000] or PANIC.
/// </summary>
public static class RuntimeLogFilter
{
    // 允许可选前导时间戳（yyyy-MM-dd HH:mm:ss / yyyy-MM-ddTHH:mm:ss，可带小数秒）。
    private static readonly Regex TimestampPrefixPattern = new(
        @"^\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2}(?:\.\d+)?\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 时间戳之后行首的大写级别 token；(?![A-Za-z]) 避免 Information 之类误报。
    private static readonly Regex LeadingLevelPattern = new(
        @"^(TRACE|DEBUG|INFO|WARN|ERROR|FATAL|PANIC)(?![A-Za-z])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 识别行内第一个级别 token；未识别时返回 false 且级别按 Info 对待。
    /// </summary>
    public static bool TryParseLevel(string? line, out RuntimeLogLevel level)
    {
        level = RuntimeLogLevel.Info;
        if (string.IsNullOrEmpty(line))
            return false;

        var body = TimestampPrefixPattern.Replace(line, string.Empty);
        var match = LeadingLevelPattern.Match(body);
        if (!match.Success)
            return false;

        level = match.Groups[1].Value switch
        {
            "TRACE" => RuntimeLogLevel.Trace,
            "DEBUG" => RuntimeLogLevel.Debug,
            "INFO" => RuntimeLogLevel.Info,
            "WARN" => RuntimeLogLevel.Warn,
            "ERROR" => RuntimeLogLevel.Error,
            _ => RuntimeLogLevel.Fatal // FATAL 与 PANIC 都按最高档处理
        };
        return true;
    }

    /// <summary>
    /// 级别达到 minimumLevel 且（若有搜索词）行内不区分大小写地包含 searchText 才通过。
    /// </summary>
    public static bool Matches(string? line, RuntimeLogLevel minimumLevel, string? searchText)
    {
        if (!TryParseLevel(line, out var level))
            level = RuntimeLogLevel.Info;
        if (level < minimumLevel)
            return false;
        if (string.IsNullOrWhiteSpace(searchText))
            return true;
        return (line ?? string.Empty).IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// 导出为逐行 "[Time] Message" 文本；Message 再过一遍 RedactSecrets 作双保险，
    /// 用 Environment.NewLine 连接且末尾不带换行，空集合返回空字符串。
    /// </summary>
    public static string BuildExportText(IEnumerable<RuntimeLogLineSnapshot> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return string.Join(
            Environment.NewLine,
            lines.Select(snapshot => $"[{snapshot.Time}] {SingBoxRuntime.RedactSecrets(snapshot.Message)}"));
    }
}

public enum RuntimeLogLevel
{
    Trace = 0,
    Debug = 1,
    Info = 2,
    Warn = 3,
    Error = 4,
    Fatal = 5
}
