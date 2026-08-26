using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace ProxyManager.Standalone;

public partial class MainWindow : Window
{
    private readonly AppService _service;
    private readonly ObservableCollection<ProxyRule> _rules = new();
    private readonly ObservableCollection<RuntimeLogLine> _runtimeLogs = new();
    private List<ProxyRule> _allRules = new();
    private string _searchFilter = "";
    private bool _isMaximized = false;

    public MainWindow()
    {
        InitializeComponent();

        _service = new AppService();
        _service.StatusChanged += s => Dispatcher.Invoke(() => StatusDetail.Text = s);
        _service.RuntimeLogReceived += line => Dispatcher.Invoke(() =>
        {
            _runtimeLogs.Add(new RuntimeLogLine(DateTime.Now.ToString("HH:mm:ss"), line));
            while (_runtimeLogs.Count > 500)
                _runtimeLogs.RemoveAt(0);
        });

        Loaded += MainWindow_Loaded;
        RulesList.ItemsSource = _rules;
        LogsList.ItemsSource = _runtimeLogs;

        // 允许标题栏拖动
        MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadRules();
        LoadSettings();
        RefreshProcessList();
    }

    #region 窗口控制

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        _isMaximized = !_isMaximized;
        WindowState = _isMaximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    #endregion

    #region 导航

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        PageRules.Visibility = Visibility.Collapsed;
        PageMonitor.Visibility = Visibility.Collapsed;
        PageProcess.Visibility = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;
        PageAbout.Visibility = Visibility.Collapsed;

