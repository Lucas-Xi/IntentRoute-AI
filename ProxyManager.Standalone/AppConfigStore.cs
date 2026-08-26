using System.IO;
using System.Security.Cryptography;
using System.Security;
using System.Text;
using Newtonsoft.Json;

namespace ProxyManager.Standalone;

/// <summary>
/// Serializes application configuration without leaving proxy passwords in plaintext at rest.
/// Existing plaintext values are accepted once for migration and encrypted on the next save.
/// </summary>
public static class AppConfigStore
{
    private const string DpapiPrefix = "dpapi:";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static AppConfigLoadResult LoadPreservingInvalidFile(string path, DateTime? utcNow = null) =>
        LoadPreservingInvalidFileCore(path, semanticValidator: null, utcNow);

    internal static AppConfigLoadResult LoadPreservingInvalidFile(
        string path,
        Action<AppConfig> semanticValidator,
        DateTime? utcNow = null) =>
        LoadPreservingInvalidFileCore(
            path,
            semanticValidator ?? throw new ArgumentNullException(nameof(semanticValidator)),
            utcNow);

    private static AppConfigLoadResult LoadPreservingInvalidFileCore(
        string path,
        Action<AppConfig>? semanticValidator,
        DateTime? utcNow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            return AppConfigLoadResult.Missing();

        try
        {
            var config = Deserialize(ReadStrictUtf8(path));
            semanticValidator?.Invoke(config);
            return AppConfigLoadResult.Loaded(config);
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException or IOException or UnauthorizedAccessException or SecurityException or AppConfigProtectionException or InvalidDataException or ArgumentException)
        {
            string? backupPath = null;
            try
            {
                backupPath = CreateRecoveryCopy(path, utcNow ?? DateTime.UtcNow);
            }
            catch
            {
                // The original is never modified. Recovery-copy failure is surfaced
                // through the null BackupPath and configuration remains save-blocked.
            }

            return AppConfigLoadResult.Unusable(
                backupPath,
                ex is AppConfigProtectionException
                    ? "当前 Windows 用户无法解密已保存的代理凭据。"
                    : "配置文件无法安全读取。文件可能已损坏或被截断。");
        }
    }

    public static AppConfig Deserialize(string json)
    {
        var config = JsonConvert.DeserializeObject<AppConfig>(json)
            ?? throw new JsonSerializationException("Configuration must contain a JSON object.");
        NormalizeCollections(config);

        foreach (var server in config.ProxyServers)
            server.Password = UnprotectPassword(server.Password);

        return config;
    }

    public static string ReadStrictUtf8(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return File.ReadAllText(path, StrictUtf8);
    }

    public static string Serialize(AppConfig config, bool redactPasswords = false)
    {
        ArgumentNullException.ThrowIfNull(config);

        var clone = JsonConvert.DeserializeObject<AppConfig>(
            JsonConvert.SerializeObject(config)) ?? new AppConfig();
        NormalizeCollections(clone);

        foreach (var server in clone.ProxyServers)
        {
            server.Password = redactPasswords
                ? string.Empty
                : ProtectPassword(server.Password);
        }

        return JsonConvert.SerializeObject(clone, Formatting.Indented);
    }

    public static void SaveAtomic(string path, AppConfig config, bool redactPasswords = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, Serialize(config, redactPasswords), new UTF8Encoding(false));
            if (File.Exists(path))
                File.Replace(tempPath, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* best effort cleanup */ }
            }
        }
    }

    public static string ProtectPassword(string? password)
    {
        if (string.IsNullOrEmpty(password)) return string.Empty;

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return DpapiPrefix + Convert.ToBase64String(bytes);
    }

    public static string UnprotectPassword(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return string.Empty;
        if (!stored.StartsWith(DpapiPrefix, StringComparison.Ordinal))
            return stored; // one-time migration from legacy plaintext configuration

        try
        {
            var encrypted = Convert.FromBase64String(stored[DpapiPrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                encrypted,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser));
        }
        catch (CryptographicException ex)
        {
            throw new AppConfigProtectionException(
                "A DPAPI-protected proxy password could not be decrypted for the current Windows user.",
                ex);
        }
        catch (FormatException ex)
        {
            throw new AppConfigProtectionException(
                "A DPAPI-protected proxy password is malformed.",
                ex);
        }
    }

    private static string CreateRecoveryCopy(string path, DateTime utcNow)
    {
        var timestamp = utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");
        var candidate = path + $".corrupt-{timestamp}.bak";
        for (var suffix = 1; File.Exists(candidate); suffix++)
            candidate = path + $".corrupt-{timestamp}-{suffix}.bak";

        File.Copy(path, candidate, overwrite: false);
        return candidate;
    }

    private static void NormalizeCollections(AppConfig config)
    {
        config.Rules ??= [];
        config.ProxyServers ??= [];
        config.ProxyChains ??= [];

        if (config.Rules.Any(rule => rule is null) ||
            config.ProxyServers.Any(server => server is null) ||
            config.ProxyChains.Any(chain => chain is null))
        {
            throw new JsonSerializationException(
                "Configuration collections cannot contain null entries.");
        }
    }
}

public enum AppConfigLoadStatus
{
    Missing,
    Loaded,
    Unusable
}

public sealed class AppConfigLoadResult
{
    private AppConfigLoadResult(
        AppConfigLoadStatus status,
        AppConfig? config,
        string? backupPath,
        string? error)
    {
        Status = status;
        Config = config;
        BackupPath = backupPath;
        Error = error;
    }

    public AppConfigLoadStatus Status { get; }
    public AppConfig? Config { get; }
    public string? BackupPath { get; }
    public string? Error { get; }

    public static AppConfigLoadResult Missing() =>
        new(AppConfigLoadStatus.Missing, null, null, null);

    public static AppConfigLoadResult Loaded(AppConfig config) =>
        new(AppConfigLoadStatus.Loaded, config, null, null);

    public static AppConfigLoadResult Unusable(string? backupPath, string error) =>
        new(AppConfigLoadStatus.Unusable, null, backupPath, error);
}

public sealed class AppConfigProtectionException : Exception
{
    public AppConfigProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
