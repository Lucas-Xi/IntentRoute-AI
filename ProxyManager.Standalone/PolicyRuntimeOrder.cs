namespace ProxyManager.Standalone;

internal sealed record OrderedPolicyRule(
    ProxyRule Rule,
    int SourceIndex,
    int EvaluationOrder);

/// <summary>
/// Owns the canonical rule order used by the generated sing-box route, local policy analysis,
/// and user-facing rule views. Source index is the stable tie-breaker when persisted minute-level
/// creation timestamps are equal.
/// </summary>
internal static class PolicyRuntimeOrder
{
    public static IReadOnlyList<OrderedPolicyRule> Enabled(IReadOnlyList<ProxyRule>? rules) =>
        Order(rules, enabledOnly: true)
            .Select((item, index) => new OrderedPolicyRule(item.Rule, item.SourceIndex, index + 1))
            .ToList();

    public static IReadOnlyList<ProxyRule> All(IReadOnlyList<ProxyRule>? rules) =>
        Order(rules, enabledOnly: false).Select(item => item.Rule).ToList();

    private static IOrderedEnumerable<(ProxyRule Rule, int SourceIndex)> Order(
        IReadOnlyList<ProxyRule>? rules,
        bool enabledOnly)
    {
        return (rules ?? [])
            .Select((rule, sourceIndex) => (Rule: rule, SourceIndex: sourceIndex))
            .Where(item => item.Rule != null && (!enabledOnly || item.Rule.IsEnabled))
            .OrderBy(item => item.Rule.Priority)
            .ThenBy(item => item.Rule.CreatedAt, StringComparer.Ordinal)
            .ThenBy(item => item.SourceIndex);
    }
}
