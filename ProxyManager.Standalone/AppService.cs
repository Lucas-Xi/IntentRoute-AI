using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Newtonsoft.Json;
using ProxyManager.Standalone.Localization;

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
    [JsonProperty(Required = Required.Always)]
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
    public string StatusText => Enabled ? Strings.ServerStatusEnabled : Strings.ServerStatusDisabled;
    public Brush StatusColor => Enabled ? new SolidColorBrush(Color.FromRgb(63, 185, 80)) : new SolidColorBrush(Color.FromRgb(139, 148, 158));
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProxyChain : INotifyPropertyChanged
{
    [JsonProperty(Required = Required.Always)]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public ProxyChainType ChainType { get; set; } = ProxyChainType.Sequential;
    public List<string> Servers { get; set; } = new();
    public bool Enabled { get; set; } = true;
    public string ChainTypeText => ChainType switch { ProxyChainType.Sequential => Strings.ChainSequential, ProxyChainType.Failover => Strings.ChainFailover, ProxyChainType.LoadBalance => Strings.ChainLoadBalance, _ => "?" };
    public string ServerSummary => string.Format(Strings.ServerSummaryFormat, Servers.Count);
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public class ProxyRule : INotifyPropertyChanged
{
    [JsonProperty(Required = Required.Always)]
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
    public string ModeText => Mode switch
    {
        ProxyMode.Proxy => Strings.ModeProxyText,
        ProxyMode.Direct => Strings.ModeDirectText,
        ProxyMode.Block => Strings.ModeBlockText,
        _ => "?"
    };
    public string StatusText => IsEnabled ? Strings.RuleStatusEnabled : Strings.RuleStatusDisabled;
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
            if (!string.IsNullOrEmpty(TargetHosts)) p.Add($"{Strings.RuleConditionHostsPrefix}{TargetHosts}");
            if (!string.IsNullOrEmpty(TargetIPs)) p.Add($"{Strings.RuleConditionIpPrefix}{TargetIPs}");
            if (!string.IsNullOrEmpty(TargetPorts)) p.Add($"{Strings.RuleConditionPortsPrefix}{TargetPorts}");
            if (!string.IsNullOrEmpty(Protocol) && !Protocol.Equals("Both", StringComparison.OrdinalIgnoreCase)) p.Add(Protocol);
            return p.Count > 0 ? string.Join(" | ", p) : Strings.RuleConditionAll;
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
    public string Summary => string.Format(Strings.ProfileSummaryFormat, RuleCount, ServerCount);
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
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESNTRY32
    {
        public uint dwSize; public uint cntUsage; public uint th32ProcessID; public IntPtr th32DefaultHeapID;
        public uint th32ModuleID; public uint cntThreads; public uint th32ParentProcessID;
        public int pcPriClassBase; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
    // 必须显式绑定 W 变体：CharSet.Unicode 在默认 ExactSpelling=false 下会先命中
    // kernel32 的 ANSI 导出名 Process32First，导致宽字符结构体读到错位乱码。
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool Process32FirstW(IntPtr h, ref PROCESNTRY32 e);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern bool Process32NextW(IntPtr h, ref PROCESNTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint processAccess, bool bInheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);

    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static Dictionary<uint, string> GetRunningProcesses()
    {
        var p = new Dictionary<uint, string>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        // 失败时返回的是 INVALID_HANDLE_VALUE(-1) 而非零句柄。
        if (snap == IntPtr.Zero || snap == INVALID_HANDLE_VALUE) return p;
        try
        {
            var e = new PROCESNTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESNTRY32>() };
            if (Process32FirstW(snap, ref e))
                do { if (!p.ContainsKey(e.th32ProcessID)) p[e.th32ProcessID] = e.szExeFile; }
                while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return p;
    }

    public static bool TryGetProcessPath(uint pid, out string path)
    {
        path = "";
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint size = 1024;
            if (!QueryFullProcessImageNameW(h, 0, sb, ref size)) return false;
            var result = sb.ToString();
            if (string.IsNullOrWhiteSpace(result)) return false;
            path = result;
            return true;
        }
        finally { CloseHandle(h); }
    }
}

#endregion

#region AppService

public class AppService : IDisposable, IAsyncDisposable
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly ConfigurationWorkspace _workspace;
    private readonly SingBoxRuntime _runtime;
    private readonly object _runtimeApplyGate = new();
    private System.Threading.Timer? _monitorTimer;
    private CancellationTokenSource? _runtimeApplyCts;
    private HashSet<string> _runningProcesses = new();
    private string? _approvedSingBoxExecutablePath;
    public AppConfig Config => _workspace.Snapshot();
    public bool IsConfigurationWritable => _workspace.IsWritable;
    public string ConfigPath => _configPath;
    public string ConfigDirectory => _configDir;
    public string? ConfigurationRecoveryBackupPath => _workspace.RecoveryBackupPath;
    public string? ConfigurationError => _workspace.Error;
    internal bool IsSingBoxExecutableApprovedForSession
    {
        get
        {
            var current = _workspace.Snapshot();
            return !string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) &&
                !string.IsNullOrWhiteSpace(current.SingBoxExecutablePath) &&
                PathsEqual(_approvedSingBoxExecutablePath, current.SingBoxExecutablePath);
        }
    }
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

    internal AppService(
        string appDataRoot,
        bool startMonitor,
        bool applyOnStart,
        Func<string, SingBoxRuntime>? runtimeFactory = null)
    {
        _configDir = AppDataMigration.PrepareConfigDirectory(appDataRoot);
        _configPath = Path.Combine(_configDir, "config.json");
        _runtime = runtimeFactory?.Invoke(_configDir) ?? new SingBoxRuntime(_configDir);
        _workspace = ConfigurationWorkspace.Load(_configPath);
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtime.LogReceived += line => RuntimeLogReceived?.Invoke(line);
        if (startMonitor) StartMonitor();
        if (_workspace.IsWritable && applyOnStart)
            ApplyRules();
    }

    public void ResetUnusableConfiguration()
    {
        _workspace.ResetProtectedConfiguration();
        _approvedSingBoxExecutablePath = null;
        ConfigurationStateChanged?.Invoke();
        ApplyRules();
    }

    public void RecoverConfigurationFromFile(string sourcePath)
    {
        _workspace.RecoverFromFile(sourcePath);
        _approvedSingBoxExecutablePath = null;
        ConfigurationStateChanged?.Invoke();
        ApplyRules();
    }

    // ── Rules ───────────────────────────────────

    public ProxyRule? AddRuleByName(string exeName, string exePath, ProxyMode mode = ProxyMode.Proxy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exeName);
        var current = _workspace.Snapshot();
        if (current.Rules.Any(r => r.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
            return null;

        var rule = new ProxyRule
        {
            ExeName = exeName,
            ExePath = exePath,
            Mode = mode,
            IsEnabled = true,
            Priority = (current.Rules.Count + 1) * 10
        };
        var committed = CommitConfiguration(candidate => candidate.Rules.Add(rule));
        return committed.Rules.Single(candidate => candidate.Id == rule.Id);
    }

    public ProxyRule? AddRule(string exePath, ProxyMode mode = ProxyMode.Proxy) =>
        AddRuleByName(Path.GetFileName(exePath), exePath, mode);

    public void ClearRules()
    {
        if (_workspace.Snapshot().Rules.Count == 0) return;
        CommitConfiguration(candidate => candidate.Rules.Clear());
    }

    public int ImportRules(IReadOnlyList<ProxyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var current = _workspace.Snapshot();
        var imported = new List<ProxyRule>();
        foreach (var rule in rules)
        {
            if (rule == null)
                throw new InvalidDataException(Strings.ErrImportEmptyRules);
            if (current.Rules.Concat(imported).Any(existing =>
                string.Equals(existing.ExeName, rule.ExeName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var importedRule = CloneRule(rule);
            importedRule.Id = Guid.NewGuid().ToString();
            imported.Add(importedRule);
        }

        if (imported.Count == 0) return 0;
        CommitConfiguration(candidate => candidate.Rules.AddRange(imported));
        return imported.Count;
    }

    internal void AcceptDisabledAiRules(IReadOnlyList<ProxyRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        if (rules.Count == 0)
            throw new ArgumentException("No AI rules were supplied.", nameof(rules));
        if (rules.Any(rule => rule == null || rule.IsEnabled))
            throw new ArgumentException("AI rules must be complete and disabled before acceptance.", nameof(rules));

        CommitConfiguration(candidate =>
        {
            foreach (var source in rules)
            {
                var rule = CloneRule(source);
                rule.Id = Guid.NewGuid().ToString();
                rule.IsEnabled = false;
                rule.Priority = (candidate.Rules.Count + 1) * 10;
                candidate.Rules.Add(rule);
            }
        }, applyRuntime: false);
    }

    public void RemoveRule(string id)
    {
        if (_workspace.Snapshot().Rules.All(rule => rule.Id != id)) return;
        CommitConfiguration(candidate => candidate.Rules.RemoveAll(rule => rule.Id == id));
    }

    public void ToggleRule(string id)
    {
        if (_workspace.Snapshot().Rules.All(rule => rule.Id != id)) return;
        CommitConfiguration(candidate =>
        {
            var rule = candidate.Rules.First(item => item.Id == id);
            rule.IsEnabled = !rule.IsEnabled;
        });
    }

    public void UpdateRuleMode(string id, ProxyMode mode)
    {
        if (_workspace.Snapshot().Rules.All(rule => rule.Id != id)) return;
        CommitConfiguration(candidate => candidate.Rules.First(rule => rule.Id == id).Mode = mode);
    }

    public void MoveRule(string id, int delta)
    {
        var current = _workspace.Snapshot();
        var canonicalOrder = PolicyRuntimeOrder.All(current.Rules).ToList();
        var idx = canonicalOrder.FindIndex(r => r.Id == id);
        if (idx < 0) return;
        int newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= canonicalOrder.Count) return;

        (canonicalOrder[idx], canonicalOrder[newIdx]) = (canonicalOrder[newIdx], canonicalOrder[idx]);
        var orderedIds = canonicalOrder.Select(rule => rule.Id).ToList();
        CommitConfiguration(candidate =>
        {
            var rulesById = candidate.Rules.ToDictionary(rule => rule.Id, StringComparer.Ordinal);
            candidate.Rules.Clear();
            candidate.Rules.AddRange(orderedIds.Select(ruleId => rulesById[ruleId]));
            for (int i = 0; i < candidate.Rules.Count; i++)
                candidate.Rules[i].Priority = (i + 1) * 10;
        });
    }

    // ── Proxy Servers ───────────────────────────

    public ProxyServer AddServer(ProxyServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var committed = CommitConfiguration(candidate => candidate.ProxyServers.Add(server));
        return committed.ProxyServers.Single(candidate => candidate.Id == server.Id);
    }

    public void RemoveServer(string id)
    {
        if (_workspace.Snapshot().ProxyServers.All(server => server.Id != id)) return;
        CommitConfiguration(candidate => candidate.ProxyServers.RemoveAll(server => server.Id == id));
    }

    public void UpdateServer(ProxyServer s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (_workspace.Snapshot().ProxyServers.All(server => server.Id != s.Id)) return;
        CommitConfiguration(candidate =>
        {
            var idx = candidate.ProxyServers.FindIndex(server => server.Id == s.Id);
            candidate.ProxyServers[idx] = s;
        });
    }
    public async Task<bool> TestServerAsync(string id)
    {
        var s = _workspace.Snapshot().ProxyServers.FirstOrDefault(x => x.Id == id);
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

    // ── Runtime Logs ─────────────────────────────

    public void ClearLogs()
    {
        _runtime.ClearRecentLogs();
    }

    // ── Global Settings ──────────────────────────

    public void SetGlobalMode(GlobalMode mode)
    {
        if (_workspace.Snapshot().GlobalMode == mode) return;
        CommitConfiguration(candidate => candidate.GlobalMode = mode);
    }

    public void SetDnsMode(DnsMode mode)
    {
        if (_workspace.Snapshot().DnsMode == mode) return;
        CommitConfiguration(candidate => candidate.DnsMode = mode);
    }
    public void UpdatePrimaryProxy(
        ProxyType proxyType,
        string host,
        int port,
        string username,
        string password)
    {
        var normalizedHost = LocalProxyEndpoint.NormalizeOrThrow(host, port);
        CommitConfiguration(candidate =>
        {
            candidate.SocksHost = normalizedHost;
            candidate.SocksPort = port;
            var primary = FindPrimaryProxy(candidate);
            if (primary == null)
            {
                primary = new ProxyServer
                {
                    Name = "默认 SOCKS5",
                    ProxyType = ProxyType.Socks5,
                    Enabled = true
                };
                candidate.ProxyServers.Insert(0, primary);
            }
            primary.ProxyType = proxyType;
            primary.Host = normalizedHost;
            primary.Port = port;
            primary.Username = username?.Trim() ?? string.Empty;
            primary.Password = password ?? string.Empty;
            primary.Enabled = true;
        });
    }

    public ProxyServer? GetPrimaryProxy() =>
        FindPrimaryProxy(_workspace.Snapshot());

    public void SetSingBoxExecutablePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The selected sing-box executable does not exist.", path);

        var approvedPath = Path.GetFullPath(path);
        var committed = _workspace.Commit(candidate => candidate.SingBoxExecutablePath = approvedPath);
        _approvedSingBoxExecutablePath = PathsEqual(approvedPath, committed.SingBoxExecutablePath)
            ? approvedPath
            : null;
        ApplyRules();
    }

    public void ClearSingBoxExecutablePath()
    {
        _workspace.Commit(candidate => candidate.SingBoxExecutablePath = string.Empty);
        _approvedSingBoxExecutablePath = null;
        ApplyRules();
    }

    public Task<SingBoxReadinessResult> ProbeRuntimeReadinessAsync(CancellationToken cancellationToken = default)
    {
        var current = _workspace.Snapshot();
        if (!string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) &&
            !string.IsNullOrWhiteSpace(current.SingBoxExecutablePath) &&
            PathsEqual(_approvedSingBoxExecutablePath, current.SingBoxExecutablePath))
        {
            return _runtime.ProbeReadinessAsync(_approvedSingBoxExecutablePath, cancellationToken);
        }

        string? candidate;
        if (!string.IsNullOrWhiteSpace(current.SingBoxExecutablePath))
        {
            try
            {
                candidate = Path.GetFullPath(current.SingBoxExecutablePath);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(SingBoxReadinessResult.NotReady(
                    null,
                    Strings.RtSavedPathInvalid));
            }
        }
        else
        {
            candidate = _runtime.DiscoverExecutable();
        }
        return Task.FromResult(SingBoxReadinessResult.NotReady(
            candidate,
            candidate == null
                ? Strings.RtNotDetected
                : Strings.RtNotApproved));
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
            var cfg = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(path));
            ReplaceConfiguration(cfg, preserveRuntimeApproval: false);
        }
    }

    public void SaveProfile(string name)
    {
        var path = Path.Combine(_configDir, $"{name}.profile.json");
        _workspace.SaveCopy(path, redactPasswords: false);
    }

    public void ExportProfile(string filePath) =>
        _workspace.SaveCopy(filePath, redactPasswords: true);

    public void ImportProfile(string filePath)
    {
        if (File.Exists(filePath))
        {
            var cfg = AppConfigStore.Deserialize(AppConfigStore.ReadStrictUtf8(filePath));
            ReplaceConfiguration(cfg, preserveRuntimeApproval: false);
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
        if (!_workspace.IsWritable)
        {
            StatusChanged?.Invoke(Strings.StatusConfigUnreadable);
            return;
        }
        var current = _workspace.Snapshot();
        var proxyApps = current.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Proxy).Select(r => r.ExeName).ToList();
        var directApps = current.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Direct).Select(r => r.ExeName).ToList();
        var blockApps = current.Rules.Where(r => r.IsEnabled && r.Mode == ProxyMode.Block).Select(r => r.ExeName).ToList();

            StatusChanged?.Invoke(string.Format(Strings.StatusRuleStatsFormat, proxyApps.Count, directApps.Count, blockApps.Count));
        QueueRuntimeApply();
    }

    public HashSet<string> GetRunningProcesses() => _runningProcesses;
    public List<ProxyRule> GetRules() => _workspace.Snapshot().Rules;
    public List<ProxyServer> GetServers() => _workspace.Snapshot().ProxyServers;
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
        if (!_workspace.IsWritable) return;
        var current = _workspace.Snapshot();
        if (string.IsNullOrWhiteSpace(_approvedSingBoxExecutablePath) ||
            string.IsNullOrWhiteSpace(current.SingBoxExecutablePath) ||
            !PathsEqual(_approvedSingBoxExecutablePath, current.SingBoxExecutablePath))
        {
            CancelPendingRuntimeApply();
            _runtime.MarkRunningConfigurationStale(
                "The active configuration was saved but cannot be applied until the sing-box executable is approved again for this session.");
            StatusChanged?.Invoke(string.IsNullOrWhiteSpace(current.SingBoxExecutablePath)
                ? Strings.StatusNoRuntimeSaved
                : Strings.StatusRuntimeNotApproved);
            return;
        }
        AppConfig snapshot;
        try
        {
            snapshot = current;
            snapshot.SingBoxExecutablePath = _approvedSingBoxExecutablePath;
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(Strings.StatusRuntimePrepFailPrefix + ex.Message);
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
                        ? Strings.StatusNotAppliedPrefix + result.Error
                        : Strings.StatusNotStartedPrefix + result.Error);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(Strings.StatusRuntimeErrorPrefix + SingBoxRuntime.RedactSecrets(ex.Message));
            }
        }, token);
    }

    private void CancelPendingRuntimeApply()
    {
        CancellationTokenSource? pending;
        lock (_runtimeApplyGate)
        {
            pending = _runtimeApplyCts;
            _runtimeApplyCts = null;
        }

        if (pending == null) return;
        try { pending.Cancel(); }
        finally { pending.Dispose(); }
    }

    private void OnRuntimeStatusChanged(SingBoxRuntimeStatus status)
    {
        var text = status.State switch
        {
            SingBoxRuntimeState.Running when status.IsRunning => string.Format(Strings.RtStateRunningFormat, status.ProcessId),
            SingBoxRuntimeState.RunningStale when status.IsRunning =>
                Strings.RtStateRunningStalePrefix + (string.IsNullOrEmpty(status.LastError) ? "" : ": " + status.LastError),
            SingBoxRuntimeState.Probing => Strings.RtStateProbing,
            SingBoxRuntimeState.Checking => Strings.RtStateChecking,
            SingBoxRuntimeState.Starting => Strings.RtStateStarting,
            SingBoxRuntimeState.Failed => Strings.RtStateFailedPrefix + (string.IsNullOrEmpty(status.LastError) ? "" : ": " + status.LastError),
            _ => Strings.RtStateStopped
        };
        RuntimeStatusChanged?.Invoke(status);
        StatusChanged?.Invoke(text);
    }

    private AppConfig CommitConfiguration(
        Action<AppConfig> mutate,
        bool preserveRuntimeApproval = true,
        bool applyRuntime = true)
    {
        var previousApproval = preserveRuntimeApproval ? _approvedSingBoxExecutablePath : null;
        var committed = _workspace.Commit(mutate);
        ReconcileRuntimeApproval(previousApproval, committed);
        if (applyRuntime) ApplyRules();
        return committed;
    }

    private AppConfig ReplaceConfiguration(
        AppConfig candidate,
        bool preserveRuntimeApproval,
        bool applyRuntime = true)
    {
        var previousApproval = preserveRuntimeApproval ? _approvedSingBoxExecutablePath : null;
        var committed = _workspace.Replace(candidate);
        ReconcileRuntimeApproval(previousApproval, committed);
        if (applyRuntime) ApplyRules();
        return committed;
    }

    private void ReconcileRuntimeApproval(string? previousApproval, AppConfig committed)
    {
        _approvedSingBoxExecutablePath = previousApproval != null &&
            !string.IsNullOrWhiteSpace(committed.SingBoxExecutablePath) &&
            PathsEqual(previousApproval, committed.SingBoxExecutablePath)
                ? previousApproval
                : null;
    }

    private static ProxyServer? FindPrimaryProxy(AppConfig config) =>
        config.ProxyServers.FirstOrDefault(server => server.Enabled) ??
        config.ProxyServers.FirstOrDefault();

    private static ProxyRule CloneRule(ProxyRule rule) =>
        JsonConvert.DeserializeObject<ProxyRule>(JsonConvert.SerializeObject(rule))
            ?? throw new InvalidDataException(Strings.ErrCloneRuleCandidate);

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

}

#endregion