        if (sender is RadioButton rb)
        {
            UIElement? page = rb.Name switch
            {
                "NavRules" => PageRules,
                "NavMonitor" => PageMonitor,
                "NavProcess" => PageProcess,
                "NavSettings" => PageSettings,
                "NavAbout" => PageAbout,
                _ => null
            };

            if (page != null)
                page.Visibility = Visibility.Visible;

            PageTitle.Text = rb.Name switch
            {
                "NavRules" => "规则管理",
                "NavMonitor" => "运行日志",
                "NavProcess" => "进程列表",
                "NavSettings" => "设置",
                "NavAbout" => "关于",
                _ => ""
            };

            PageSubtitle.Text = rb.Name switch
            {
                "NavRules" => " - 拖拽 .exe 添加规则",
                "NavMonitor" => " - sing-box 运行日志",
                "NavProcess" => " - 运行中的进程",
                "NavSettings" => " - 配置代理和功能",
                "NavAbout" => " - 版本信息",
                _ => ""
            };
        }
    }

    #endregion

    #region 规则管理

    private void LoadRules()
    {
        _allRules = _service.Config.Rules
            .OrderBy(r => r.Priority)
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
        ApplyFilter();
        UpdateStats();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(_searchFilter)
            ? _allRules
            : _allRules.Where(r =>
                r.ExeName.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                r.ExePath.Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        int index = 1;
        foreach (var rule in filtered)
            rule.Index = index++;

        _rules.Clear();
        foreach (var rule in filtered)
            _rules.Add(rule);

        EmptyState.Visibility = _rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateStats()
    {
        var proxyCount = _allRules.Count(r => r.Mode == ProxyMode.Proxy && r.IsEnabled);
        var directCount = _allRules.Count(r => r.Mode == ProxyMode.Direct && r.IsEnabled);
        var blockCount = _allRules.Count(r => r.Mode == ProxyMode.Block && r.IsEnabled);

        ProxyCount.Text = $"代理: {proxyCount}";
        DirectCount.Text = $"直连: {directCount}";
        BlockCount.Text = $"阻止: {blockCount}";
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _searchFilter = SearchBox.Text;
        ApplyFilter();
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择应用程序",
            Filter = "可执行文件 (*.exe)|*.exe",
            Multiselect = true
        };

        if (dialog.ShowDialog() == true)
        {
            foreach (var file in dialog.FileNames)
                _service.AddRule(file);
            LoadRules();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定清空所有规则？", "确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _service.Config.Rules.Clear();
            _service.SaveConfig();
            LoadRules();
        }
    }

    private ProxyRule? GetSelectedRule() => RulesList.SelectedItem as ProxyRule;

    private void ToggleRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null) { _service.ToggleRule(rule.Id); LoadRules(); }
    }

    private void SetProxy_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null) { _service.UpdateRuleMode(rule.Id, ProxyMode.Proxy); LoadRules(); }
    }

    private void SetDirect_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null) { _service.UpdateRuleMode(rule.Id, ProxyMode.Direct); LoadRules(); }
    }

    private void SetBlock_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null) { _service.UpdateRuleMode(rule.Id, ProxyMode.Block); LoadRules(); }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null)
        {
            _service.MoveRule(rule.Id, -1);
            LoadRules();
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null)
        {
            _service.MoveRule(rule.Id, 1);
            LoadRules();
        }
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        var rule = GetSelectedRule();
        if (rule != null)
        {
            if (MessageBox.Show($"删除规则 '{rule.ExeName}'？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            {
                _service.RemoveRule(rule.Id);
                LoadRules();
            }
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入规则",
            Filter = "JSON 文件 (*.json)|*.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var json = File.ReadAllText(dialog.FileName);
                var import = Newtonsoft.Json.JsonConvert.DeserializeObject<ImportData>(json);
                if (import?.Rules != null)
                {
                    foreach (var rule in import.Rules)
                    {
                        if (!_service.Config.Rules.Any(r =>
                            r.ExeName.Equals(rule.ExeName, StringComparison.OrdinalIgnoreCase)))
                        {
                            rule.Id = Guid.NewGuid().ToString();
                            _service.Config.Rules.Add(rule);
                        }
                    }
                    _service.SaveConfig();
                    LoadRules();
                    MessageBox.Show($"导入完成: {import.Rules.Count} 条规则", "成功");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败: {ex.Message}", "错误");
            }
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出规则",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"rules_{DateTime.Now:yyyyMMdd}.json"
        };

        if (dialog.ShowDialog() == true)
        {
            try
            {
                var export = new
                {
                    Version = "0.1.1",
                    ExportTime = DateTime.Now,
                    Rules = _service.Config.Rules
                };
                var json = Newtonsoft.Json.JsonConvert.SerializeObject(export, Newtonsoft.Json.Formatting.Indented);
                File.WriteAllText(dialog.FileName, json);
                MessageBox.Show($"导出完成: {_service.Config.Rules.Count} 条规则", "成功");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败: {ex.Message}", "错误");
            }
        }
    }

    private class ImportData
    {
        public List<ProxyRule>? Rules { get; set; }
    }

    #endregion

    #region 全局模式

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        _service.SetGlobalMode(ModeProxy.IsChecked == true ? GlobalMode.ProxyAll : GlobalMode.DirectAll);
    }

    #endregion

    #region 流量监控

    private void ClearLogs_Click(object sender, RoutedEventArgs e)
    {
        _service.ClearLogs();
        _runtimeLogs.Clear();
    }

    #endregion

    #region 进程列表

    private void RefreshProcess_Click(object sender, RoutedEventArgs e)
    {
        RefreshProcessList();
    }

    private void RefreshProcessList()
    {
        var processes = ProcessMonitor.GetRunningProcesses();
        var rules = _service.Config.Rules
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Priority)
            .ToList();
        var list = processes
            .OrderBy(p => p.Value)
            .Select(p => new
            {
                Pid = p.Key,
                Name = p.Value,
                Path = "",
                Status = GetConfiguredMode(rules, p.Value)
            })
            .ToList();

        ProcessList.ItemsSource = list;
        ProcessCount.Text = $"{list.Count} 个进程";
    }

    #endregion

    #region 设置

    private void LoadSettings()
    {
        var config = _service.Config;
        SocksPort.Text = config.SocksPort.ToString();
        ModeProxy.IsChecked = config.GlobalMode == GlobalMode.ProxyAll;
        ModeDirect.IsChecked = config.GlobalMode == GlobalMode.DirectAll;
    }

    private void SaveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SocksPort.Text, out var socksPort) && socksPort is >= 1 and <= 65535)
        {
            _service.UpdateProxy("127.0.0.1", socksPort, "127.0.0.1", 10809);
            StatusDetail.Text = $"本地 SOCKS5 代理已更新: 127.0.0.1:{socksPort}";
        }
        else
        {
            MessageBox.Show("请输入有效的端口号", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    #endregion

    #region 拖放

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            e.Effects = files.Any(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                ? DragDropEffects.Copy : DragDropEffects.None;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private void Window_DragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            foreach (var file in (string[])e.Data.GetData(DataFormats.FileDrop))
            {
                if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    _service.AddRule(file);
                }
            }
            LoadRules();
        }
    }

    #endregion

    protected override void OnClosed(EventArgs e)
    {
        _service.Dispose();
        base.OnClosed(e);
    }

    private static string GetConfiguredMode(IEnumerable<ProxyRule> rules, string processName)
    {
        var rule = rules.FirstOrDefault(r =>
            r.ExeName == "*" || r.ExeName.Equals(processName, StringComparison.OrdinalIgnoreCase));
        return rule?.Mode switch
        {
            ProxyMode.Proxy => "代理规则",
            ProxyMode.Direct => "直连规则",
            ProxyMode.Block => "阻止规则",
            _ => "默认规则"
        };
    }

    private sealed record RuntimeLogLine(string Time, string Message);
}
