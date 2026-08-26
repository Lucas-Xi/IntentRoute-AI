using System.IO;
using Newtonsoft.Json;

namespace ProxyManager.Standalone;

/// <summary>
/// Owns the active configuration. Callers can inspect detached snapshots, while every
/// mutation is prepared and validated as a complete candidate before it is persisted
/// and published.
/// </summary>
internal sealed class ConfigurationWorkspace
{
    private readonly string _configPath;
    private AppConfig _activeConfiguration;

    private ConfigurationWorkspace(
        string configPath,
        AppConfig activeConfiguration,
        bool isWritable,
        string? recoveryBackupPath,
        string? error)
    {
        _configPath = configPath;
        _activeConfiguration = activeConfiguration;
        IsWritable = isWritable;
        RecoveryBackupPath = recoveryBackupPath;
        Error = error;
    }

    public bool IsWritable { get; private set; }
    public string? RecoveryBackupPath { get; private set; }
    public string? Error { get; private set; }

    public static ConfigurationWorkspace Load(string configPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configPath);
        var load = AppConfigStore.LoadPreservingInvalidFile(configPath);
        if (load.Status == AppConfigLoadStatus.Unusable)
        {
            return new ConfigurationWorkspace(
                configPath,
                new AppConfig(),
                isWritable: false,
                load.BackupPath,
                load.Error);
        }

        var active = load.Config ?? new AppConfig();
        Normalize(active, addDefaultProxyWhenEmpty: true);
        return new ConfigurationWorkspace(
            configPath,
            active,
            isWritable: true,
            recoveryBackupPath: null,
            error: null);
    }

    public AppConfig Snapshot() => Clone(_activeConfiguration);

    public AppConfig Commit(Action<AppConfig> mutate)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(mutate);

        var candidate = Snapshot();
        mutate(candidate);
        return PersistAndPublish(candidate, addDefaultProxyWhenEmpty: false);
    }

    public AppConfig Replace(AppConfig candidate, bool addDefaultProxyWhenEmpty = true)
    {
        EnsureWritable();
        ArgumentNullException.ThrowIfNull(candidate);
        return PersistAndPublish(candidate, addDefaultProxyWhenEmpty);
    }

    public AppConfig ResetProtectedConfiguration()
    {
        if (IsWritable)
            throw new InvalidOperationException("Configuration recovery is not required.");
        EnsureRecoveryCopyAvailable();

        var replacement = new AppConfig();
        Normalize(replacement, addDefaultProxyWhenEmpty: true);
        var published = PersistAndPublishProtected(replacement, addDefaultProxyWhenEmpty: true);
        MarkRecovered();
        return published;
    }

    public AppConfig RecoverFromFile(string sourcePath)
    {
        if (IsWritable)
            throw new InvalidOperationException("Configuration recovery is not required.");
        EnsureRecoveryCopyAvailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        var recovered = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(sourcePath));
        var published = PersistAndPublishProtected(recovered, addDefaultProxyWhenEmpty: true);
        MarkRecovered();
        return published;
    }

    public void SaveCopy(string destinationPath, bool redactPasswords)
    {
        EnsureWritable();
        AppConfigStore.SaveAtomic(destinationPath, Snapshot(), redactPasswords);
    }

    private AppConfig PersistAndPublish(AppConfig candidate, bool addDefaultProxyWhenEmpty)
    {
        EnsureWritable();
        return PersistAndPublishProtected(candidate, addDefaultProxyWhenEmpty);
    }

    private AppConfig PersistAndPublishProtected(AppConfig candidate, bool addDefaultProxyWhenEmpty)
    {
        // Detach caller-owned objects before validation and publication. This prevents a
        // caller from retaining a mutable reference into the active configuration.
        var published = Clone(candidate);
        Normalize(published, addDefaultProxyWhenEmpty);
        Validate(published);
        AppConfigStore.SaveAtomic(_configPath, published);
        _activeConfiguration = published;
        return Snapshot();
    }

    private static void Validate(AppConfig candidate)
    {
        if (!Enum.IsDefined(candidate.GlobalMode))
            throw new InvalidDataException("配置包含不支持的全局模式。");
        if (!Enum.IsDefined(candidate.DnsMode))
            throw new InvalidDataException("配置包含不支持的 DNS 模式。");
        if (candidate.Rules.Any(rule => rule is null) ||
            candidate.ProxyServers.Any(server => server is null) ||
            candidate.ProxyChains.Any(chain => chain is null))
        {
            throw new InvalidDataException("配置集合不能包含空条目。");
        }

        foreach (var rule in candidate.Rules)
        {
            if (!Enum.IsDefined(rule.Mode))
                throw new InvalidDataException("配置包含不支持的规则模式。");
        }

        foreach (var server in candidate.ProxyServers)
        {
            if (!Enum.IsDefined(server.ProxyType))
                throw new InvalidDataException("配置包含不支持的代理类型。");
            server.Host = LocalProxyEndpoint.NormalizeOrThrow(server.Host, server.Port);
        }

        foreach (var chain in candidate.ProxyChains)
        {
            if (!Enum.IsDefined(chain.ChainType))
                throw new InvalidDataException("配置包含不支持的代理链类型。");
        }

        var build = SingBoxConfigBuilder.Build(candidate);
        if (!build.Success)
            throw new InvalidDataException("配置不符合当前支持的安全语义: " + build.Error);
    }

    private static void Normalize(AppConfig config, bool addDefaultProxyWhenEmpty)
    {
        config.Rules ??= [];
        config.ProxyServers ??= [];
        config.ProxyChains ??= [];
        if (addDefaultProxyWhenEmpty && config.ProxyServers.Count == 0)
        {
            config.ProxyServers.Add(new ProxyServer
            {
                Name = "默认 SOCKS5",
                Host = "127.0.0.1",
                Port = 10808,
                ProxyType = ProxyType.Socks5
            });
        }
    }

    private static AppConfig Clone(AppConfig config) =>
        JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(config))
        ?? throw new InvalidDataException("无法创建配置候选副本。");

    private void EnsureWritable()
    {
        if (!IsWritable)
        {
            throw new InvalidOperationException(
                "配置文件不可安全读取，已阻止覆盖保存。请先导入有效配置或明确重置。");
        }
    }

    private void EnsureRecoveryCopyAvailable()
    {
        if (string.IsNullOrWhiteSpace(RecoveryBackupPath) ||
            !File.Exists(RecoveryBackupPath))
        {
            throw new InvalidOperationException(
                "恢复副本不可用，已阻止替换唯一的原始配置。请先手动复制原文件并重新启动应用。");
        }
    }

    private void MarkRecovered()
    {
        IsWritable = true;
        Error = null;
        RecoveryBackupPath = null;
    }
}
