using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonRequired = System.Text.Json.Serialization.JsonRequiredAttribute;

namespace ProxyManager.Standalone;

public sealed record AiPolicyExplainRequest(string Model, PolicyDisclosure Disclosure);

public interface IAiPolicyExplainer
{
    AiProviderKind Kind { get; }
    bool IsAvailable { get; }
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken cancellationToken = default);
    Task<AiPolicyExplanation> ExplainPolicyAsync(
        AiPolicyExplainRequest request,
        CancellationToken cancellationToken = default);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiPolicyExplanation
{
    [JsonPropertyName("summary")]
    [JsonRequired]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("priorities")]
    [JsonRequired]
    public List<AiPolicyPriority> Priorities { get; set; } = [];

    [JsonPropertyName("caveats")]
    [JsonRequired]
    public List<string> Caveats { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AiPolicyPriority
{
    [JsonPropertyName("findingCode")]
    [JsonRequired]
    public string FindingCode { get; set; } = string.Empty;

    [JsonPropertyName("explanation")]
    [JsonRequired]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("safeNextStep")]
    [JsonRequired]
    public string SafeNextStep { get; set; } = string.Empty;

    [JsonPropertyName("confidence")]
    [JsonRequired]
    public double Confidence { get; set; }
}

internal static class AiPolicyContract
{
    public const int MaxPriorities = 8;
    public const int MaxCaveats = 8;
    public const int MaxResponseChars = 48_000;

