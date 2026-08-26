using Newtonsoft.Json.Linq;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class SingBoxConfigBuilderTests
{
    [Fact]
    public void Build_MapsProcessDestinationAndProxyWithoutLeakingPassword()
    {
        var config = BaseConfig();
        config.ProxyServers[0].Password = "top-secret-value";
        config.Rules.Add(new ProxyRule
        {
            ExeName = "chrome.exe",
            Mode = ProxyMode.Proxy,
            ProxyId = "proxy-1",
            TargetHosts = "api.example.com, *.example.org",
            TargetIPs = "10.0.0.1, 192.168.0.0/16",
            TargetPorts = "80,443,8000-9000",
            Protocol = "TCP",
            Priority = 10
        });

        var result = SingBoxConfigBuilder.Build(config);

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain("top-secret-value", result.RedactedJson, StringComparison.Ordinal);
        var root = JObject.Parse(result.RedactedJson!);
        Assert.Equal("tun", root["inbounds"]![0]!["type"]!.Value<string>());
        Assert.Contains(SingBoxConfigBuilder.TunIpv4Address, root["inbounds"]![0]!["address"]!.Values<string>());
        Assert.Contains(SingBoxConfigBuilder.TunIpv6Address, root["inbounds"]![0]!["address"]!.Values<string>());
        Assert.True(root["inbounds"]![0]!["auto_route"]!.Value<bool>());
        Assert.True(root["inbounds"]![0]!["strict_route"]!.Value<bool>());
        Assert.Equal("***", root["outbounds"]![1]!["password"]!.Value<string>());

        var rule = (JObject)root["route"]!["rules"]![0]!;
        Assert.Equal("chrome.exe", rule["process_name"]![0]!.Value<string>());
        Assert.Contains("api.example.com", rule["domain"]!.Values<string>());
        Assert.Contains("example.org", rule["domain_suffix"]!.Values<string>());
        Assert.Contains("10.0.0.1/32", rule["ip_cidr"]!.Values<string>());
        Assert.Contains("192.168.0.0/16", rule["ip_cidr"]!.Values<string>());
        Assert.Contains(443, rule["port"]!.Values<int>());
        Assert.Contains("8000:9000", rule["port_range"]!.Values<string>());
        Assert.Equal("tcp", rule["network"]!.Value<string>());
        Assert.Equal("route", rule["action"]!.Value<string>());
        Assert.Equal("proxy-proxy-1", rule["outbound"]!.Value<string>());
    }

    [Fact]
    public void Build_UsesRejectForBlockAndProxyAsGlobalFinal()
    {
        var config = BaseConfig();
        config.GlobalMode = GlobalMode.ProxyAll;
        config.Rules.Add(new ProxyRule { ExeName = "game.exe", Mode = ProxyMode.Block });

        var result = SingBoxConfigBuilder.Build(config);

        Assert.True(result.Success, result.Error);
        var root = JObject.Parse(result.RedactedJson!);
        Assert.Equal("proxy-proxy-1", root["route"]!["final"]!.Value<string>());
        Assert.Equal("reject", root["route"]!["rules"]![0]!["action"]!.Value<string>());
        Assert.Null(root["route"]!["rules"]![0]!["outbound"]);
    }

    [Fact]
    public void Build_AllowsGlobalWildcardByOmittingProcessFilter()
    {
        var config = BaseConfig();
        config.Rules.Add(new ProxyRule { ExeName = "*", Mode = ProxyMode.Direct });

        var result = SingBoxConfigBuilder.Build(config);

        Assert.True(result.Success, result.Error);
        var root = JObject.Parse(result.RedactedJson!);
        Assert.Null(root["route"]!["rules"]![0]!["process_name"]);
    }

    [Fact]
    public void Build_RejectsProxyRuleWithoutEnabledServer()
    {
        var config = new AppConfig();
        config.Rules.Add(new ProxyRule { ExeName = "browser.exe", Mode = ProxyMode.Proxy });

        var result = SingBoxConfigBuilder.Build(config);

        Assert.False(result.Success);
        Assert.Contains("no enabled proxy", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_RejectsUnsupportedChainAndInvalidCidr()
    {
        var withChain = BaseConfig();
        withChain.Rules.Add(new ProxyRule
        {
            ExeName = "browser.exe",
            Mode = ProxyMode.Proxy,
            ProxyChainId = "chain-1"
        });
        var chainResult = SingBoxConfigBuilder.Build(withChain);
        Assert.False(chainResult.Success);
        Assert.Contains("not supported", chainResult.Error, StringComparison.OrdinalIgnoreCase);

        var withInvalidCidr = BaseConfig();
        withInvalidCidr.Rules.Add(new ProxyRule
        {
            ExeName = "browser.exe",
            Mode = ProxyMode.Direct,
            TargetIPs = "10.0.0.0/33"
        });
        var cidrResult = SingBoxConfigBuilder.Build(withInvalidCidr);
        Assert.False(cidrResult.Success);
        Assert.Contains("invalid IP/CIDR", cidrResult.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("192.168.1.10")]
    [InlineData("8.8.8.8")]
    public void Build_RejectsNonLiteralOrNonLoopbackProxyHosts(string host)
    {
        var config = BaseConfig();
        config.ProxyServers[0].Host = host;

        var result = SingBoxConfigBuilder.Build(config);

        Assert.False(result.Success);
        Assert.Contains("loopback", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.2")]
    [InlineData("::1")]
    public void Build_AcceptsLiteralLoopbackProxyHosts(string host)
    {
        var config = BaseConfig();
        config.ProxyServers[0].Host = host;

        var result = SingBoxConfigBuilder.Build(config);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void Build_EmitsCanonicalIpv6LoopbackAddress()
    {
        var config = BaseConfig();
        config.ProxyServers[0].Host = "0:0:0:0:0:0:0:1";

        var result = SingBoxConfigBuilder.Build(config);

        Assert.True(result.Success, result.Error);
        var root = JObject.Parse(result.ConfigJson!);
        Assert.Equal("::1", root["outbounds"]![1]!["server"]!.Value<string>());
    }

    private static AppConfig BaseConfig() => new()
    {
        GlobalMode = GlobalMode.DirectAll,
        ProxyServers =
        [
            new ProxyServer
            {
                Id = "proxy-1",
                Name = "Local SOCKS",
                ProxyType = ProxyType.Socks5,
                Host = "127.0.0.1",
                Port = 10808,
                Enabled = true
            }
        ]
    };
}
