namespace ProxyManager.Standalone;

using Strings = ProxyManager.Standalone.Localization.Strings;

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
                    [Strings.DiagOpenAiNoKeyEnv]);
            }

            var allowlist = await provider.ListModelsAsync(cancellationToken);
            var trimmed = selectedModel?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return new AiProviderHealthCheck(
                    AiProviderKind.OpenAI,
                    AiProviderHealthState.Misconfigured,
                    [
                        Strings.DiagKeyPresentNoRequest,
                        Strings.DiagOpenAiNoModelPicked
                    ]);
            }

            if (!allowlist.Contains(trimmed, StringComparer.Ordinal))
            {
                return new AiProviderHealthCheck(
                    AiProviderKind.OpenAI,
                    AiProviderHealthState.Misconfigured,
                    [
                        Strings.DiagKeyPresentNoRequest,
                        Strings.DiagOpenAiPickModel
                    ]);
            }

            return new AiProviderHealthCheck(
                AiProviderKind.OpenAI,
                AiProviderHealthState.Ready,
                [
                    Strings.DiagKeyPresentNoRequest,
                    string.Format(Strings.DiagOpenAiModelAllowlistedFormat, trimmed)
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
                    Strings.DiagOllamaLoopback,
                    string.Format(Strings.DiagOllamaModelsNoPickFormat, models.Count)
                ]);
        }

        if (!models.Contains(trimmedOllama, StringComparer.Ordinal))
        {
            return new AiProviderHealthCheck(
                AiProviderKind.Ollama,
                AiProviderHealthState.Misconfigured,
                [
                    Strings.DiagOllamaLoopback,
                    string.Format(Strings.DiagOllamaModelMissingFormat, models.Count)
                ]);
        }

        return new AiProviderHealthCheck(
            AiProviderKind.Ollama,
            AiProviderHealthState.Ready,
            [
                Strings.DiagOllamaLoopback,
                string.Format(Strings.DiagOllamaModelPresentFormat, models.Count)
            ]);
    }
}
