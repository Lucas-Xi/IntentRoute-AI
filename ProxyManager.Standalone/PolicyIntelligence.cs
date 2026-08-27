using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ProxyManager.Standalone;

public enum PolicyFindingSeverity
{
    Info,
    Warning,
    Critical
}

public enum PolicyFindingKind
{
    Duplicate,
    Conflict,
    Shadowed,
    BroadScope,
    DisabledInvalid,
    DisabledDuplicate,
    PriorityTie,
    AnalysisIncomplete,
    GlobalProxyPosture
}

public enum PolicyScopeRelation
{
    SingleRule,
    ExactMatch,
    EarlierSuperset,
    SamePriorityOverlap,
    InactiveExactMatch,
    GlobalDefault
}

public sealed record PolicyRuleReference(
    string RuleId,
    string DisplayName,
    int? EvaluationOrder,
    bool IsEnabled);

public sealed record PolicyFinding(
    string Code,
    PolicyFindingKind Kind,
    PolicyFindingSeverity Severity,
    PolicyScopeRelation Relation,
    string Title,
    string Detail,
    string Recommendation,
    IReadOnlyList<PolicyRuleReference> Rules);

public sealed class PolicyAnalysisReport
{
    internal PolicyAnalysisReport(
        string fingerprint,
        GlobalMode globalMode,
        int activeRuleCount,
        int disabledRuleCount,
        int proxyRuleCount,
        int directRuleCount,
        int blockRuleCount,
        int enabledProxyCount,
        bool isComplete,
        int omittedFindingCount,
        IReadOnlyList<PolicyFinding> findings)
    {
        Fingerprint = fingerprint;
        GlobalMode = globalMode;
        ActiveRuleCount = activeRuleCount;
        DisabledRuleCount = disabledRuleCount;
        ProxyRuleCount = proxyRuleCount;
        DirectRuleCount = directRuleCount;
        BlockRuleCount = blockRuleCount;
        EnabledProxyCount = enabledProxyCount;
        IsComplete = isComplete;
        OmittedFindingCount = omittedFindingCount;
        Findings = findings;
    }

    public string Fingerprint { get; }
    public GlobalMode GlobalMode { get; }
    public int ActiveRuleCount { get; }
    public int DisabledRuleCount { get; }
    public int ProxyRuleCount { get; }
    public int DirectRuleCount { get; }
    public int BlockRuleCount { get; }
    public int EnabledProxyCount { get; }
    public bool IsComplete { get; }
    public int OmittedFindingCount { get; }
    public IReadOnlyList<PolicyFinding> Findings { get; }
    public int CriticalCount => Findings.Count(finding => finding.Severity == PolicyFindingSeverity.Critical);
    public int WarningCount => Findings.Count(finding => finding.Severity == PolicyFindingSeverity.Warning);
    public int InfoCount => Findings.Count(finding => finding.Severity == PolicyFindingSeverity.Info);
}

public sealed record PolicyDisclosureFinding(
    string Code,
    PolicyFindingKind Kind,
    PolicyFindingSeverity Severity,
    PolicyScopeRelation Relation,
    int AffectedRuleCount);

public sealed class PolicyDisclosure
{
    public const int MaxFindings = 20;

    internal PolicyDisclosure(
        GlobalMode globalMode,
        int activeRuleCount,
        int disabledRuleCount,
        int proxyRuleCount,
        int directRuleCount,
        int blockRuleCount,
        int enabledProxyCount,
        int omittedFindingCount,
        IReadOnlyList<PolicyDisclosureFinding> findings)
    {
        GlobalMode = globalMode;
        ActiveRuleCount = activeRuleCount;
        DisabledRuleCount = disabledRuleCount;
        ProxyRuleCount = proxyRuleCount;
        DirectRuleCount = directRuleCount;
        BlockRuleCount = blockRuleCount;
        EnabledProxyCount = enabledProxyCount;
        OmittedFindingCount = omittedFindingCount;
        Findings = findings;
    }

    public GlobalMode GlobalMode { get; }
    public int ActiveRuleCount { get; }
    public int DisabledRuleCount { get; }
    public int ProxyRuleCount { get; }
    public int DirectRuleCount { get; }
    public int BlockRuleCount { get; }
    public int EnabledProxyCount { get; }
    public int OmittedFindingCount { get; }
    public IReadOnlyList<PolicyDisclosureFinding> Findings { get; }
}

