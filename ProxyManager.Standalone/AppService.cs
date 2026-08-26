using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;

namespace ProxyManager.Standalone;

#region Enums

public enum ProxyMode { Proxy, Direct, Block }
public enum GlobalMode { ProxyAll, DirectAll }
public enum ProxyType { Socks5, Http, Https }
public enum ProxyChainType { Sequential, Failover, LoadBalance }
public enum DnsMode { Local, Proxy, Auto }
public enum ConnectionAction { Proxy, Direct, Block }

#endregion

#region Models

public class ProxyServer : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public ProxyType ProxyType { get; set; } = ProxyType.Socks5;
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 10808;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public string TestUrl { get; set; } = "http://www.google.com";

    public string ProxyTypeText => ProxyType switch { ProxyType.Socks5 => "SOCKS5", ProxyType.Http => "HTTP", ProxyType.Https => "HTTPS", _ => "?" };
    public string Address => $"{Host}:{Port}";
    public string StatusText => Enabled ? "✓ 启用" : "✗ 禁用";
    public Brush StatusColor => Enabled ? new SolidColorBrush(Color.FromRgb(63, 185, 80)) : new SolidColorBrush(Color.FromRgb(139, 148, 158));
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProxyChain : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public ProxyChainType ChainType { get; set; } = ProxyChainType.Sequential;
    public List<string> Servers { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string ChainTypeText => ChainType switch { ProxyChainType.Sequential => "顺序链", ProxyChainType.Failover => "故障转移", ProxyChainType.LoadBalance => "负载均衡", _ => "?" };
    public string ServerSummary => $"{Servers.Count} 服务器";
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProxyRule : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string ExeName { get; set; } = "";
    public string ExePath { get; set; } = "";
    public ProxyMode Mode { get; set; } = ProxyMode.Proxy;
    public string CreatedAt { get; set; } = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
    public string Note { get; set; } = "";
    public string TargetHosts { get; set; } = "";
    public string TargetIPs { get; set; } = "";
    public string TargetPorts { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string ProxyId { get; set; } = "";
    public string ProxyChainId { get; set; } = "";
    public int Priority { get; set; } = 100;

    private bool _enabled = true;
    public bool IsEnabled { get => _enabled; set { _enabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); } }
    public int Index { get; set; }
    public string ModeText => Mode switch { ProxyMode.Proxy => "代理", ProxyMode.Direct => "直连", ProxyMode.Block => "阻止", _ => "?" };
    public string StatusText => IsEnabled ? "启用" : "禁用";
    public Brush ModeColor => Mode switch
    {
        ProxyMode.Proxy => new SolidColorBrush(Color.FromRgb(88, 166, 255)),
        ProxyMode.Direct => new SolidColorBrush(Color.FromRgb(63, 185, 80)),
        ProxyMode.Block => new SolidColorBrush(Color.FromRgb(248, 81, 73)),
        _ => Brushes.Gray
    };
    public string ConditionSummary
    {
        get
        {
            var p = new List<string>();
            if (!string.IsNullOrEmpty(TargetHosts)) p.Add($"主机:{TargetHosts}");
            if (!string.IsNullOrEmpty(TargetIPs)) p.Add($"IP:{TargetIPs}");
            if (!string.IsNullOrEmpty(TargetPorts)) p.Add($"端口:{TargetPorts}");
            if (!string.IsNullOrEmpty(Protocol) && !Protocol.Equals("Both", StringComparison.OrdinalIgnoreCase)) p.Add(Protocol);
            return p.Count > 0 ? string.Join(" | ", p) : "全部流量";
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProfileInfo
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime ModifiedAt { get; set; } = DateTime.Now;
    public int RuleCount { get; set; }
    public int ServerCount { get; set; }
    public string Summary => $"{RuleCount} 规则, {ServerCount} 代理";
}

public class AppConfig
{
    public string SingBoxExecutablePath { get; set; } = "";
    public string SocksHost { get; set; } = "127.0.0.1";
    public int SocksPort { get; set; } = 10808;
    public string HttpHost { get; set; } = "127.0.0.1";
    public int HttpPort { get; set; } = 10809;
    public GlobalMode GlobalMode { get; set; } = GlobalMode.DirectAll;
    public DnsMode DnsMode { get; set; } = DnsMode.Auto;
    public List<ProxyRule> Rules { get; set; } = new();
    public List<ProxyServer> ProxyServers { get; set; } = new();
    public List<ProxyChain> ProxyChains { get; set; } = new();
}

#endregion

#region Windows Helpers

public static class ProcessMonitor
{
    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
    [DllImport("kernel32.dll")] private static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    public static Dictionary<uint, string> GetRunningProcesses()
    {
        var p = new Dictionary<uint, string>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero) return p;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (Process32First(snap, ref e))
                do { if (!p.ContainsKey(e.th32ProcessID)) p[e.th32ProcessID] = e.szExeFile; }
                while (Process32Next(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return p;
    }
}

#endregion

#region AppService

public class AppService : IDisposable, IAsyncDisposable
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly SingBoxRuntime _runtime;
    private readonly object _runtimeApplyGate = new();
    private AppConfig _config = new();
    private System.Threading.Timer? _monitorTimer;
    private CancellationTokenSource? _runtimeApplyCts;
    private HashSet<string> _runningProcesses = new();
    private bool _configurationWritable = true;
    private string? _approvedSingBoxExecutablePath;
    private string? _configurationRecoveryBackupPath;
    private string? _configurationError;
    public AppConfig Config => _config;
    public bool IsConfigurationWritable => _configurationWritable;
    public string ConfigPath => _configPath;
    public string ConfigDirectory => _configDir;
    public string? ConfigurationRecoveryBackupPath => _configurationRecoveryBackupPath;
    public string? ConfigurationError => _configurationError;
    internal bool IsSingBoxExecutableApprovedForSession =>
        !string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) &&
        !string.IsNullOrWhiteSpace(_config.SingBoxExecutablePath) &&
        PathsEqual(_approvedSingBoxExecutablePath, _config.SingBoxExecutablePath);
    public event Action<string>? StatusChanged;
    public event Action<string>? RuntimeLogReceived;
    public event Action<SingBoxRuntimeStatus>? RuntimeStatusChanged;
    public event Action? ConfigurationStateChanged;

    public AppService()
        : this(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            startMonitor: true,
            applyOnStart: true)
    {
    }

    internal AppService(string appDataRoot, bool startMonitor, bool applyOnStart)
    {
        _configDir = AppDataMigration.PrepareConfigDirectory(appDataRoot);
        _configPath = Path.Combine(_configDir, "config.json");
        _runtime = new SingBoxRuntime(_configDir);
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtime.LogReceived += line => RuntimeLogReceived?.Invoke(line);
        LoadConfig();
        if (startMonitor) StartMonitor();
        if (_configurationWritable && applyOnStart)
            ApplyRules();
    }

    private void LoadConfig()
    {
        var load = AppConfigStore.LoadPreservingInvalidFile(_configPath);
        if (load.Status == AppConfigLoadStatus.Unusable)
        {
            _config = new AppConfig();
            _configurationWritable = false;
            _configurationRecoveryBackupPath = load.BackupPath;
            _configurationError = load.Error;
            return;
        }

        _config = load.Config ?? new AppConfig();
        _configurationWritable = true;
        _configurationRecoveryBackupPath = null;
        _configurationError = null;

        // Ensure defaults for new fields
        _config.ProxyServers ??= new();
        _config.ProxyChains ??= new();
        if (_config.ProxyServers.Count == 0)
        {
            _config.ProxyServers.Add(new ProxyServer { Name = "默认 SOCKS5", Host = "127.0.0.1", Port = 10808, ProxyType = ProxyType.Socks5 });
        }
    }

    public void SaveConfig()
    {
        EnsureConfigurationWritable();
        AppConfigStore.SaveAtomic(_configPath, _config);
        ApplyRules();
    }

    public void ResetUnusableConfiguration()
    {
        if (_configurationWritable)
            throw new InvalidOperationException("Configuration recovery is not required.");
        if (string.IsNullOrWhiteSpace(_configurationRecoveryBackupPath) ||
            !File.Exists(_configurationRecoveryBackupPath))
        {
            throw new InvalidOperationException(
                "恢复副本不可用，已阻止重置以避免覆盖唯一的原始配置。请先手动复制原文件或导入有效配置。");
        }

        var replacement = CreateDefaultConfig();
        AppConfigStore.SaveAtomic(_configPath, replacement);
        _config = replacement;
        MarkConfigurationRecovered();
        ApplyRules();
    }

    public void RecoverConfigurationFromFile(string sourcePath)
    {
        if (_configurationWritable)
            throw new InvalidOperationException("Configuration recovery is not required.");
        EnsureRecoveryCopyAvailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        var recovered = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(sourcePath));
        NormalizeConfig(recovered, addDefaultProxy: recovered.ProxyServers.Count == 0);
        foreach (var server in recovered.ProxyServers)
            ValidateLocalServer(server);
        var build = SingBoxConfigBuilder.Build(recovered);
        if (!build.Success)
            throw new InvalidDataException("导入配置不符合当前支持的安全语义: " + build.Error);
        AppConfigStore.SaveAtomic(_configPath, recovered);
        _config = recovered;
        _approvedSingBoxExecutablePath = null;
        MarkConfigurationRecovered();
        ApplyRules();
    }

    // ── Rules ───────────────────────────────────

    public ProxyRule AddRule(string exePath, ProxyMode mode = ProxyMode.Proxy)
    {
        EnsureConfigurationWritable();
        var exeName = Path.GetFileName(exePath);
        if (_config.Rules.Any(r => r.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
            return null!;

        var rule = new ProxyRule { ExeName = exeName, ExePath = exePath, Mode = mode, IsEnabled = true, Priority = (_config.Rules.Count + 1) * 10 };
        _config.Rules.Add(rule);
        SaveConfig();
        return rule;
    }

    public void ClearRules()
    {
        EnsureConfigurationWritable();
        var candidate = CloneConfig(_config);
        candidate.Rules.Clear();
        ReplaceWithValidatedConfig(candidate, preserveRuntimeApproval: true);
    }

    public int ImportRules(IReadOnlyList<ProxyRule> rules)
    {
        EnsureConfigurationWritable();
        ArgumentNullException.ThrowIfNull(rules);
        var candidate = CloneConfig(_config);
        var added = 0;
        foreach (var rule in rules)
        {
            if (rule == null)
                throw new InvalidDataException("导入文件包含空规则。");
            if (candidate.Rules.Any(existing =>
                string.Equals(existing.ExeName, rule.ExeName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var importedRule = JsonConvert.DeserializeObject<ProxyRule>(
                JsonConvert.SerializeObject(rule))
                ?? throw new InvalidDataException("导入规则无法解析。");
            importedRule.Id = Guid.NewGuid().ToString();
            candidate.Rules.Add(importedRule);
            added++;
        }

        if (added > 0)
            ReplaceWithValidatedConfig(candidate, preserveRuntimeApproval: true);
        return added;
    }

    internal void AcceptDisabledAiRules(IReadOnlyList<ProxyRule> rules)
    {
        EnsureConfigurationWritable();
        // Disabled AI drafts do not change the active routing graph. Persist once without
        // queueing a needless sing-box replacement; enabling remains a separate user action.
        AiRuleAcceptance.PersistDisabledRules(_config, _configPath, rules);
    }

    public void RemoveRule(string id) { EnsureConfigurationWritable(); _config.Rules.RemoveAll(r => r.Id == id); SaveConfig(); }
    public void ToggleRule(string id) { EnsureConfigurationWritable(); var r = _config.Rules.FirstOrDefault(r => r.Id == id); if (r != null) { r.IsEnabled = !r.IsEnabled; SaveConfig(); } }
    public void UpdateRuleMode(string id, ProxyMode mode) { EnsureConfigurationWritable(); var r = _config.Rules.FirstOrDefault(r => r.Id == id); if (r != null) { r.Mode = mode; SaveConfig(); } }
    public void MoveRule(string id, int delta)
    {
        EnsureConfigurationWritable();
        var idx = _config.Rules.FindIndex(r => r.Id == id);
        if (idx < 0) return;
        int newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _config.Rules.Count) return;
        (_config.Rules[idx], _config.Rules[newIdx]) = (_config.Rules[newIdx], _config.Rules[idx]);
        for (int i = 0; i < _config.Rules.Count; i++) _config.Rules[i].Priority = (i + 1) * 10;
        SaveConfig();
    }

    // ── Proxy Servers ───────────────────────────

    public ProxyServer AddServer(ProxyServer s) { EnsureConfigurationWritable(); ValidateLocalServer(s); _config.ProxyServers.Add(s); SaveConfig(); return s; }
    public void RemoveServer(string id) { EnsureConfigurationWritable(); _config.ProxyServers.RemoveAll(s => s.Id == id); SaveConfig(); }
    public void UpdateServer(ProxyServer s)
    {
        EnsureConfigurationWritable();
        ValidateLocalServer(s);
        var idx = _config.ProxyServers.FindIndex(x => x.Id == s.Id);
        if (idx >= 0) { _config.ProxyServers[idx] = s; SaveConfig(); }
    }
    public async Task<bool> TestServerAsync(string id)
    {
        var s = _config.ProxyServers.FirstOrDefault(x => x.Id == id);
        if (s == null) return false;
        return await TestLocalProxyAsync(s.Host, s.Port);
    }

    public async Task<bool> TestLocalProxyAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        if (!LocalProxyEndpoint.TryNormalize(host, port, out var normalizedHost, out _))
            return false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var address = System.Net.IPAddress.Parse(normalizedHost);
            using var client = new System.Net.Sockets.TcpClient(address.AddressFamily);
            await client.ConnectAsync(address, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    // ── Proxy Chains ─────────────────────────────

    public ProxyChain AddChain(ProxyChain c) { EnsureConfigurationWritable(); _config.ProxyChains.Add(c); SaveConfig(); return c; }
    public void RemoveChain(string id) { EnsureConfigurationWritable(); _config.ProxyChains.RemoveAll(c => c.Id == id); SaveConfig(); }

    // ── Runtime Logs ─────────────────────────────

    public void ClearLogs()
    {
        _runtime.ClearRecentLogs();
    }

    // ── Global Settings ──────────────────────────

    public void SetGlobalMode(GlobalMode mode) { EnsureConfigurationWritable(); _config.GlobalMode = mode; SaveConfig(); }
    public void SetDnsMode(DnsMode mode) { EnsureConfigurationWritable(); _config.DnsMode = mode; SaveConfig(); }
    public void UpdatePrimaryProxy(
        ProxyType proxyType,
        string host,
        int port,
        string username,
        string password)
    {
        EnsureConfigurationWritable();
        var normalizedHost = LocalProxyEndpoint.NormalizeOrThrow(host, port);
        _config.SocksHost = normalizedHost;
        _config.SocksPort = port;
        var socks = GetPrimaryProxy();
        if (socks == null)
        {
            socks = new ProxyServer
            {
                Name = "默认 SOCKS5",
                ProxyType = ProxyType.Socks5,
                Enabled = true
            };
            _config.ProxyServers.Insert(0, socks);
        }
        socks.ProxyType = proxyType;
        socks.Host = normalizedHost;
        socks.Port = port;
        socks.Username = username?.Trim() ?? string.Empty;
        socks.Password = password ?? string.Empty;
        socks.Enabled = true;
        SaveConfig();
    }

    public ProxyServer? GetPrimaryProxy() =>
        _config.ProxyServers.FirstOrDefault(server => server.Enabled) ?? _config.ProxyServers.FirstOrDefault();

    public void SetSingBoxExecutablePath(string path)
    {
        EnsureConfigurationWritable();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected sing-box executable does not exist.", path);

        var approvedPath = Path.GetFullPath(path);
        var previousPath = _config.SingBoxExecutablePath;
        var previousApproval = _approvedSingBoxExecutablePath;
        _config.SingBoxExecutablePath = approvedPath;
        _approvedSingBoxExecutablePath = approvedPath;
        try
        {
            SaveConfig();
        }
        catch
        {
            _config.SingBoxExecutablePath = previousPath;
            _approvedSingBoxExecutablePath = previousApproval;
            throw;
        }
    }

    public void ClearSingBoxExecutablePath()
    {
        EnsureConfigurationWritable();
        var previousPath = _config.SingBoxExecutablePath;
        var previousApproval = _approvedSingBoxExecutablePath;
        _config.SingBoxExecutablePath = string.Empty;
        _approvedSingBoxExecutablePath = null;
        try
        {
            SaveConfig();
        }
        catch
        {
            _config.SingBoxExecutablePath = previousPath;
            _approvedSingBoxExecutablePath = previousApproval;
            throw;
        }
    }

    public Task<SingBoxReadinessResult> ProbeRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) &&
            !string.IsNullOrWhiteSpace(_config.SingBoxExecutablePath) &&
            PathsEqual(_approvedSingBoxExecutablePath, _config.SingBoxExecutablePath))
        {
            return _runtime.ProbeReadinessAsync(_approvedSingBoxExecutablePath, cancellationToken);
        }

        string? candidate;
        if (!string.IsNullOrWhiteSpace(_config.SingBoxExecutablePath))
        {
            try
            {
                candidate = Path.GetFullPath(_config.SingBoxExecutablePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SingBoxReadinessResult.NotReady(
                    null,
                    "保存的 sing-box 路径无效，未执行任何命令。请通过“浏览…”选择有效文件。"));
            }
        }
        else
        {
            candidate = _runtime.DiscoverExecutable();
        }
        return Task.FromResult(SingBoxReadinessResult.NotReady(
            candidate,
            candidate == null
                ? "未发现 sing-box。请先单独安装 v1.13+，再通过“浏览…”选择并批准可执行文件。"
                : "该 sing-box 路径尚未在本次启动中批准，未执行任何命令。请通过“浏览…”重新选择该文件后再检查。"));
    }

    // ── Profiles ─────────────────────────────────

    public List<ProfileInfo> GetProfiles()
    {
        var list = new List<ProfileInfo>();
        if (!Directory.Exists(_configDir)) return list;
        foreach (var f in Directory.GetFiles(_configDir, "*.profile.json"))
        {
            try
            {
                var info = new FileInfo(f);
                var cfg = JsonConvert.DeserializeObject<AppConfig>(File.ReadAllText(f));
                list.Add(new ProfileInfo { Name = Path.GetFileNameWithoutExtension(f).Replace(".profile", ""), FileName = f, CreatedAt = info.CreationTime, ModifiedAt = info.LastWriteTime, RuleCount = cfg?.Rules.Count ?? 0, ServerCount = cfg?.ProxyServers.Count ?? 0 });
            }
            catch { }
        }
        return list;
    }

    public void SwitchProfile(string name)
    {
        var path = Path.Combine(_configDir, $"{name}.profile.json");
        if (File.Exists(path))
        {
            EnsureConfigurationWritable();
            var cfg = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(path));
            ReplaceWithValidatedConfig(cfg);
        }
    }

    public void SaveProfile(string name)
    {
        EnsureConfigurationWritable();
        var path = Path.Combine(_configDir, $"{name}.profile.json");
        AppConfigStore.SaveAtomic(path, _config);
    }

    public void ExportProfile(string filePath) { EnsureConfigurationWritable(); AppConfigStore.SaveAtomic(filePath, _config, redactPasswords: true); }
    public void ImportProfile(string filePath)
    {
        EnsureConfigurationWritable();
        if (File.Exists(filePath))
        {
            var cfg = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(filePath));
            ReplaceWithValidatedConfig(cfg);
        }
    }

    // ── Monitor ──────────────────────────────────

    private void StartMonitor()
    {
        _monitorTimer = new System.Threading.Timer(_ =>
        {
            var procs = ProcessMonitor.GetRunningProcesses();
            _runningProcesses = new HashSet<string>(procs.Values, StringComparer.OrdinalIgnoreCase);
        }, null, 0, 2000);
    }

    public void ApplyRules()
    {
        if (!_configurationWritable)
        {
            StatusChanged?.Invoke("配置文件不可安全读取；已阻止保存和 sing-box 启动。请先完成恢复。");
            return;
        }
        var proxyApps = _config.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Proxy).Select(r => r.ExeName).ToList();
        var directApps = _config.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Direct).Select(r => r.ExeName).ToList();
        var blockApps = _config.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Block).Select(r => r.ExeName).ToList();

        StatusChanged?.Invoke($"代理:{proxyApps.Count} 直连:{directApps.Count} 阻止:{blockApps.Count}");
        QueueRuntimeApply();
    }

    public HashSet<string> GetRunningProcesses() => _runningProcesses;
    public List<ProxyRule> GetRules() => _config.Rules;
    public List<ProxyServer> GetServers() => _config.ProxyServers;
    public List<ProxyChain> GetChains() => _config.ProxyChains;
    public SingBoxRuntimeStatus GetRuntimeStatus() => _runtime.GetStatus();
    public IReadOnlyList<string> GetRuntimeLogs() => _runtime.GetRecentLogs();

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        _monitorTimer?.Dispose();
        CancellationTokenSource? runtimeApplyCts;
        lock (_runtimeApplyGate)
        {
            _runtimeApplyCts?.Cancel();
            runtimeApplyCts = _runtimeApplyCts;
            _runtimeApplyCts = null;
        }
        await _runtime.DisposeAsync().ConfigureAwait(false);
        runtimeApplyCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    private void QueueRuntimeApply()
    {
        if (!_configurationWritable) return;
        if (string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) ||
            string.IsNullOrWhiteSpace(_config.SingBoxExecutablePath) ||
            !PathsEqual(_approvedSingBoxExecutablePath, _config.SingBoxExecutablePath))
        {
            StatusChanged?.Invoke(string.IsNullOrWhiteSpace(_config.SingBoxExecutablePath)
                ? "sing-box 尚未选择；配置已保存，但未启动运行时。"
                : "sing-box 路径尚未在本次启动中批准；配置已保存，但未执行该文件。请在设置中通过“浏览…”重新选择。");
            return;
        }
        AppConfig snapshot;
        try
        {
            snapshot = JsonConvert.DeserializeObject<AppConfig>(
                JsonConvert.SerializeObject(_config)) ?? new AppConfig();
            snapshot.SingBoxExecutablePath = _approvedSingBoxExecutablePath;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("无法准备运行时配置: " + ex.Message);
            return;
        }

        CancellationToken token;
        lock (_runtimeApplyGate)
        {
            _runtimeApplyCts?.Cancel();
            _runtimeApplyCts?.Dispose();
            _runtimeApplyCts = new CancellationTokenSource();
            token = _runtimeApplyCts.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _runtime.ApplyAsync(snapshot, token);
                if (!result.Success && !token.IsCancellationRequested)
                {
                    StatusChanged?.Invoke(result.Status.IsRunning
                        ? "新配置未应用；原 sing-box 仍在运行: " + result.Error
                        : "运行时未启动: " + result.Error);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusChanged?.Invoke("运行时错误: " + SingBoxRuntime.RedactSecrets(ex.Message));
            }
        }, token);
    }

    private void OnRuntimeStatusChanged(SingBoxRuntimeStatus status)
    {
        var text = status.State switch
        {
            SingBoxRuntimeState.Running when status.IsRunning => $"sing-box TUN 运行中 (PID {status.ProcessId})",
            SingBoxRuntimeState.RunningStale when status.IsRunning =>
                "新配置未应用；原 sing-box 仍在运行" + (string.IsNullOrEmpty(status.LastError) ? "" : ": " + status.LastError),
            SingBoxRuntimeState.Probing => "正在检查 sing-box 路径和版本…",
            SingBoxRuntimeState.Checking => "正在校验 sing-box 配置…",
            SingBoxRuntimeState.Starting => "正在应用分流规则…",
            SingBoxRuntimeState.Failed => "sing-box 未运行" + (string.IsNullOrEmpty(status.LastError) ? "" : ": " + status.LastError),
            _ => "sing-box 已停止"
        };
        RuntimeStatusChanged?.Invoke(status);
        StatusChanged?.Invoke(text);
    }

    private void EnsureConfigurationWritable()
    {
        if (!_configurationWritable)
        {
            throw new InvalidOperationException(
                "配置文件不可安全读取，已阻止覆盖保存。请先导入有效配置或明确重置。");
        }
    }

    private void ReplaceWithValidatedConfig(AppConfig candidate, bool preserveRuntimeApproval = false)
    {
        NormalizeConfig(candidate, addDefaultProxy: candidate.ProxyServers.Count == 0);
        foreach (var server in candidate.ProxyServers)
            ValidateLocalServer(server);
        var build = SingBoxConfigBuilder.Build(candidate);
        if (!build.Success)
            throw new InvalidDataException("配置不符合当前支持的安全语义: " + build.Error);

        AppConfigStore.SaveAtomic(_configPath, candidate);
        var previousApproval = preserveRuntimeApproval ? _approvedSingBoxExecutablePath : null;
        _config = candidate;
        _approvedSingBoxExecutablePath = previousApproval != null &&
            !string.IsNullOrWhiteSpace(candidate.SingBoxExecutablePath) &&
            PathsEqual(previousApproval, candidate.SingBoxExecutablePath)
                ? previousApproval
                : null;
        ApplyRules();
    }

    private static AppConfig CloneConfig(AppConfig config) =>
        JsonConvert.DeserializeObject<AppConfig>(JsonConvert.SerializeObject(config))
        ?? throw new InvalidDataException("无法创建配置候选副本。");

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void MarkConfigurationRecovered()
    {
        _configurationWritable = true;
        _configurationError = null;
        _configurationRecoveryBackupPath = null;
        ConfigurationStateChanged?.Invoke();
    }

    private void EnsureRecoveryCopyAvailable()
    {
        if (string.IsNullOrWhiteSpace(_configurationRecoveryBackupPath) ||
            !File.Exists(_configurationRecoveryBackupPath))
        {
            throw new InvalidOperationException(
                "恢复副本不可用，已阻止替换唯一的原始配置。请先手动复制原文件并重新启动应用。");
        }
    }

    private static AppConfig CreateDefaultConfig()
    {
        var config = new AppConfig();
        NormalizeConfig(config, addDefaultProxy: true);
        return config;
    }

    private static void NormalizeConfig(AppConfig config, bool addDefaultProxy)
    {
        config.Rules ??= [];
        config.ProxyServers ??= [];
        config.ProxyChains ??= [];
        if (addDefaultProxy && config.ProxyServers.Count == 0)
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

    private static void ValidateLocalServer(ProxyServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        server.Host = LocalProxyEndpoint.NormalizeOrThrow(server.Host, server.Port);
    }
}

#endregion
