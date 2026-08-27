using Newtonsoft.Json;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class RouteDecisionSimulatorTests
{
    [Fact]
    public void SimulateRoute_UsesCanonicalPriorityCreationAndSourceOrder()
    {
        var config = BaseConfig();
        var sourceTieWinner = Rule("browser.exe", ProxyMode.Block, priority: 10, createdAt: "2026-01-02 09:00");
        config.Rules =
        [
            Rule("browser.exe", ProxyMode.Direct, priority: 20, createdAt: "2026-01-01 09:00"),
            sourceTieWinner,
            Rule("browser.exe", ProxyMode.Proxy, priority: 10, createdAt: "2026-01-02 09:00"),
            Rule("browser.exe", ProxyMode.Proxy, priority: 10, createdAt: "2026-01-03 09:00")
        ];

        var report = Simulate(config);

        Assert.Equal(RouteDecisionKind.MatchedRule, report.Kind);
        Assert.Equal(ProxyMode.Block, report.Action);
        Assert.Equal(sourceTieWinner.Id, report.MatchedRuleId);
        Assert.Equal(1, report.MatchedEvaluationOrder);
        Assert.Equal(1, report.EvaluatedRuleCount);
    }

    [Fact]
    public void SimulateRoute_MatchesGlobalRuleAndIgnoresDisabledRule()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("browser.exe", ProxyMode.Block, enabled: false, priority: 1),
            Rule("*", ProxyMode.Direct, priority: 2)
        ];

        var report = Simulate(config, process: "another.exe");

        Assert.Equal(RouteDecisionKind.MatchedRule, report.Kind);
        Assert.Equal(ProxyMode.Direct, report.Action);
        Assert.Single(report.Trace);
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("api.example.com")]
    [InlineData("deep.api.example.com")]
    public void SimulateRoute_DomainSuffixMatchesApexAndSubdomains(string destination)
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, hosts: "*.example.com")];

        var report = Simulate(config, destination: destination);

        Assert.Equal(RouteDecisionKind.MatchedRule, report.Kind);
        Assert.Equal(ProxyMode.Block, report.Action);
    }

    [Fact]
    public void SimulateRoute_ExactDomainDoesNotMatchSubdomain()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, hosts: "example.com")];

        var report = Simulate(config, destination: "api.example.com");

        Assert.Equal(RouteDecisionKind.GlobalFallback, report.Kind);
        Assert.Equal(RouteDecisionReason.DestinationMismatch, Assert.Single(report.Trace).Reason);
    }

    [Theory]
    [InlineData("10.20.30.40", "10.0.0.0/8")]
    [InlineData("2001:db8:abcd::10", "2001:db8::/32")]
    public void SimulateRoute_MatchesIpv4AndIpv6Cidr(string destination, string cidr)
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Direct, ips: cidr)];

        var report = Simulate(config, RouteDestinationKind.Ip, destination);

        Assert.Equal(RouteDecisionKind.MatchedRule, report.Kind);
        Assert.Equal(ProxyMode.Direct, report.Action);
    }

    [Theory]
    [InlineData(443, RouteTransport.Tcp, RouteDecisionKind.MatchedRule)]
    [InlineData(8443, RouteTransport.Tcp, RouteDecisionKind.MatchedRule)]
    [InlineData(9000, RouteTransport.Tcp, RouteDecisionKind.MatchedRule)]
    [InlineData(443, RouteTransport.Udp, RouteDecisionKind.GlobalFallback)]
    [InlineData(444, RouteTransport.Tcp, RouteDecisionKind.GlobalFallback)]
    public void SimulateRoute_RespectsPortRangesAndTransport(
        int port,
        RouteTransport transport,
        RouteDecisionKind expected)
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, ports: "443,8443-9000", protocol: "TCP")];

        var report = Simulate(config, port: port, transport: transport);

        Assert.Equal(expected, report.Kind);
    }

    [Fact]
    public void SimulateRoute_ResolvesExplicitAndDefaultProxyIdentity()
    {
        var config = BaseConfig();
        config.ProxyServers.Add(new ProxyServer
        {
            Id = "secondary-proxy",
            Name = "Secondary",
            Host = "127.0.0.1",
            Port = 2080,
            Enabled = true
        });
        var defaultRule = Rule("default.exe", ProxyMode.Proxy);
        var explicitRule = Rule("explicit.exe", ProxyMode.Proxy);
        explicitRule.ProxyId = "secondary-proxy";
        config.Rules = [defaultRule, explicitRule];

        var defaultReport = Simulate(config, process: "default.exe");
        var explicitReport = Simulate(config, process: "explicit.exe");

        Assert.Equal("local-proxy", defaultReport.ResolvedProxyId);
        Assert.Equal("secondary-proxy", explicitReport.ResolvedProxyId);
    }

    [Theory]
    [InlineData(GlobalMode.DirectAll, ProxyMode.Direct, null)]
    [InlineData(GlobalMode.ProxyAll, ProxyMode.Proxy, "local-proxy")]
    public void SimulateRoute_ReturnsGlobalFallbackAfterAllProvenMisses(
        GlobalMode globalMode,
        ProxyMode action,
        string? proxyId)
    {
        var config = BaseConfig();
        config.GlobalMode = globalMode;
        config.Rules = [Rule("other.exe", ProxyMode.Block)];

        var report = Simulate(config);

        Assert.Equal(RouteDecisionKind.GlobalFallback, report.Kind);
        Assert.Equal(action, report.Action);
        Assert.Equal(proxyId, report.ResolvedProxyId);
        Assert.Equal(RouteDecisionReason.ProcessMismatch, Assert.Single(report.Trace).Reason);
    }

    [Fact]
    public void SimulateRoute_DomainMissWithIpAlternativeIsIndeterminate()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, hosts: "example.com", ips: "10.0.0.0/8")];

        var report = Simulate(config, destination: "unknown.example.net");

        Assert.Equal(RouteDecisionKind.Indeterminate, report.Kind);
        Assert.Equal(RouteDecisionReason.ResolvedIpRequired, report.Reason);
        Assert.Null(report.Action);
    }

    [Fact]
    public void SimulateRoute_IpMissWithDomainAlternativeIsIndeterminate()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, hosts: "example.com", ips: "10.0.0.0/8")];

        var report = Simulate(config, RouteDestinationKind.Ip, "192.0.2.1");

        Assert.Equal(RouteDecisionKind.Indeterminate, report.Kind);
        Assert.Equal(RouteDecisionReason.DomainContextRequired, report.Reason);
        Assert.Null(report.Action);
    }

    [Fact]
    public void SimulateRoute_KnownMemberOfDestinationOrGroupIsAProvenMatch()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Block, hosts: "example.com", ips: "10.0.0.0/8")];

        var domainReport = Simulate(config, destination: "example.com");
        var ipReport = Simulate(config, RouteDestinationKind.Ip, "10.1.2.3");

        Assert.Equal(RouteDecisionKind.MatchedRule, domainReport.Kind);
        Assert.Equal(RouteDecisionKind.MatchedRule, ipReport.Kind);
    }

    [Theory]
    [InlineData("other.exe", 443, RouteTransport.Tcp, RouteDecisionReason.ProcessMismatch)]
    [InlineData("browser.exe", 80, RouteTransport.Tcp, RouteDecisionReason.PortMismatch)]
    [InlineData("browser.exe", 443, RouteTransport.Udp, RouteDecisionReason.TransportMismatch)]
    public void SimulateRoute_ProvenScopeMismatchTakesPriorityOverMissingDestinationContext(
        string process,
        int port,
        RouteTransport transport,
        RouteDecisionReason reason)
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("browser.exe", ProxyMode.Block, ips: "10.0.0.0/8", ports: "443", protocol: "TCP")
        ];

        var report = Simulate(config, process: process, destination: "example.com", port: port, transport: transport);

        Assert.Equal(RouteDecisionKind.GlobalFallback, report.Kind);
        Assert.Equal(reason, Assert.Single(report.Trace).Reason);
    }

    [Theory]
    [InlineData("", RouteDestinationKind.Domain, "example.com", 443)]
    [InlineData("*.exe", RouteDestinationKind.Domain, "example.com", 443)]
    [InlineData("C:\\Apps\\browser.exe", RouteDestinationKind.Domain, "example.com", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Domain, "*.example.com", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Domain, "192.0.2.1", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Ip, "10.0.0.0/8", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Ip, "127.1", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Ip, "010.0.0.1", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Ip, "fe80::1%3", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Ip, "not-an-ip", 443)]
    [InlineData("browser.exe", RouteDestinationKind.Domain, "example.com", 0)]
    [InlineData("browser.exe", RouteDestinationKind.Domain, "example.com", 65536)]
    public void SimulateRoute_RejectsInvalidOrAmbiguousQuery(
        string process,
        RouteDestinationKind kind,
        string destination,
        int port)
    {
        var report = PolicyIntelligence.SimulateRoute(
            BaseConfig(),
            new RouteDecisionQuery(process, kind, destination, port, RouteTransport.Tcp));

        Assert.Equal(RouteDecisionKind.InvalidQuery, report.Kind);
        Assert.Equal(RouteDecisionReason.InvalidQuery, report.Reason);
        Assert.False(report.IsProven);
        Assert.False(report.IsSnapshotBound);
        Assert.Empty(report.Trace);
    }

    [Fact]
    public void SimulateRoute_RejectsInvalidEnumValues()
    {
        var config = BaseConfig();

        var invalidDestination = PolicyIntelligence.SimulateRoute(
            config,
            new RouteDecisionQuery("browser.exe", (RouteDestinationKind)999, "example.com", 443, RouteTransport.Tcp));
        var invalidTransport = PolicyIntelligence.SimulateRoute(
            config,
            new RouteDecisionQuery("browser.exe", RouteDestinationKind.Domain, "example.com", 443, (RouteTransport)999));

        Assert.Equal(RouteDecisionKind.InvalidQuery, invalidDestination.Kind);
        Assert.Equal(RouteDecisionKind.InvalidQuery, invalidTransport.Kind);
    }

    [Fact]
    public void SimulateRoute_FailsClosedWhenPolicyCannotBuild()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Proxy, ips: "999.1.1.1/99")];

        var report = Simulate(config);

        Assert.Equal(RouteDecisionKind.InvalidPolicy, report.Kind);
        Assert.Equal(RouteDecisionReason.InvalidPolicy, report.Reason);
        Assert.False(report.IsProven);
        Assert.True(report.IsSnapshotBound);
        Assert.NotEmpty(report.Error!);
        Assert.Empty(report.Trace);
    }

    [Fact]
    public void SimulateRoute_StopsAtFirstIndeterminateRule()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("browser.exe", ProxyMode.Block, ips: "10.0.0.0/8", priority: 10),
            Rule("browser.exe", ProxyMode.Direct, hosts: "example.com", priority: 20)
        ];

        var report = Simulate(config, destination: "example.com");

        Assert.Equal(RouteDecisionKind.Indeterminate, report.Kind);
        Assert.Equal(1, report.EvaluatedRuleCount);
        Assert.Single(report.Trace);
    }

    [Fact]
    public void SimulateRoute_BoundsEvaluationAndReturnsIndeterminate()
    {
        var config = BaseConfig();
        config.Rules = Enumerable.Range(0, PolicyIntelligence.MaxRouteRulesEvaluated + 1)
            .Select(index => Rule($"other-{index}.exe", ProxyMode.Direct, priority: index))
            .ToList();

        var report = Simulate(config);

        Assert.Equal(RouteDecisionKind.Indeterminate, report.Kind);
        Assert.Equal(RouteDecisionReason.EvaluationBudgetExceeded, report.Reason);
        Assert.Equal(PolicyIntelligence.MaxRouteRulesEvaluated, report.EvaluatedRuleCount);
        Assert.Equal(PolicyIntelligence.MaxRouteRulesEvaluated, report.Trace.Count);
    }

    [Fact]
    public void SimulateRoute_IsReadOnlyAndFingerprintBindsPolicyAndQuery()
    {
        var config = BaseConfig();
        config.Rules = [Rule("browser.exe", ProxyMode.Direct, hosts: "example.com")];
        var before = JsonConvert.SerializeObject(config);
        var query = Query();

        var report = PolicyIntelligence.SimulateRoute(config, query);

        Assert.Equal(before, JsonConvert.SerializeObject(config));
        Assert.True(PolicyIntelligence.MatchesRouteSnapshot(report, config, query));
        Assert.False(PolicyIntelligence.MatchesRouteSnapshot(report, config, query with { Port = 80 }));
        config.Rules[0].Mode = ProxyMode.Block;
        Assert.False(PolicyIntelligence.MatchesRouteSnapshot(report, config, query));
    }

    [Fact]
    public void SimulateRoute_HonorsCancellationBeforeWorkBegins()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PolicyIntelligence.SimulateRoute(BaseConfig(), Query(), cancellation.Token));
    }

    private static RouteDecisionReport Simulate(
        AppConfig config,
        RouteDestinationKind destinationKind = RouteDestinationKind.Domain,
        string destination = "example.com",
        string process = "browser.exe",
        int port = 443,
        RouteTransport transport = RouteTransport.Tcp) =>
        PolicyIntelligence.SimulateRoute(
            config,
            new RouteDecisionQuery(process, destinationKind, destination, port, transport));

    private static RouteDecisionQuery Query() =>
        new("browser.exe", RouteDestinationKind.Domain, "example.com", 443, RouteTransport.Tcp);

    private static AppConfig BaseConfig() => new()
    {
        GlobalMode = GlobalMode.DirectAll,
        ProxyServers =
        [
            new ProxyServer
            {
                Id = "local-proxy",
                Name = "Local SOCKS",
                ProxyType = ProxyType.Socks5,
                Host = "127.0.0.1",
                Port = 10808,
                Enabled = true
            }
        ]
    };

    private static ProxyRule Rule(
        string process,
        ProxyMode mode,
        string hosts = "",
        string ips = "",
        string ports = "",
        string protocol = "Both",
        bool enabled = true,
        int priority = 100,
        string createdAt = "2026-01-01 09:00") => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ExeName = process,
        ExePath = process == "*" ? string.Empty : $"C:\\Apps\\{process}",
        Mode = mode,
        TargetHosts = hosts,
        TargetIPs = ips,
        TargetPorts = ports,
        Protocol = protocol,
        IsEnabled = enabled,
        Priority = priority,
        CreatedAt = createdAt
    };
}