/// <summary>
/// Performs conservative, read-only analysis of the exact ordering and matching shapes emitted by
/// <see cref="SingBoxConfigBuilder"/>. Local findings may contain rule labels; the separate
/// <see cref="ToDisclosure"/> projection is the only object allowed to cross an AI-provider seam.
/// </summary>
public static class PolicyIntelligence
{
    public const int MaxRulesAnalyzedPerState = 500;
    public const int MaxLocalFindings = 500;
    public const int MaxPairComparisons = 250_000;

    private static readonly char[] ListSeparators = [',', ';', '|', '\n', '\r', '\t', ' '];

    public static PolicyAnalysisReport Analyze(
        AppConfig snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        var rules = snapshot.Rules ?? [];
        var enabledProxyIds = (snapshot.ProxyServers ?? [])
            .Where(server => server != null && server.Enabled)
            .Select(server => server.Id ?? string.Empty)
            .ToList();
        var defaultProxyId = enabledProxyIds.FirstOrDefault();
        var findings = new List<PendingFinding>();
        var droppedFindings = 0;
        var pairComparisons = 0;
        var pairBudgetExhausted = false;
        void AddFinding(PendingFinding finding)
        {
            // Reserve the final slot for an explicit incomplete-analysis finding.
            if (findings.Count < MaxLocalFindings - 1) findings.Add(finding);
            else droppedFindings++;
        }

        var orderedEnabled = PolicyRuntimeOrder.Enabled(rules);
        var evaluated = orderedEnabled
            .Take(MaxRulesAnalyzedPerState)
            .Select(item => EvaluatedRule.TryCreate(
                item.Rule,
                item.SourceIndex,
                item.EvaluationOrder,
                defaultProxyId))
            .Where(item => item != null)
            .Select(item => item!)
            .ToList();

        foreach (var priorityGroup in evaluated.GroupBy(item => item.Rule.Priority).Where(group => group.Count() > 1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tied = priorityGroup.ToList();
            for (var laterIndex = 1; laterIndex < tied.Count; laterIndex++)
            {
                for (var earlierIndex = 0; earlierIndex < laterIndex; earlierIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var earlier = tied[earlierIndex];
                    var later = tied[laterIndex];
                    if (++pairComparisons > MaxPairComparisons)
                    {
                        pairBudgetExhausted = true;
                        break;
                    }
                    if (!earlier.Scope.Contains(later.Scope) && !later.Scope.Contains(earlier.Scope)) continue;
                    AddFinding(PendingFinding.Pair(
                        PolicyFindingKind.PriorityTie,
                        PolicyFindingSeverity.Warning,
                        PolicyScopeRelation.SamePriorityOverlap,
                        "重叠规则使用相同优先级",
                        $"第 {earlier.EvaluationOrder} 与第 {later.EvaluationOrder} 条活动规则的优先级均为 {earlier.Rule.Priority}；当前由创建时间和持久化顺序决定先后。",
                        "为重叠规则设置不同优先级，使运行顺序不依赖次级排序。",
                        earlier,
                        later));
                }
                if (pairBudgetExhausted) break;
            }
            if (pairBudgetExhausted) break;
        }

        for (var laterIndex = 0; laterIndex < evaluated.Count && !pairBudgetExhausted; laterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var later = evaluated[laterIndex];
            for (var earlierIndex = 0; earlierIndex < laterIndex; earlierIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var earlier = evaluated[earlierIndex];
                if (++pairComparisons > MaxPairComparisons)
                {
                    pairBudgetExhausted = true;
                    break;
                }
                if (!earlier.Scope.Contains(later.Scope)) continue;

                var exact = earlier.Scope.Equals(later.Scope);
                var sameOutcome = earlier.Outcome.Equals(later.Outcome);
                if (exact && sameOutcome)
                {
                    AddFinding(PendingFinding.Pair(
                        PolicyFindingKind.Duplicate,
                        PolicyFindingSeverity.Warning,
                        PolicyScopeRelation.ExactMatch,
                        "重复的活动规则",
                        $"第 {later.EvaluationOrder} 条活动规则与第 {earlier.EvaluationOrder} 条具有相同匹配范围和动作，后者已先被求值。",
                        "保留一条即可；删除前请核对两条规则的本地备注。",
                        earlier,
                        later));
                }
                else if (exact)
                {
                    AddFinding(PendingFinding.Pair(
                        PolicyFindingKind.Conflict,
                        PolicyFindingSeverity.Critical,
                        PolicyScopeRelation.ExactMatch,
                        "相同范围使用了不同动作",
                        $"第 {earlier.EvaluationOrder} 条活动规则先于第 {later.EvaluationOrder} 条求值，后者的相同匹配范围不会获得不同结果。",
                        "调整优先级、缩小匹配范围，或删除其中一条冲突规则。",
                        earlier,
                        later));
                }
                else
                {
                    AddFinding(PendingFinding.Pair(
                        PolicyFindingKind.Shadowed,
                        sameOutcome ? PolicyFindingSeverity.Warning : PolicyFindingSeverity.Critical,
                        PolicyScopeRelation.EarlierSuperset,
                        sameOutcome ? "后续规则被更宽的同动作规则遮蔽" : "后续规则被更宽的不同动作规则遮蔽",
                        $"第 {earlier.EvaluationOrder} 条活动规则的匹配范围确定性地包含第 {later.EvaluationOrder} 条，且会先被求值。",
                        sameOutcome
                            ? "如无需单独说明该范围，可移除后续规则；否则应重新设计优先级和范围。"
                            : "将更具体的规则移到前面，或收窄前置规则后再应用。",
                        earlier,
                        later));
                }

                // The first proven superset is the earliest runtime cause; reporting every later
                // superset would produce duplicate advice without changing the result.
                break;
            }
        }

        foreach (var rule in evaluated.Where(item => item.Scope.IsUnconstrainedExceptProcess))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var global = rule.Scope.ProcessName == null;
            AddFinding(PendingFinding.Single(
                PolicyFindingKind.BroadScope,
                global ? PolicyFindingSeverity.Critical : PolicyFindingSeverity.Info,
                PolicyScopeRelation.SingleRule,
                global ? "全局无条件规则影响所有流量" : "该规则覆盖一个进程的全部流量",
                global
                    ? $"第 {rule.EvaluationOrder} 条活动规则使用显式 *，且覆盖所有支持的 TCP/UDP 目的地址与端口。"
                    : $"第 {rule.EvaluationOrder} 条活动规则覆盖该进程所有支持的 TCP/UDP 目的地址与端口。",
                global
                    ? "确认这是有意的全局策略，并检查其后的规则是否已被遮蔽。"
                    : "若只需要部分流量，请增加域名、IP、端口或协议条件。",
                rule));
        }

        var disabled = rules
            .Select((rule, sourceIndex) => new { Rule = rule, SourceIndex = sourceIndex })
            .Where(item => item.Rule != null && !item.Rule.IsEnabled)
            .Take(MaxRulesAnalyzedPerState)
            .ToList();
        cancellationToken.ThrowIfCancellationRequested();
        var disabledValidationBase = CreateDisabledValidationBase(snapshot);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var item in disabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var disabledError = ValidateDisabledRule(disabledValidationBase, item.Rule);
            if (disabledError != null)
            {
                AddFinding(PendingFinding.Disabled(
                    PolicyFindingKind.DisabledInvalid,
                    PolicyFindingSeverity.Warning,
                    "禁用规则当前无法安全启用",
                    disabledError,
                    "先修正规则或代理引用；不要在未重新校验前启用。",
                    item.Rule));
                continue;
            }

            var disabledEvaluated = EvaluatedRule.TryCreate(
                item.Rule,
                item.SourceIndex,
                evaluationOrder: null,
                defaultProxyId);
            if (disabledEvaluated == null) continue;

            var duplicate = evaluated.FirstOrDefault(active =>
                active.Scope.Equals(disabledEvaluated.Scope) &&
                active.Outcome.Equals(disabledEvaluated.Outcome));
            if (duplicate != null)
            {
                AddFinding(PendingFinding.DisabledPair(
                    PolicyFindingKind.DisabledDuplicate,
                    PolicyFindingSeverity.Info,
                    "禁用草案与活动规则重复",
                    "该禁用规则启用后不会增加新的匹配范围或动作。",
                    "确认草案不再需要后可删除；保持禁用不会影响运行时。",
                    duplicate,
                    disabledEvaluated));
            }
        }