    public const string SystemInstructions = """
        You explain a deterministic, de-identified network-routing policy report. The input contains only
        aggregate counts and finding codes/categories. Never infer or invent process names, domains, IPs,
        ports, paths, proxy endpoints, credentials, logs, rule identifiers, or commands. Do not propose
        automatic activation or direct configuration changes. Prioritize only supplied finding codes and
        return plain text data matching the schema. Safe next steps must require human review.
        """;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12
    };

    public static JObject CreateSchema()
    {
        static JObject StringProperty(int maxLength) => new()
        {
            ["type"] = "string",
            ["maxLength"] = maxLength
        };

        return new JObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["properties"] = new JObject
            {
                ["summary"] = StringProperty(800),
                ["priorities"] = new JObject
                {
                    ["type"] = "array",
                    ["maxItems"] = MaxPriorities,
                    ["items"] = new JObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JObject
                        {
                            ["findingCode"] = StringProperty(16),
                            ["explanation"] = StringProperty(500),
                            ["safeNextStep"] = StringProperty(500),
                            ["confidence"] = new JObject
                            {
                                ["type"] = "number",
                                ["minimum"] = 0,
                                ["maximum"] = 1
                            }
                        },
                        ["required"] = new JArray("findingCode", "explanation", "safeNextStep", "confidence")
                    }
                },
                ["caveats"] = new JObject
                {
                    ["type"] = "array",
                    ["maxItems"] = MaxCaveats,
                    ["items"] = StringProperty(400)
                }
            },
            ["required"] = new JArray("summary", "priorities", "caveats")
        };
    }

    public static string CreateInput(PolicyDisclosure disclosure)
    {
        ValidateDisclosure(disclosure);
        var payload = new JObject
        {
            ["policySummary"] = new JObject
            {
                ["globalMode"] = disclosure.GlobalMode.ToString(),
                ["activeRuleCount"] = disclosure.ActiveRuleCount,
                ["disabledRuleCount"] = disclosure.DisabledRuleCount,
                ["proxyRuleCount"] = disclosure.ProxyRuleCount,
                ["directRuleCount"] = disclosure.DirectRuleCount,
                ["blockRuleCount"] = disclosure.BlockRuleCount,
                ["enabledProxyCount"] = disclosure.EnabledProxyCount,
                ["omittedFindingCount"] = disclosure.OmittedFindingCount
            },
            ["findings"] = new JArray(disclosure.Findings.Select(finding => new JObject
            {
                ["code"] = finding.Code,
                ["kind"] = finding.Kind.ToString(),
                ["severity"] = finding.Severity.ToString(),
                ["relation"] = finding.Relation.ToString(),
                ["affectedRuleCount"] = finding.AffectedRuleCount
            }))
        };
        return payload.ToString(Formatting.None);
    }

    public static AiPolicyExplanation ParseExplanation(string json, PolicyDisclosure disclosure)
    {
        ValidateDisclosure(disclosure);
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxResponseChars)
            throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "AI 策略解读为空或超过安全大小限制。");

        try
        {
            var result = System.Text.Json.JsonSerializer.Deserialize<AiPolicyExplanation>(json, StrictOptions)
                ?? throw new System.Text.Json.JsonException("Empty result.");
            if (result.Summary == null || result.Priorities == null || result.Caveats == null ||
                result.Caveats.Any(caveat => caveat == null) ||
                result.Priorities.Any(priority => priority == null || priority.FindingCode == null ||
                    priority.Explanation == null || priority.SafeNextStep == null))
            {
                throw new System.Text.Json.JsonException("Null fields are not allowed.");
            }

            var allowedCodes = disclosure.Findings.Select(finding => finding.Code).ToHashSet(StringComparer.Ordinal);
            if (result.Summary.Length > 800 || result.Priorities.Count > MaxPriorities ||
                result.Caveats.Count > MaxCaveats || result.Caveats.Any(caveat => caveat.Length > 400) ||
                result.Priorities.Any(priority => priority.Explanation.Length > 500 ||
                    priority.SafeNextStep.Length > 500 ||
                    double.IsNaN(priority.Confidence) || double.IsInfinity(priority.Confidence) ||
                    priority.Confidence is < 0 or > 1 ||
                    !allowedCodes.Contains(priority.FindingCode)) ||
                result.Priorities.Select(priority => priority.FindingCode).Distinct(StringComparer.Ordinal).Count() !=
                    result.Priorities.Count)
            {
                throw new System.Text.Json.JsonException("Policy explanation violates local bounds or references.");
            }

            return result;
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "AI 策略解读不符合严格格式或引用了未知发现。", ex);
        }
    }

    public static void ValidateRequest(AiPolicyExplainRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model) || request.Model.Length > 128)
            throw new AiProviderException(AiProviderErrorKind.ModelNotFound, "请选择有效的 AI 模型。");
        ValidateDisclosure(request.Disclosure);
    }

    private static void ValidateDisclosure(PolicyDisclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        var counts = new[]
        {
            disclosure.ActiveRuleCount,
            disclosure.DisabledRuleCount,
            disclosure.ProxyRuleCount,
            disclosure.DirectRuleCount,
            disclosure.BlockRuleCount,
            disclosure.EnabledProxyCount,
            disclosure.OmittedFindingCount
        };
        if (counts.Any(count => count < 0) || disclosure.Findings.Count > PolicyDisclosure.MaxFindings ||
            disclosure.Findings.Any(finding => finding.AffectedRuleCount is < 0 or > 2 ||
                finding.Code.Length != 7 || !finding.Code.StartsWith("PIR-", StringComparison.Ordinal) ||
                !int.TryParse(finding.Code.AsSpan(4), out _)) ||
            disclosure.Findings.Select(finding => finding.Code).Distinct(StringComparer.Ordinal).Count() !=
                disclosure.Findings.Count)
        {
            throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "本地策略披露不符合安全结构。");
        }
    }
}

