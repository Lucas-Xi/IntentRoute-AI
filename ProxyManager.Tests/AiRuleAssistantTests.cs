using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class AiRuleAssistantTests
{
    [Fact]
    public void StrictParser_RejectsUnexpectedProperties()
    {
        var json = ValidSuggestionJson();
        var root = JObject.Parse(json);
        ((JObject)root["rules"]![0]!).Add("execute", "powershell.exe");

        var error = Assert.Throws<AiProviderException>(() =>
            AiRuleContract.ParseSuggestion(root.ToString()));

        Assert.Equal(AiProviderErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public void StrictParser_RejectsMissingOrNullRequiredFields()
    {
        var missing = JObject.Parse(ValidSuggestionJson());
        ((JObject)missing["rules"]![0]!).Remove("protocol");
        var withNull = JObject.Parse(ValidSuggestionJson());
        withNull["summary"] = JValue.CreateNull();

        Assert.Throws<AiProviderException>(() => AiRuleContract.ParseSuggestion(missing.ToString()));
        Assert.Throws<AiProviderException>(() => AiRuleContract.ParseSuggestion(withNull.ToString()));
    }

    [Fact]
    public void StrictParser_RejectsInvalidJson()
    {
        var error = Assert.Throws<AiProviderException>(() =>
            AiRuleContract.ParseSuggestion("{not-json"));

        Assert.Equal(AiProviderErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("protocol", "ICMP")]
    [InlineData("action", "Execute")]
    public void StrictParser_RejectsUnsupportedEnumValues(string property, string value)
    {
        var root = JObject.Parse(ValidSuggestionJson());
        ((JObject)root["rules"]![0]!)[property] = value;

        var error = Assert.Throws<AiProviderException>(() =>
            AiRuleContract.ParseSuggestion(root.ToString()));

        Assert.Equal(AiProviderErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task OpenAiProvider_UsesStoreFalseStrictSchemaAndFindsOutputTextAnywhere()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            var responseBody = new JObject
            {
                ["output"] = new JArray
                {
                    new JObject { ["type"] = "reasoning", ["content"] = new JArray() },
                    new JObject
                    {
                        ["type"] = "message",
                        ["content"] = new JArray
                        {
                            new JObject
                            {
                                ["type"] = "output_text",
                                ["text"] = ValidSuggestionJson()
                            }
                        }
                    }
                }
            };
            return JsonResponse(responseBody.ToString());
        });
        using var client = new HttpClient(handler);
        using var provider = new OpenAiRuleProvider(client, () => "test-key");

        var result = await provider.GenerateDraftAsync(
            new AiRuleRequest("Route Chrome to GitHub", OpenAiRuleProvider.DefaultModel));

        Assert.Single(result.Rules);
        var payload = JObject.Parse(requestBody!);
        Assert.False((bool)payload["store"]!);
        Assert.Equal("json_schema", (string?)payload["text"]?["format"]?["type"]);
        Assert.True((bool)payload["text"]?["format"]?["strict"]!);
        Assert.False((bool)payload["text"]?["format"]?["schema"]?["additionalProperties"]!);
        Assert.Equal("Route Chrome to GitHub", (string?)payload["input"]);
        Assert.DoesNotContain("test-key", requestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ProxyServers", requestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiProvider_DoesNotExposeKeyWhenAuthenticationFails()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var client = new HttpClient(handler);
        using var provider = new OpenAiRuleProvider(client, () => "secret-test-key");

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateDraftAsync(
            new AiRuleRequest("Route Chrome to GitHub", OpenAiRuleProvider.DefaultModel)));

        Assert.Equal(AiProviderErrorKind.Authentication, error.Kind);
        Assert.DoesNotContain("secret-test-key", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiProvider_MapsRateLimitWithoutReturningRemoteBody()
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("remote-sensitive-detail")
        };
        var handler = new RecordingHandler(_ => Task.FromResult(response));
        using var client = new HttpClient(handler);
        using var provider = new OpenAiRuleProvider(client, () => "test-key");

        var error = await Assert.ThrowsAsync<AiProviderException>(() => provider.GenerateDraftAsync(
            new AiRuleRequest("Route Chrome to GitHub", OpenAiRuleProvider.DefaultModel)));

        Assert.Equal(AiProviderErrorKind.RateLimited, error.Kind);
        Assert.DoesNotContain("remote-sensitive-detail", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:11434/")]
    [InlineData("http://127.0.0.2:11434/")]
    [InlineData("http://127.1.2.3:11434/")]
    [InlineData("http://127.255.255.254:11434/")]
    [InlineData("http://[::ffff:127.0.0.1]:11434/")]
    [InlineData("http://192.168.1.10:11434/")]
    [InlineData("http://example.com:11434/")]
    [InlineData("http://user:password@127.0.0.1:11434/")]
    [InlineData("http://127.0.0.1:11434/?model=qwen")]
    [InlineData("http://127.0.0.1:11434/#fragment")]
    public void OllamaProvider_RejectsNonLoopbackOrCredentialedEndpoints(string endpoint)
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaRuleProvider.ValidateLoopbackBaseUri(new Uri(endpoint)));
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434/")]
    [InlineData("http://[::1]:11434/")]
    public void OllamaProvider_AcceptsHttpLoopbackEndpoints(string endpoint)
    {
        var result = OllamaRuleProvider.ValidateLoopbackBaseUri(new Uri(endpoint));
        Assert.Equal("http", result.Scheme);
    }

    [Fact]
    public void OllamaProvider_RejectsHostnamesEvenWhenNamedLocalhost()
    {
        Assert.Throws<ArgumentException>(() =>
            OllamaRuleProvider.ValidateLoopbackBaseUri(new Uri("http://localhost:11434/")));
    }

    [Fact]
    public async Task OllamaProvider_ListsInstalledModels()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(JsonResponse(
            """{"models":[{"name":"qwen3:8b"},{"model":"gpt-oss:20b"}]}""")));
        using var client = new HttpClient(handler);
        using var provider = new OllamaRuleProvider(client, new Uri("http://127.0.0.1:11434/"));

        var models = await provider.ListModelsAsync();

        Assert.Equal(["gpt-oss:20b", "qwen3:8b"], models);
    }

    [Fact]
    public async Task OllamaProvider_ReturnsEmptyListWhenNoModelsAreInstalled()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(JsonResponse("""{"models":[]}""")));
        using var client = new HttpClient(handler);
        using var provider = new OllamaRuleProvider(client, new Uri("http://127.0.0.1:11434/"));

        var models = await provider.ListModelsAsync();

        Assert.Empty(models);
    }

    [Fact]
    public async Task OllamaProvider_UsesNonStreamingSharedSchema()
    {
        string? requestBody = null;
        var handler = new RecordingHandler(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse(new JObject
            {
                ["message"] = new JObject { ["role"] = "assistant", ["content"] = ValidSuggestionJson() },
                ["done"] = true
            }.ToString());
        });
        using var client = new HttpClient(handler);
        using var provider = new OllamaRuleProvider(client, new Uri("http://127.0.0.1:11434/"));

        var result = await provider.GenerateDraftAsync(new AiRuleRequest("Route Chrome to GitHub", "qwen3:8b"));

        Assert.Single(result.Rules);
        var payload = JObject.Parse(requestBody!);
        Assert.False((bool)payload["stream"]!);
        Assert.Equal("qwen3:8b", (string?)payload["model"]);
        Assert.False((bool)payload["format"]?["additionalProperties"]!);
        Assert.Equal(0, (double)payload["options"]?["temperature"]!);
    }

    [Fact]
    public void Validator_MapsValidDraftsToDisabledRulesAndDryRunsBuilder()
    {
        var suggestion = AiRuleContract.ParseSuggestion(ValidSuggestionJson());
        var config = ConfigWithEnabledProxy();

        var result = AiRuleDraftValidator.Validate(suggestion, config);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var rule = Assert.Single(result.Rules);
        Assert.False(rule.IsEnabled);
        Assert.Equal("chrome.exe", rule.ExeName);
        Assert.Equal("*.github.com, github.com", rule.TargetHosts);
        Assert.Equal(ProxyMode.Proxy, rule.Mode);
    }

    [Fact]
    public void Validator_RejectsPathLikeProcessNames()
    {
        var suggestion = AiRuleContract.ParseSuggestion(ValidSuggestionJson(processName: "C:\\Windows\\cmd.exe"));

        var result = AiRuleDraftValidator.Validate(suggestion, ConfigWithEnabledProxy());

        Assert.False(result.Success);
        // The process-name rule is the only validation message that mentions ".exe".
        Assert.Contains(result.Errors, error => error.Contains(".exe", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("targetHosts", "bad..example.com")]
    [InlineData("targetIps", "999.1.1.1/99")]
    [InlineData("targetPorts", "0-70000")]
    public void Validator_RejectsInvalidNetworkFilters(string property, string value)
    {
        var root = JObject.Parse(ValidSuggestionJson());
        ((JObject)root["rules"]![0]!)[property] = value;
        var suggestion = AiRuleContract.ParseSuggestion(root.ToString());

        var result = AiRuleDraftValidator.Validate(suggestion, ConfigWithEnabledProxy());

        Assert.False(result.Success);
    }

    [Fact]
    public void Validator_RejectsDuplicateRulesWithinSuggestion()
    {
        var root = JObject.Parse(ValidSuggestionJson());
        var rules = (JArray)root["rules"]!;
        rules.Add(rules[0]!.DeepClone());
        var suggestion = AiRuleContract.ParseSuggestion(root.ToString());

        var result = AiRuleDraftValidator.Validate(suggestion, ConfigWithEnabledProxy());

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("本次草案", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsExcessiveRuleCount()
    {
        var root = JObject.Parse(ValidSuggestionJson());
        var rules = (JArray)root["rules"]!;
        while (rules.Count <= AiRuleContract.MaxRules)
            rules.Add(rules[0]!.DeepClone());
        var suggestion = AiRuleContract.ParseSuggestion(root.ToString());

        var result = AiRuleDraftValidator.Validate(suggestion, ConfigWithEnabledProxy());

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("1–", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsProxyRulesWithoutEnabledProxy()
    {
        var suggestion = AiRuleContract.ParseSuggestion(ValidSuggestionJson());
        var config = new AppConfig { ProxyServers = [] };

        var result = AiRuleDraftValidator.Validate(suggestion, config);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("已启用", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsDuplicateExistingRules()
    {
        var suggestion = AiRuleContract.ParseSuggestion(ValidSuggestionJson());
        var config = ConfigWithEnabledProxy();
        config.Rules.Add(new ProxyRule
        {
            ExeName = "chrome.exe",
            TargetHosts = "github.com, *.github.com",
            Protocol = "Both",
            Mode = ProxyMode.Proxy
        });

        var result = AiRuleDraftValidator.Validate(suggestion, config);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("现有规则重复", StringComparison.Ordinal));
    }

    [Fact]
    public void Acceptance_PersistsAllRulesDisabledInOneConfiguration()
    {
        var directory = CreateTempDirectory();
        try
        {
            using var service = new AppService(directory, startMonitor: false, applyOnStart: false);
            var rules = AiRuleDraftValidator.Validate(
                AiRuleContract.ParseSuggestion(ValidSuggestionJson()),
                service.Config).Rules;
            var statuses = new List<string>();
            service.StatusChanged += statuses.Add;

            service.AcceptDisabledAiRules(rules);

            var persisted = AppConfigStore.Deserialize(File.ReadAllText(service.ConfigPath));
            var rule = Assert.Single(persisted.Rules);
            Assert.False(rule.IsEnabled);
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.Empty(statuses);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Validate_RejectsEditedDraftFieldsAndRecoversWhenFixed()
    {
        var config = ConfigWithEnabledProxy();
        var suggestion = System.Text.Json.JsonSerializer.Deserialize<AiRuleSuggestion>(ValidSuggestionJson())!;
        var original = AiRuleDraftValidator.Validate(suggestion, config);
        Assert.True(original.Success, string.Join("；", original.Errors));

        suggestion.Rules[0].ProcessName = "not a valid name";
        var edited = AiRuleDraftValidator.Validate(suggestion, config);
        Assert.False(edited.Success);
        // The process-name rule is the only validation message that mentions ".exe".
        Assert.Contains(edited.Errors, error => error.Contains(".exe", StringComparison.Ordinal));

        suggestion.Rules[0].ProcessName = "chrome.exe";
        var restored = AiRuleDraftValidator.Validate(suggestion, config);
        Assert.True(restored.Success, string.Join("；", restored.Errors));
    }

    private static AppConfig ConfigWithEnabledProxy() => new()
    {
        ProxyServers =
        [
            new ProxyServer
            {
                Name = "Local SOCKS5",
                Host = "127.0.0.1",
                Port = 10808,
                ProxyType = ProxyType.Socks5,
                Enabled = true
            }
        ]
    };

    private static string ValidSuggestionJson(string processName = "chrome.exe") => new JObject
    {
        ["summary"] = "Route Chrome GitHub traffic through the proxy.",
        ["rules"] = new JArray
        {
            new JObject
            {
                ["processName"] = processName,
                ["targetHosts"] = "github.com, *.github.com",
                ["targetIps"] = string.Empty,
                ["targetPorts"] = string.Empty,
                ["protocol"] = "Both",
                ["action"] = "Proxy",
                ["rationale"] = "The user requested this route.",
                ["confidence"] = 0.9
            }
        },
        ["warnings"] = new JArray("Review generated domains before enabling the rule.")
    }.ToString();

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "IntentRouteAI.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
