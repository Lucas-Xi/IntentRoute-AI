using System.IO;
using System.Security.Cryptography;
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

    public static AppConfig Deserialize(string json)
    {
        var config = JsonConvert.DeserializeObject<AppConfig>(json) ?? new AppConfig();
        NormalizeCollections(config);

        foreach (var server in config.ProxyServers)
            server.Password = UnprotectPassword(server.Password);

        return config;
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
        if (password.StartsWith(DpapiPrefix, StringComparison.Ordinal)) return password;

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
        catch (CryptographicException)
        {
            return string.Empty;
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static void NormalizeCollections(AppConfig config)
    {
        config.Rules ??= [];
        config.ProxyServers ??= [];
        config.ProxyChains ??= [];
    }
}
