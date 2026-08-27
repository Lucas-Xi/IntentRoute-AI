using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class PolicyIntelligenceTests
{
    [Fact]
    public void Analyze_UsesBuilderOrderAndFindsEarlierSuperset()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, createdAt: "2026-01-01 09:00", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", createdAt: "2026-01-01 10:00", priority: 10)
        ];

        var report = PolicyIntelligence.Analyze(config);

        var finding = Assert.Single(report.Findings, item => item.Kind == PolicyFindingKind.Shadowed);
        Assert.Equal(PolicyFindingSeverity.Critical, finding.Severity);
        Assert.Equal([1, 2], finding.Rules.Select(rule => rule.EvaluationOrder).ToArray());
        Assert.Contains(report.Findings, item => item.Kind == PolicyFindingKind.PriorityTie);
    }

    [Fact]
    public void Analyze_DistinguishesDuplicateAndConflictByEffectiveOutcome()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", priority: 20),
            Rule("cursor.exe", ProxyMode.Direct, hosts: "example.com", priority: 30),
            Rule("cursor.exe", ProxyMode.Block, hosts: "example.com", priority: 40)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Contains(report.Findings, finding => finding.Kind == PolicyFindingKind.Duplicate);
        Assert.Contains(report.Findings, finding => finding.Kind == PolicyFindingKind.Conflict);
    }

    [Fact]
    public void Analyze_GlobalCatchAllIsBroadAndShadowsLaterSpecificRule()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("*", ProxyMode.Direct, priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Contains(report.Findings, finding =>
            finding.Kind == PolicyFindingKind.BroadScope &&
            finding.Severity == PolicyFindingSeverity.Critical);
        Assert.Contains(report.Findings, finding =>
            finding.Kind == PolicyFindingKind.Shadowed &&
            finding.Severity == PolicyFindingSeverity.Critical);
    }

    [Fact]
    public void Analyze_LaterCatchAllDoesNotShadowEarlierSpecificRule()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", priority: 10),
            Rule("*", ProxyMode.Direct, priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.DoesNotContain(report.Findings, finding => finding.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_TreatsDestinationMatcherTypesAsAnOrGroup()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, hosts: "*.example.com", ips: "10.0.0.0/8", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "api.example.com", priority: 20),
            Rule("chrome.exe", ProxyMode.Block, ips: "10.2.0.0/16", priority: 30)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Equal(2, report.Findings.Count(finding => finding.Kind == PolicyFindingKind.Shadowed));
    }

    [Fact]
    public void Analyze_DoesNotClaimContainmentAcrossUnrelatedDestinationTypes()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, hosts: "example.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, ips: "10.0.0.1", priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.DoesNotContain(report.Findings, finding =>
            finding.Kind is PolicyFindingKind.Shadowed or PolicyFindingKind.Conflict or PolicyFindingKind.Duplicate);
    }

    [Fact]
    public void Analyze_DomainSuffixContainsApexAndSubdomainButExactDoesNotContainSuffix()
    {
        var suffixFirst = BaseConfig();
        suffixFirst.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, hosts: "*.example.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "example.com", priority: 20),
            Rule("chrome.exe", ProxyMode.Block, hosts: "api.example.com", priority: 30)
        ];
        var exactFirst = BaseConfig();
        exactFirst.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, hosts: "example.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "*.example.com", priority: 20)
        ];

        var suffixReport = PolicyIntelligence.Analyze(suffixFirst);
        var exactReport = PolicyIntelligence.Analyze(exactFirst);

        Assert.Equal(2, suffixReport.Findings.Count(finding => finding.Kind == PolicyFindingKind.Shadowed));
        Assert.DoesNotContain(exactReport.Findings, finding => finding.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_RespectsProtocolPortAndCidrContainment()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, ips: "10.0.0.0/8", ports: "8000-9000", protocol: "Both", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, ips: "10.2.0.0/16", ports: "8443", protocol: "TCP", priority: 20),
            Rule("chrome.exe", ProxyMode.Proxy, ips: "10.2.0.0/16", ports: "8443", protocol: "UDP", priority: 30)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Equal(2, report.Findings.Count(finding => finding.Kind == PolicyFindingKind.Shadowed));
    }

    [Fact]
    public void Analyze_DoesNotTreatTcpAsContainingUdp()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, protocol: "TCP", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, protocol: "UDP", priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.DoesNotContain(report.Findings, finding => finding.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_DisabledInvalidRuleIsNeverReportedAsRuntimeShadow()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, ips: "999.1.1.1/99", enabled: false, priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Contains(report.Findings, finding => finding.Kind == PolicyFindingKind.DisabledInvalid);
        Assert.DoesNotContain(report.Findings, finding => finding.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_DisabledExactCopyIsInformationalOnly()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "github.com", enabled: false, priority: 20)
        ];

        var report = PolicyIntelligence.Analyze(config);

        var finding = Assert.Single(report.Findings, item => item.Kind == PolicyFindingKind.DisabledDuplicate);
        Assert.Equal(PolicyFindingSeverity.Info, finding.Severity);
        Assert.DoesNotContain(report.Findings, item => item.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_IsReadOnlyAndFingerprintChangesWithPolicy()
    {
        var config = BaseConfig();
        config.Rules = [Rule("chrome.exe", ProxyMode.Direct, hosts: "example.com")];
        var before = JsonConvert.SerializeObject(config);

        var first = PolicyIntelligence.Analyze(config);
        var after = JsonConvert.SerializeObject(config);
        config.Rules[0].TargetHosts = "changed.example.com";
        var second = PolicyIntelligence.Analyze(config);

        Assert.Equal(before, after);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
    }

    [Fact]
    public void MatchesSnapshot_RejectsGlobalModeAndProxyOrderChanges()
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
        config.Rules = [Rule("chrome.exe", ProxyMode.Proxy)];
        var report = PolicyIntelligence.Analyze(config);

        Assert.True(PolicyIntelligence.MatchesSnapshot(report, config));

        config.GlobalMode = GlobalMode.ProxyAll;
        Assert.False(PolicyIntelligence.MatchesSnapshot(report, config));
        config.GlobalMode = GlobalMode.DirectAll;

        config.ProxyServers.Reverse();
        Assert.False(PolicyIntelligence.MatchesSnapshot(report, config));
    }

    [Fact]
    public void Analyze_BoundsLargePoliciesAndMarksReportIncomplete()
    {
        var config = BaseConfig();
        config.Rules = Enumerable.Range(0, PolicyIntelligence.MaxRulesAnalyzedPerState + 1)
            .Select(index => Rule($"app-{index}.exe", ProxyMode.Direct, priority: 10))
            .ToList();

        var report = PolicyIntelligence.Analyze(config);

        Assert.False(report.IsComplete);
        Assert.True(report.OmittedFindingCount > 0);
        Assert.True(report.Findings.Count <= PolicyIntelligence.MaxLocalFindings);
        Assert.Contains(report.Findings, finding => finding.Kind == PolicyFindingKind.AnalysisIncomplete);
    }

    [Fact]
    public void Analyze_NormalizesEquivalentDestinationAndPortUnions()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("ports.exe", ProxyMode.Direct, ports: "80-90,91-100", priority: 10),
            Rule("ports.exe", ProxyMode.Direct, ports: "80-100", priority: 20),
            Rule("network.exe", ProxyMode.Direct, ips: "10.0.0.0/9,10.128.0.0/9", priority: 30),
            Rule("network.exe", ProxyMode.Direct, ips: "10.0.0.0/8", priority: 40),
            Rule("domain.exe", ProxyMode.Direct, hosts: "*.example.com,api.example.com", priority: 50),
            Rule("domain.exe", ProxyMode.Direct, hosts: "*.example.com", priority: 60)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Equal(3, report.Findings.Count(finding => finding.Kind == PolicyFindingKind.Duplicate));
    }

    [Fact]
    public void Analyze_DeduplicatesCidrsByNetworkValueInsteadOfArrayIdentity()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("exact-cidr.exe", ProxyMode.Direct, ips: "10.0.0.0/24,10.0.0.0/24", priority: 10),
            Rule("exact-cidr.exe", ProxyMode.Direct, ips: "10.0.0.0/24", priority: 20),
            Rule("equivalent-cidr.exe", ProxyMode.Direct, ips: "10.0.0.1/24,10.0.0.0/24", priority: 30),
            Rule("equivalent-cidr.exe", ProxyMode.Direct, ips: "10.0.0.0/24", priority: 40)
        ];

        var report = PolicyIntelligence.Analyze(config);

        Assert.Equal(2, report.Findings.Count(finding => finding.Kind == PolicyFindingKind.Duplicate));
        Assert.DoesNotContain(report.Findings, finding => finding.Kind == PolicyFindingKind.Shadowed);
    }

    [Fact]
    public void Analyze_HonorsCancellationBeforeWorkBegins()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            PolicyIntelligence.Analyze(BaseConfig(), cancellation.Token));
    }

    [Fact]
    public void Disclosure_OmitsAllLocalIdentifiersAndSensitiveValues()
    {
        var config = BaseConfig();
        config.SingBoxExecutablePath = "C:\\sensitive\\sing-box.exe";
        config.ProxyServers[0].Host = "127.0.0.1";
        config.ProxyServers[0].Port = 32109;
        config.ProxyServers[0].Username = "fixture-user";
        config.ProxyServers[0].Password = "fixture-password";
        config.Rules =
        [
            Rule("private-browser.exe", ProxyMode.Direct, hosts: "secret.example.com", ips: "10.20.30.40", ports: "54321", priority: 10),
            Rule("private-browser.exe", ProxyMode.Proxy, hosts: "secret.example.com", ips: "10.20.30.40", ports: "54321", priority: 20)
        ];
        config.Rules[0].Note = "fixture-note";
        config.Rules[0].ExePath = "C:\\sensitive\\private-browser.exe";

        var disclosure = PolicyIntelligence.ToDisclosure(PolicyIntelligence.Analyze(config));
        var json = AiPolicyContract.CreateInput(disclosure);

        foreach (var forbidden in new[]
        {
            "private-browser.exe", "secret.example.com", "10.20.30.40", "54321",
            "32109", "fixture-user", "fixture-password", "fixture-note", "C:\\sensitive",
            config.Rules[0].Id, config.ProxyServers[0].Id
        })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
        Assert.DoesNotContain("ProxyServers", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Rules", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Disclosure_ContainsOnlyExplicitlySelectedFindingCodes()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("*", ProxyMode.Direct, priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "example.com", priority: 20)
        ];
        var report = PolicyIntelligence.Analyze(config);
        var selected = report.Findings.First(finding => finding.Kind == PolicyFindingKind.Shadowed).Code;

        var disclosure = PolicyIntelligence.ToDisclosure(report, [selected]);

        Assert.Single(disclosure.Findings);
        Assert.Equal(selected, disclosure.Findings[0].Code);
        Assert.Equal(report.Findings.Count - 1, disclosure.OmittedFindingCount);
    }

    [Fact]
    public void ExplanationParser_RejectsUnknownFindingAndAdditionalProperties()
    {
        var disclosure = DisclosureWithConflict();
        var unknown = ValidExplanationJson("PIR-999");
        var extra = JObject.Parse(ValidExplanationJson(disclosure.Findings[0].Code));
        extra["rules"] = new JArray();

        Assert.Throws<AiProviderException>(() => AiPolicyContract.ParseExplanation(unknown, disclosure));
        Assert.Throws<AiProviderException>(() => AiPolicyContract.ParseExplanation(extra.ToString(), disclosure));
    }

    [Fact]
    public async Task OpenAiPolicyExplainer_UsesStrictStoreFalseDisclosureOnly()
    {
        string? requestBody = null;
        var disclosure = DisclosureWithConflict();
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new JObject
            {
                ["output"] = new JArray(new JObject
                {
                    ["type"] = "message",
                    ["content"] = new JArray(new JObject
                    {
                        ["type"] = "output_text",
                        ["text"] = ValidExplanationJson(disclosure.Findings[0].Code)
                    })
                })
            }.ToString());
        });
        using var client = new HttpClient(handler);
        using var provider = new OpenAiRuleProvider(client, () => "test-key");

        var result = await provider.ExplainPolicyAsync(
            new AiPolicyExplainRequest(OpenAiRuleProvider.DefaultModel, disclosure));

        Assert.Single(result.Priorities);
        var payload = JObject.Parse(requestBody!);
        Assert.False((bool)payload["store"]!);
        Assert.True((bool)payload["text"]?["format"]?["strict"]!);
        Assert.False((bool)payload["text"]?["format"]?["schema"]?["additionalProperties"]!);
        Assert.Null(payload["tools"]);
        Assert.DoesNotContain("test-key", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("processName", requestBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("targetHosts", requestBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OllamaPolicyExplainer_UsesNonStreamingStrictDisclosure()
    {
        string? requestBody = null;
        var disclosure = DisclosureWithConflict();
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new JObject
            {
                ["message"] = new JObject
                {
                    ["role"] = "assistant",
                    ["content"] = ValidExplanationJson(disclosure.Findings[0].Code)
                },
                ["done"] = true
            }.ToString());
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaRuleProvider(client, new Uri("http://127.0.0.1:11434/"));

        var result = await provider.ExplainPolicyAsync(new AiPolicyExplainRequest("qwen3:8b", disclosure));

        Assert.Single(result.Priorities);
        var payload = JObject.Parse(requestBody!);
        Assert.False((bool)payload["stream"]!);
        Assert.False((bool)payload["format"]?["additionalProperties"]!);
        Assert.Equal(0, (double)payload["options"]?["temperature"]!);
    }

    private static PolicyDisclosure DisclosureWithConflict()
    {
        var config = BaseConfig();
        config.Rules =
        [
            Rule("chrome.exe", ProxyMode.Direct, hosts: "example.com", priority: 10),
            Rule("chrome.exe", ProxyMode.Proxy, hosts: "example.com", priority: 20)
        ];
        return PolicyIntelligence.ToDisclosure(PolicyIntelligence.Analyze(config));
    }

    private static string ValidExplanationJson(string findingCode) => new JObject
    {
        ["summary"] = "A deterministic conflict should be reviewed.",
        ["priorities"] = new JArray(new JObject
        {
            ["findingCode"] = findingCode,
            ["explanation"] = "An earlier rule has the same scope.",
            ["safeNextStep"] = "Review both rules and change nothing automatically.",
            ["confidence"] = 0.9
        }),
        ["caveats"] = new JArray("The explanation does not observe live traffic.")
    }.ToString();

    private static AppConfig BaseConfig() => new()
    {
        GlobalMode = GlobalMode.DirectAll,
        ProxyServers =
        [
            new ProxyServer
            {
                Id = "local-proxy",
                Name = "Local SOCKS5",
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
        ExeName = process,
        Mode = mode,
        TargetHosts = hosts,
        TargetIPs = ips,
        TargetPorts = ports,
        Protocol = protocol,
        IsEnabled = enabled,
        Priority = priority,
        CreatedAt = createdAt
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
