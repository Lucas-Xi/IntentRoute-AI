namespace ProxyManager.Standalone;

public enum AiProviderHealthState
{
    Ready,
    NotConfigured,
    Misconfigured,
    Unreachable
}

public sealed record AiProviderHealthCheck(
    AiProviderKind Kind,
    AiProviderHealthState State,
    IReadOnlyList<string> Details);

public static class AiProviderDiagnostics
{
    // Credential-free by construction: the OpenAI branch reports only whether the local
    // environment variable is present and never sends a request; the Ollama branch reuses
    // the literal-loopback model listing, which involves no credentials by design.
    public static async Task<AiProviderHealthCheck> CheckAsync(
        IAiRuleProvider provider,
        string? selectedModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();

        if (provider.Kind == AiProviderKind.OpenAI)
        {
            if (!provider.IsAvailable)
            {
                return new AiProviderHealthCheck(
                    AiProviderKind.OpenAI,
                    AiProviderHealthState.NotConfigured,
                    ["未检测到 OPENAI_API_KEY 环境变量。请设置当前用户环境变量后重新打开应用。"]);
            }

            var allowlist = await provider.ListModelsAsync(cancellationToken);
            var trimmed = selectedModel?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return new AiProviderHealthCheck(
                    AiProviderKind.OpenAI,
                    AiProviderHealthState.Misconfigured,
                    [
                        "OPENAI_API_KEY 已配置（本检测只报告存在性，不显示密钥内容，也不发送网络请求）。",
                        "尚未选择模型。请打开 AI 页加载并选择允许列表中的模型。"
                    ]);
            }

            if (!allowlist.Contains(trimmed, StringComparer.Ordinal))
            {
                return new AiProviderHealthCheck(
                    AiProviderKind.OpenAI,
                    AiProviderHealthState.Misconfigured,
                    [
                        "OPENAI_API_KEY 已配置（本检测只报告存在性，不显示密钥内容，也不发送网络请求）。",
                        "所选模型不在本版本的允许列表中，请重新选择模型。"
                    ]);
            }

            return new AiProviderHealthCheck(
                AiProviderKind.OpenAI,
                AiProviderHealthState.Ready,
                [
                    "OPENAI_API_KEY 已配置（本检测只报告存在性，不显示密钥内容，也不发送网络请求）。",
                    $"所选模型 {trimmed} 在允许列表中。"
                ]);
        }

        IReadOnlyList<string> models;
        try
        {
            models = await provider.ListModelsAsync(cancellationToken);
        }
        catch (AiProviderException ex)
        {
            return new AiProviderHealthCheck(
                AiProviderKind.Ollama,
                AiProviderHealthState.Unreachable,
                [ex.Message]);
        }

        var trimmedOllama = selectedModel?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedOllama))
        {
            return new AiProviderHealthCheck(
                AiProviderKind.Ollama,
                AiProviderHealthState.Misconfigured,
                [
                    "Ollama 本地服务可达（仅访问字面量环回地址，无凭据参与）。",
                    $"已安装 {models.Count} 个模型，但尚未选择模型。"
                ]);
        }

        if (!models.Contains(trimmedOllama, StringComparer.Ordinal))
        {
            return new AiProviderHealthCheck(
                AiProviderKind.Ollama,
                AiProviderHealthState.Misconfigured,
                [
                    "Ollama 本地服务可达（仅访问字面量环回地址，无凭据参与）。",
                    $"已安装 {models.Count} 个模型，但所选模型不在其中。"
                ]);
        }

        return new AiProviderHealthCheck(
            AiProviderKind.Ollama,
            AiProviderHealthState.Ready,
            [
                "Ollama 本地服务可达（仅访问字面量环回地址，无凭据参与）。",
                $"已安装 {models.Count} 个模型，所选模型已在本机安装。"
            ]);
    }
}