        if (snapshot.GlobalMode == GlobalMode.ProxyAll)
        {
            AddFinding(new PendingFinding(
                PolicyFindingKind.GlobalProxyPosture,
                PolicyFindingSeverity.Info,
                PolicyScopeRelation.GlobalDefault,
                "未匹配流量默认走代理",
                "全局模式为 ProxyAll；所有未被活动规则匹配的流量使用默认代理出站。",
                "确认本地代理可用，并保留管理员恢复路径。",
                []));
        }

        var totalDisabledCount = rules.Count(rule => rule != null && !rule.IsEnabled);
        var omittedRuleCount = Math.Max(0, orderedEnabled.Count - evaluated.Count) +
            Math.Max(0, totalDisabledCount - disabled.Count);
        var isComplete = omittedRuleCount == 0 && droppedFindings == 0 && !pairBudgetExhausted;
        var incompleteItemCount = omittedRuleCount + droppedFindings + (pairBudgetExhausted ? 1 : 0);
        if (!isComplete)
        {
            findings.Add(new PendingFinding(
                PolicyFindingKind.AnalysisIncomplete,
                PolicyFindingSeverity.Warning,
                PolicyScopeRelation.SingleRule,
                "策略规模超过本地体检预算",
                $"为保持界面响应，本次至少有 {incompleteItemCount} 个规则、发现或比较批次未完整展开。",
                "减少重复/同优先级规则后重新体检；不要把当前报告视为完整结论。",
                []));
        }