public sealed partial class OpenAiRuleProvider
{
    public async Task<AiPolicyExplanation> ExplainPolicyAsync(
        AiPolicyExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        AiPolicyContract.ValidateRequest(request);
        if (!SupportedModels.Contains(request.Model, StringComparer.Ordinal))
            throw new AiProviderException(AiProviderErrorKind.ModelNotFound, "所选 OpenAI 模型不在此版本的允许列表中。");

        var apiKey = _apiKeyProvider();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new AiProviderException(
                AiProviderErrorKind.NotConfigured,
                "未检测到 OPENAI_API_KEY。请设置当前用户环境变量后重新打开应用。");

        var payload = new JObject
        {
            ["model"] = request.Model,
            ["store"] = false,
            ["max_output_tokens"] = 1_800,
            ["instructions"] = AiPolicyContract.SystemInstructions,
            ["input"] = AiPolicyContract.CreateInput(request.Disclosure),
            ["text"] = new JObject
            {
                ["format"] = new JObject
                {
                    ["type"] = "json_schema",
                    ["name"] = "intent_route_policy_explanation",
                    ["strict"] = true,
                    ["schema"] = AiPolicyContract.CreateSchema()
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiProviderErrorKind.Timeout, "OpenAI 策略解读请求超时。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiProviderErrorKind.Unavailable, "无法连接 OpenAI。", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode) throw MapOpenAiStatus(response.StatusCode);
            var responseJson = await AiHttp.ReadBoundedStringAsync(
                response.Content,
                AiPolicyContract.MaxResponseChars,
                timeout.Token);
            try
            {
                var root = JObject.Parse(responseJson);
                var outputText = root["output"]?
                    .OfType<JObject>()
                    .SelectMany(item => item["content"]?.OfType<JObject>() ?? [])
                    .FirstOrDefault(content => string.Equals(
                        (string?)content["type"],
                        "output_text",
                        StringComparison.Ordinal))?["text"]
                    ?.Value<string>();
                if (string.IsNullOrWhiteSpace(outputText))
                    throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "OpenAI 未返回策略解读。");
                return AiPolicyContract.ParseExplanation(outputText, request.Disclosure);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "OpenAI 返回了无法解析的策略解读。", ex);
            }
        }
    }
}

public sealed partial class OllamaRuleProvider
{
    public async Task<AiPolicyExplanation> ExplainPolicyAsync(
        AiPolicyExplainRequest request,
        CancellationToken cancellationToken = default)
    {
        AiPolicyContract.ValidateRequest(request);
        if (!IsSafeModelName(request.Model))
            throw new AiProviderException(AiProviderErrorKind.ModelNotFound, "Ollama 模型名称无效。");

        var payload = new JObject
        {
            ["model"] = request.Model,
            ["stream"] = false,
            ["think"] = false,
            ["messages"] = new JArray
            {
                new JObject { ["role"] = "system", ["content"] = AiPolicyContract.SystemInstructions },
                new JObject { ["role"] = "user", ["content"] = AiPolicyContract.CreateInput(request.Disclosure) }
            },
            ["format"] = AiPolicyContract.CreateSchema(),
            ["options"] = new JObject { ["temperature"] = 0 }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "api/chat"))
        {
            Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json")
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));

        HttpResponseMessage response;
        try
        {
            response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AiProviderException(AiProviderErrorKind.Timeout, "Ollama 策略解读超时。", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new AiProviderException(AiProviderErrorKind.Unavailable, "无法连接本机 Ollama。", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new AiProviderException(AiProviderErrorKind.ModelNotFound, "所选 Ollama 模型未安装。");
            if (!response.IsSuccessStatusCode)
                throw new AiProviderException(AiProviderErrorKind.Unavailable, "Ollama 未能生成策略解读。");

            var responseJson = await AiHttp.ReadBoundedStringAsync(
                response.Content,
                AiPolicyContract.MaxResponseChars,
                timeout.Token);
            try
            {
                var root = JObject.Parse(responseJson);
                var content = (string?)root["message"]?["content"];
                if (string.IsNullOrWhiteSpace(content))
                    throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "Ollama 未返回策略解读。");
                return AiPolicyContract.ParseExplanation(content, request.Disclosure);
            }
            catch (Newtonsoft.Json.JsonException ex)
            {
                throw new AiProviderException(AiProviderErrorKind.InvalidResponse, "Ollama 返回了无法解析的策略解读。", ex);
            }
        }
    }
}
