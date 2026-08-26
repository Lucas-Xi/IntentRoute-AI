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

public class AppService : IDisposable
{
    private readonly string _configDir;
    private readonly string _configPath;
    private readonly SingBoxRuntime _runtime;
    private readonly object _runtimeApplyGate = new();
    private AppConfig _config = new();
    private System.Threading.Timer? _monitorTimer;
    private CancellationTokenSource? _runtimeApplyCts;
    private HashSet<string> _runningProcesses = new();
    public AppConfig Config => _config;
    public event Action<string>? StatusChanged;
    public event Action<string>? RuntimeLogReceived;

    public AppService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _configDir = Path.Combine(appData, "ProxyManager");
        Directory.CreateDirectory(_configDir);
        _configPath = Path.Combine(_configDir, "config.json");
        _runtime = new SingBoxRuntime(_configDir);
        _runtime.StatusChanged += OnRuntimeStatusChanged;
        _runtime.LogReceived += line => RuntimeLogReceived?.Invoke(line);
        LoadConfig();
        StartMonitor();
        ApplyRules();
    }

    private void LoadConfig()
    {
        if (File.Exists(_configPath))
        {
            try { _config = AppConfigStore.Deserialize(File.ReadAllText(_configPath)); }
            catch { _config = new AppConfig(); }
        }
        else _config = new AppConfig();

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
        AppConfigStore.SaveAtomic(_configPath, _config);
        ApplyRules();
    }

    // ── Rules ───────────────────────────────────

    public ProxyRule AddRule(string exePath, ProxyMode mode = ProxyMode.Proxy)
    {
        var exeName = Path.GetFileName(exePath);
        if (_config.Rules.Any(r => r.ExeName.Equals(exeName, StringComparison.OrdinalIgnoreCase)))
            return null!;

        var rule = new ProxyRule { ExeName = exeName, ExePath = exePath, Mode = mode, IsEnabled = true, Priority = (_config.Rules.Count + 1) * 10 };
        _config.Rules.Add(rule);
        SaveConfig();
        return rule;
    }

    public void RemoveRule(string id) { _config.Rules.RemoveAll(r => r.Id == id); SaveConfig(); }
    public void ToggleRule(string id) { var r = _config.Rules.FirstOrDefault(r => r.Id == id); if (r != null) { r.IsEnabled = !r.IsEnabled; SaveConfig(); } }
    public void UpdateRuleMode(string id, ProxyMode mode) { var r = _config.Rules.FirstOrDefault(r => r.Id == id); if (r != null) { r.Mode = mode; SaveConfig(); } }
    public void MoveRule(string id, int delta)
    {
        var idx = _config.Rules.FindIndex(r => r.Id == id);
        if (idx < 0) return;
        int newIdx = idx + delta;
        if (newIdx < 0 || newIdx >= _config.Rules.Count) return;
        (_config.Rules[idx], _config.Rules[newIdx]) = (_config.Rules[newIdx], _config.Rules[idx]);
        for (int i = 0; i < _config.Rules.Count; i++) _config.Rules[i].Priority = (i + 1) * 10;
        SaveConfig();
    }

    // ── Proxy Servers ───────────────────────────

    public ProxyServer AddServer(ProxyServer s) { _config.ProxyServers.Add(s); SaveConfig(); return s; }
    public void RemoveServer(string id) { _config.ProxyServers.RemoveAll(s => s.Id == id); SaveConfig(); }
    public void UpdateServer(ProxyServer s)
    {
        var idx = _config.ProxyServers.FindIndex(x => x.Id == s.Id);
        if (idx >= 0) { _config.ProxyServers[idx] = s; SaveConfig(); }
    }
    public async Task<bool> TestServerAsync(string id)
    {
        var s = _config.ProxyServers.FirstOrDefault(x => x.Id == id);
        if (s == null) return false;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(s.Host, s.Port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    // ── Proxy Chains ─────────────────────────────

    public ProxyChain AddChain(ProxyChain c) { _config.ProxyChains.Add(c); SaveConfig(); return c; }
    public void RemoveChain(string id) { _config.ProxyChains.RemoveAll(c => c.Id == id); SaveConfig(); }

    // ── Runtime Logs ─────────────────────────────

    public void ClearLogs()
    {
        _runtime.ClearRecentLogs();
    }

    // ── Global Settings ──────────────────────────

    public void SetGlobalMode(GlobalMode mode) { _config.GlobalMode = mode; SaveConfig(); }
    public void SetDnsMode(DnsMode mode) { _config.DnsMode = mode; SaveConfig(); }
    public void UpdateProxy(string socksHost, int socksPort, string httpHost, int httpPort)
    {
        _config.SocksHost = socksHost; _config.SocksPort = socksPort;
        _config.HttpHost = httpHost; _config.HttpPort = httpPort;
        var socks = _config.ProxyServers.FirstOrDefault(s => s.ProxyType == ProxyType.Socks5);
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
        socks.Host = socksHost;
        socks.Port = socksPort;
        SaveConfig();
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
            var cfg = AppConfigStore.Deserialize(File.ReadAllText(path));
            if (cfg != null) { _config = cfg; SaveConfig(); }
        }
    }

    public void SaveProfile(string name)
    {
        var path = Path.Combine(_configDir, $"{name}.profile.json");
        AppConfigStore.SaveAtomic(path, _config);
    }

    public void ExportProfile(string filePath) => AppConfigStore.SaveAtomic(filePath, _config, redactPasswords: true);
    public void ImportProfile(string filePath)
    {
        if (File.Exists(filePath))
        {
            var cfg = AppConfigStore.Deserialize(File.ReadAllText(filePath));
            if (cfg != null) { _config = cfg; SaveConfig(); }
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
        _monitorTimer?.Dispose();
        lock (_runtimeApplyGate)
        {
            _runtimeApplyCts?.Cancel();
            _runtimeApplyCts?.Dispose();
            _runtimeApplyCts = null;
        }
        _runtime.Dispose();
        GC.SuppressFinalize(this);
    }

    private void QueueRuntimeApply()
    {
        AppConfig snapshot;
        try
        {
            snapshot = JsonConvert.DeserializeObject<AppConfig>(
                JsonConvert.SerializeObject(_config)) ?? new AppConfig();
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
                    StatusChanged?.Invoke("运行时未启动: " + result.Error);
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
            SingBoxRuntimeState.Checking => "正在校验 sing-box 配置…",
            SingBoxRuntimeState.Starting => "正在应用分流规则…",
            SingBoxRuntimeState.Failed => "sing-box 未运行" + (string.IsNullOrEmpty(status.LastError) ? "" : ": " + status.LastError),
            _ => "sing-box 已停止"
        };
        StatusChanged?.Invoke(text);
    }
}

#endregion