        var materialized = findings
            .Select((finding, index) => finding.ToFinding($"PIR-{index + 1:000}"))
            .ToList();
        var active = rules.Where(rule => rule != null && rule.IsEnabled).ToList();

        return new PolicyAnalysisReport(
            CreateFingerprint(snapshot, cancellationToken),
            snapshot.GlobalMode,
            active.Count,
            totalDisabledCount,
            active.Count(rule => rule.Mode == ProxyMode.Proxy),
            active.Count(rule => rule.Mode == ProxyMode.Direct),
            active.Count(rule => rule.Mode == ProxyMode.Block),
            enabledProxyIds.Count,
            isComplete,
            incompleteItemCount,
            materialized);
    }

    public static PolicyDisclosure ToDisclosure(
        PolicyAnalysisReport report,
        IReadOnlyCollection<string>? selectedFindingCodes = null)
    {
        ArgumentNullException.ThrowIfNull(report);

        IEnumerable<PolicyFinding> candidates = report.Findings;
        if (selectedFindingCodes != null)
        {
            var requested = selectedFindingCodes.ToHashSet(StringComparer.Ordinal);
            if (requested.Count is < 1 or > PolicyDisclosure.MaxFindings ||
                requested.Any(code => report.Findings.All(finding => finding.Code != code)))
            {
                throw new ArgumentException("Selected policy findings are empty, excessive, or stale.", nameof(selectedFindingCodes));
            }
            candidates = report.Findings.Where(finding => requested.Contains(finding.Code));
        }

        var selected = candidates
            .OrderBy(finding => finding.Severity switch
            {
                PolicyFindingSeverity.Critical => 0,
                PolicyFindingSeverity.Warning => 1,
                _ => 2
            })
            .ThenBy(finding => finding.Code, StringComparer.Ordinal)
            .Take(PolicyDisclosure.MaxFindings)
            .ToList();
        var findings = selected.Select(finding => new PolicyDisclosureFinding(
                finding.Code,
                finding.Kind,
                finding.Severity,
                finding.Relation,
                finding.Rules.Count))
            .ToList();

        return new PolicyDisclosure(
            report.GlobalMode,
            report.ActiveRuleCount,
            report.DisabledRuleCount,
            report.ProxyRuleCount,
            report.DirectRuleCount,
            report.BlockRuleCount,
            report.EnabledProxyCount,
            report.OmittedFindingCount + Math.Max(0, report.Findings.Count - findings.Count),
            findings);
    }

    public static bool MatchesSnapshot(
        PolicyAnalysisReport report,
        AppConfig snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(snapshot);
        return string.Equals(
            report.Fingerprint,
            CreateFingerprint(snapshot, cancellationToken),
            StringComparison.Ordinal);
    }

    private static AppConfig CreateDisabledValidationBase(AppConfig snapshot)
    {
        var candidate = JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(snapshot))
            ?? throw new InvalidDataException("无法创建禁用规则校验快照。");
        candidate.Rules = [];
        candidate.ProxyChains = [];
        return candidate;
    }

    private static string? ValidateDisabledRule(AppConfig validationBase, ProxyRule rule)
    {
        try
        {
            var clonedRule = JsonConvert.DeserializeObject<ProxyRule>(JsonConvert.SerializeObject(rule))
                ?? throw new InvalidDataException("无法创建禁用规则副本。");
            clonedRule.IsEnabled = true;
            var candidate = new AppConfig
            {
                GlobalMode = validationBase.GlobalMode,
                DnsMode = validationBase.DnsMode,
                SocksHost = validationBase.SocksHost,
                SocksPort = validationBase.SocksPort,
                HttpHost = validationBase.HttpHost,
                HttpPort = validationBase.HttpPort,
                ProxyServers = validationBase.ProxyServers,
                ProxyChains = [],
                Rules = [clonedRule]
            };
            var build = SingBoxConfigBuilder.Build(candidate);
            return build.Success ? null : "本地构建校验失败: " + build.Error;
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            return "本地构建校验失败: " + ex.Message;
        }
    }

    private static string CreateFingerprint(AppConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = new StringBuilder()
            .Append((int)config.GlobalMode).Append('|')
            .Append((int)config.DnsMode).Append('|');
        foreach (var rule in config.Rules ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rule == null)
            {
                value.Append("<null>\u001e");
                continue;
            }

            value.Append(rule.Id).Append('\u001f')
                .Append(rule.ExeName).Append('\u001f')
                .Append(rule.TargetHosts).Append('\u001f')
                .Append(rule.TargetIPs).Append('\u001f')
                .Append(rule.TargetPorts).Append('\u001f')
                .Append(rule.Protocol).Append('\u001f')
                .Append((int)rule.Mode).Append('\u001f')
                .Append(rule.ProxyId).Append('\u001f')
                .Append(rule.ProxyChainId).Append('\u001f')
                .Append(rule.Priority).Append('\u001f')
                .Append(rule.CreatedAt).Append('\u001f')
                .Append(rule.IsEnabled).Append('\u001e');
        }

        foreach (var server in config.ProxyServers ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server != null)
                value.Append(server.Id).Append('\u001f').Append(server.Enabled).Append('\u001e');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.ToString())));
    }

    private static IEnumerable<string> SplitList(string? raw) =>
        (raw ?? string.Empty).Split(
            ListSeparators,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record EvaluatedRule(
        ProxyRule Rule,
        int SourceIndex,
        int? EvaluationOrder,
        MatchScope Scope,
        RuleOutcome Outcome)
    {
        public static EvaluatedRule? TryCreate(
            ProxyRule rule,
            int sourceIndex,
            int? evaluationOrder,
            string? defaultProxyId)
        {
            if (!MatchScope.TryCreate(rule, out var scope)) return null;
            var proxyId = rule.Mode == ProxyMode.Proxy
                ? (string.IsNullOrWhiteSpace(rule.ProxyId) ? defaultProxyId ?? string.Empty : rule.ProxyId.Trim())
                : string.Empty;
            return new EvaluatedRule(
                rule,
                sourceIndex,
                evaluationOrder,
                scope,
                new RuleOutcome(rule.Mode, proxyId));
        }

        public PolicyRuleReference ToReference() => new(
            Rule.Id,
            string.IsNullOrWhiteSpace(Rule.ExeName) ? "(未命名规则)" : Rule.ExeName,
            EvaluationOrder,
            Rule.IsEnabled);
    }

    private sealed record RuleOutcome(ProxyMode Mode, string ProxyId);

    private sealed class MatchScope : IEquatable<MatchScope>
    {
        private MatchScope(
            string? processName,
            IReadOnlyList<DomainMatcher> domains,
            IReadOnlyList<IpNetwork> ipNetworks,
            IReadOnlyList<PortRange> ports,
            NetworkClass network)
        {
            ProcessName = processName;
            Domains = domains;
            IpNetworks = ipNetworks;
            Ports = ports;
            Network = network;
        }

        public string? ProcessName { get; }
        public IReadOnlyList<DomainMatcher> Domains { get; }
        public IReadOnlyList<IpNetwork> IpNetworks { get; }
        public IReadOnlyList<PortRange> Ports { get; }
        public NetworkClass Network { get; }
        public bool HasDestinationFilter => Domains.Count > 0 || IpNetworks.Count > 0;
        public bool IsUnconstrainedExceptProcess => !HasDestinationFilter && Ports.Count == 0 && Network == NetworkClass.TcpUdp;

        public static bool TryCreate(ProxyRule rule, out MatchScope scope)
        {
            scope = null!;
            if (string.IsNullOrWhiteSpace(rule.ExeName)) return false;
            var rawProcess = rule.ExeName.Trim();
            if (rawProcess != "*" && rawProcess.IndexOfAny(['*', '?', '[', ']']) >= 0) return false;
            var process = rawProcess == "*" ? null : rawProcess.ToLowerInvariant();

            var domains = new List<DomainMatcher>();
            foreach (var token in SplitList(rule.TargetHosts))
            {
                var normalized = token.Trim().TrimEnd('.').ToLowerInvariant();
                if (normalized.StartsWith("*.", StringComparison.Ordinal))
                {
                    normalized = normalized[2..].TrimStart('.');
                    if (normalized.Length == 0 || normalized.IndexOfAny(['*', '?']) >= 0) return false;
                    domains.Add(new DomainMatcher(normalized, IsSuffix: true));
                }
                else
                {
                    if (normalized.Length == 0 || normalized.IndexOfAny(['*', '?']) >= 0) return false;
                    domains.Add(new DomainMatcher(normalized, IsSuffix: false));
                }
            }

            var networks = new List<IpNetwork>();
            foreach (var token in SplitList(rule.TargetIPs))
            {
                if (!IpNetwork.TryParse(token, out var network)) return false;
                networks.Add(network);
            }

            var ports = new List<PortRange>();
            foreach (var token in SplitList(rule.TargetPorts))
            {
                if (!PortRange.TryParse(token, out var range)) return false;
                ports.Add(range);
            }

            if (!TryParseNetwork(rule.Protocol, out var networkClass)) return false;
            scope = new MatchScope(
                process,
                NormalizeDomains(domains),
                NormalizeNetworks(networks),
                NormalizePorts(ports),
                networkClass);
            return true;
        }

        public bool Contains(MatchScope later)
        {
            if (ProcessName != null && !string.Equals(ProcessName, later.ProcessName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!NetworkContains(Network, later.Network)) return false;
            if (!ContainsPorts(Ports, later.Ports)) return false;
            return ContainsDestinations(this, later);
        }

        public bool Equals(MatchScope? other)
        {
            if (other == null) return false;
            return string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                Network == other.Network &&
                Domains.SequenceEqual(other.Domains) &&
                IpNetworks.SequenceEqual(other.IpNetworks) &&
                Ports.SequenceEqual(other.Ports);
        }

        public override bool Equals(object? obj) => Equals(obj as MatchScope);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(ProcessName, StringComparer.OrdinalIgnoreCase);
            hash.Add(Network);
            foreach (var domain in Domains) hash.Add(domain);
            foreach (var network in IpNetworks) hash.Add(network);
            foreach (var port in Ports) hash.Add(port);
            return hash.ToHashCode();
        }

        private static bool ContainsDestinations(MatchScope earlier, MatchScope later)
        {
            if (!earlier.HasDestinationFilter) return true;
            if (!later.HasDestinationFilter) return false;

            var domainsCovered = later.Domains.All(laterDomain =>
                earlier.Domains.Any(earlierDomain => earlierDomain.Contains(laterDomain)));
            if (!domainsCovered) return false;

            return later.IpNetworks.All(laterNetwork =>
                earlier.IpNetworks.Any(earlierNetwork => earlierNetwork.Contains(laterNetwork)));
        }

        private static bool ContainsPorts(IReadOnlyList<PortRange> earlier, IReadOnlyList<PortRange> later)
        {
            if (earlier.Count == 0) return true;
            if (later.Count == 0) return false;
            return later.All(laterRange => earlier.Any(earlierRange => earlierRange.Contains(laterRange)));
        }

        private static bool NetworkContains(NetworkClass earlier, NetworkClass later) =>
            earlier == NetworkClass.TcpUdp || earlier == later;

        private static bool TryParseNetwork(string? value, out NetworkClass network)
        {
            if (string.IsNullOrWhiteSpace(value) ||
                value.Equals("Both", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("TCP/UDP", StringComparison.OrdinalIgnoreCase))
            {
                network = NetworkClass.TcpUdp;
                return true;
            }

            if (value.Equals("TCP", StringComparison.OrdinalIgnoreCase))
            {
                network = NetworkClass.Tcp;
                return true;
            }

            if (value.Equals("UDP", StringComparison.OrdinalIgnoreCase))
            {
                network = NetworkClass.Udp;
                return true;
            }

            network = NetworkClass.TcpUdp;
            return false;
        }

        private static IReadOnlyList<DomainMatcher> NormalizeDomains(IEnumerable<DomainMatcher> domains)
        {
            var distinct = domains.Distinct().ToList();
            return distinct
                .Where(candidate => !distinct.Any(other =>
                    other != candidate && other.Contains(candidate)))
                .OrderBy(domain => domain.Value, StringComparer.Ordinal)
                .ThenBy(domain => domain.IsSuffix)
                .ToList();
        }

        private static IReadOnlyList<IpNetwork> NormalizeNetworks(IEnumerable<IpNetwork> networks)
        {
            var normalized = networks.Distinct().ToList();
            while (true)
            {
                normalized = normalized
                    .Where(candidate => !normalized.Any(other =>
                        other != candidate && other.Contains(candidate)))
                    .ToList();

                var merged = false;
                for (var leftIndex = 0; leftIndex < normalized.Count && !merged; leftIndex++)
                {
                    for (var rightIndex = leftIndex + 1; rightIndex < normalized.Count; rightIndex++)
                    {
                        if (!normalized[leftIndex].TryMergeSibling(normalized[rightIndex], out var parent))
                            continue;
                        normalized.RemoveAt(rightIndex);
                        normalized.RemoveAt(leftIndex);
                        normalized.Add(parent);
                        merged = true;
                        break;
                    }
                }

                if (!merged) break;
            }

            return normalized.OrderBy(network => network.SortKey, StringComparer.Ordinal).ToList();
        }

        private static IReadOnlyList<PortRange> NormalizePorts(IEnumerable<PortRange> ports)
        {
            var ordered = ports.Distinct().OrderBy(port => port.Start).ThenBy(port => port.End).ToList();
            if (ordered.Count < 2) return ordered;

            var merged = new List<PortRange> { ordered[0] };
            foreach (var next in ordered.Skip(1))
            {
                var current = merged[^1];
                if (next.Start <= current.End + 1)
                    merged[^1] = new PortRange(current.Start, Math.Max(current.End, next.End));
                else
                    merged.Add(next);
            }
            return merged;
        }
    }

    private enum NetworkClass
    {
        TcpUdp,
        Tcp,
        Udp
    }

    private readonly record struct DomainMatcher(string Value, bool IsSuffix)
    {
        public bool Contains(DomainMatcher later)
        {
            if (!IsSuffix) return !later.IsSuffix && string.Equals(Value, later.Value, StringComparison.Ordinal);
            if (!later.IsSuffix)
            {
                return string.Equals(Value, later.Value, StringComparison.Ordinal) ||
                    later.Value.EndsWith('.' + Value, StringComparison.Ordinal);
            }

            return string.Equals(Value, later.Value, StringComparison.Ordinal) ||
                later.Value.EndsWith('.' + Value, StringComparison.Ordinal);
        }
    }

    private readonly record struct PortRange(int Start, int End)
    {
        public bool Contains(PortRange later) => Start <= later.Start && End >= later.End;

        public static bool TryParse(string token, out PortRange range)
        {
            range = default;
            var separator = token.Contains('-') ? '-' : token.Contains(':') ? ':' : '\0';
            if (separator == '\0')
            {
                if (!TryPort(token, out var port)) return false;
                range = new PortRange(port, port);
                return true;
            }

            var parts = token.Split(separator, 2);
            if (!TryPort(parts[0], out var start) || !TryPort(parts[1], out var end) || start > end)
                return false;
            range = new PortRange(start, end);
            return true;
        }

        private static bool TryPort(string value, out int port) =>
            int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
            port is >= 1 and <= 65535;
    }

    private readonly record struct IpNetwork(AddressFamily Family, byte[] NetworkBytes, int PrefixLength)
    {
        public string SortKey => $"{(int)Family}:{PrefixLength}:{Convert.ToHexString(NetworkBytes)}";

        public bool Contains(IpNetwork later)
        {
            if (Family != later.Family || PrefixLength > later.PrefixLength) return false;
            var wholeBytes = PrefixLength / 8;
            var remainingBits = PrefixLength % 8;
            for (var index = 0; index < wholeBytes; index++)
            {
                if (NetworkBytes[index] != later.NetworkBytes[index]) return false;
            }

            if (remainingBits == 0) return true;
            var mask = (byte)(0xFF << (8 - remainingBits));
            return (NetworkBytes[wholeBytes] & mask) == (later.NetworkBytes[wholeBytes] & mask);
        }

        public bool TryMergeSibling(IpNetwork other, out IpNetwork parent)
        {
            parent = default;
            if (Family != other.Family || PrefixLength != other.PrefixLength || PrefixLength == 0 || Equals(other))
                return false;

            var parentPrefix = PrefixLength - 1;
            var leftParentBytes = NetworkBytes.ToArray();
            var rightParentBytes = other.NetworkBytes.ToArray();
            ApplyMask(leftParentBytes, parentPrefix);
            ApplyMask(rightParentBytes, parentPrefix);
            if (!leftParentBytes.SequenceEqual(rightParentBytes)) return false;

            parent = new IpNetwork(Family, leftParentBytes, parentPrefix);
            return true;
        }

        public bool Equals(IpNetwork other) =>
            Family == other.Family && PrefixLength == other.PrefixLength && NetworkBytes.SequenceEqual(other.NetworkBytes);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Family);
            hash.Add(PrefixLength);
            foreach (var value in NetworkBytes) hash.Add(value);
            return hash.ToHashCode();
        }

        public static bool TryParse(string token, out IpNetwork network)
        {
            network = default;
            var parts = token.Split('/', 2);
            if (!IPAddress.TryParse(parts[0], out var address)) return false;
            var maxPrefix = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
            var prefix = maxPrefix;
            if (parts.Length == 2 &&
                (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix) ||
                 prefix < 0 || prefix > maxPrefix))
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            ApplyMask(bytes, prefix);
            network = new IpNetwork(address.AddressFamily, bytes, prefix);
            return true;
        }

        private static void ApplyMask(byte[] bytes, int prefix)
        {
            var wholeBytes = prefix / 8;
            var remainingBits = prefix % 8;
            if (remainingBits > 0)
            {
                bytes[wholeBytes] &= (byte)(0xFF << (8 - remainingBits));
                wholeBytes++;
            }

            for (var index = wholeBytes; index < bytes.Length; index++) bytes[index] = 0;
        }
    }

    private sealed record PendingFinding(
        PolicyFindingKind Kind,
        PolicyFindingSeverity Severity,
        PolicyScopeRelation Relation,
        string Title,
        string Detail,
        string Recommendation,
        IReadOnlyList<PolicyRuleReference> Rules)
    {
        public static PendingFinding Pair(
            PolicyFindingKind kind,
            PolicyFindingSeverity severity,
            PolicyScopeRelation relation,
            string title,
            string detail,
            string recommendation,
            EvaluatedRule earlier,
            EvaluatedRule later) =>
            new(kind, severity, relation, title, detail, recommendation, [earlier.ToReference(), later.ToReference()]);

        public static PendingFinding Single(
            PolicyFindingKind kind,
            PolicyFindingSeverity severity,
            PolicyScopeRelation relation,
            string title,
            string detail,
            string recommendation,
            EvaluatedRule rule) =>
            new(kind, severity, relation, title, detail, recommendation, [rule.ToReference()]);

        public static PendingFinding Disabled(
            PolicyFindingKind kind,
            PolicyFindingSeverity severity,
            string title,
            string detail,
            string recommendation,
            ProxyRule rule) =>
            new(kind, severity, PolicyScopeRelation.SingleRule, title, detail, recommendation,
                [new PolicyRuleReference(rule.Id, rule.ExeName, null, false)]);

        public static PendingFinding DisabledPair(
            PolicyFindingKind kind,
            PolicyFindingSeverity severity,
            string title,
            string detail,
            string recommendation,
            EvaluatedRule active,
            EvaluatedRule disabled) =>
            new(kind, severity, PolicyScopeRelation.InactiveExactMatch, title, detail, recommendation,
                [active.ToReference(), disabled.ToReference()]);

        public PolicyFinding ToFinding(string code) =>
            new(code, Kind, Severity, Relation, Title, Detail, Recommendation, Rules);
    }
}
