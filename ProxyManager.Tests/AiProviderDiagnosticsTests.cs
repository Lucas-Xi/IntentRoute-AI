using System.Net;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ProxyManager.Standalone;
using Xunit;

namespace ProxyManager.Tests;

public sealed class AiProviderDiagnosticsTests : IDisposable
{
    private readonly HttpClient _client;

    public AiProviderDiagnosticsTests()
    {
        _client = new HttpClient(new RecordingHandler(_ =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("本测试禁止任何网络请求"))));
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task CheckAsync_OpenAiIsReadyWithoutOpeningAnyConnection()
    {
        var keyCanary = Guid.NewGuid().ToString("N");
        var provider = new OpenAiRuleProvider(_client, () => keyCanary);

        var check = await AiProviderDiagnostics.CheckAsync(provider, OpenAiRuleProvider.DefaultModel);

        Assert.Equal(AiProviderKind.OpenAI, check.Kind);
        Assert.Equal(AiProviderHealthState.Ready, check.State);
        Assert.All(check.Details, detail => Assert.DoesNotContain(keyCanary, detail, StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_ReportsOpenAiNotConfiguredWhenKeyIsMissing()
    {
        var provider = new OpenAiRuleProvider(_client, () => null);

        var check = await AiProviderDiagnostics.CheckAsync(provider, OpenAiRuleProvider.DefaultModel);

        Assert.Equal(AiProviderHealthState.NotConfigured, check.State);
        Assert.Contains(check.Details, detail => detail.Contains("OPENAI_API_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_ReportsOpenAiMisconfiguredForNonAllowlistedModel()
    {
        var provider = new OpenAiRuleProvider(_client, () => Guid.NewGuid().ToString("N"));

        var check = await AiProviderDiagnostics.CheckAsync(provider, "gpt-not-a-real-model");

        Assert.Equal(AiProviderHealthState.Misconfigured, check.State);
    }

    [Fact]
    public async Task CheckAsync_ReportsOllamaReadyWhenSelectedModelIsInstalled()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(TagsResponse("llama3.2:latest"))));
        var provider = new OllamaRuleProvider(client, OllamaRuleProvider.DefaultBaseUri);

        var check = await AiProviderDiagnostics.CheckAsync(provider, "llama3.2:latest");

        Assert.Equal(AiProviderHealthState.Ready, check.State);
        Assert.Contains(check.Details, detail => detail.Contains("1 个模型", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_ReportsOllamaMisconfiguredWhenSelectedModelIsNotInstalled()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(TagsResponse("llama3.2:latest"))));
        var provider = new OllamaRuleProvider(client, OllamaRuleProvider.DefaultBaseUri);

        var check = await AiProviderDiagnostics.CheckAsync(provider, "qwen3:latest");

        Assert.Equal(AiProviderHealthState.Misconfigured, check.State);
        Assert.Contains(check.Details, detail => detail.Contains("不在其中", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_ReportsOllamaMisconfiguredWhenNoModelIsSelected()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            Task.FromResult(TagsResponse("llama3.2:latest"))));
        var provider = new OllamaRuleProvider(client, OllamaRuleProvider.DefaultBaseUri);

        var check = await AiProviderDiagnostics.CheckAsync(provider, " ");

        Assert.Equal(AiProviderHealthState.Misconfigured, check.State);
    }

    [Fact]
    public async Task CheckAsync_ReportsOllamaUnreachableWhenLocalServiceIsDown()
    {
        using var client = new HttpClient(new RecordingHandler(_ =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused"))));
        var provider = new OllamaRuleProvider(client, OllamaRuleProvider.DefaultBaseUri);

        var check = await AiProviderDiagnostics.CheckAsync(provider, "llama3.2:latest");

        Assert.Equal(AiProviderHealthState.Unreachable, check.State);
        Assert.NotEmpty(check.Details);
    }

    private static HttpResponseMessage TagsResponse(params string[] modelNames)
    {
        var json = new JObject
        {
            ["models"] = new JArray(modelNames.Select(name => new JObject { ["name"] = name }))
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => handler(request);
    }
}
