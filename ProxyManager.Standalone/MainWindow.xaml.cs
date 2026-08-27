using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
    private readonly ObservableCollection<AiRulePreviewLine> _aiDrafts = new();
    private readonly ObservableCollection<PolicyFindingPreviewLine> _policyFindings = new();
    private readonly ObservableCollection<AiPolicyAdviceLine> _policyAdvice = new();
    private readonly ObservableCollection<RouteDecisionTraceLine> _routeDecisionTrace = new();
    private readonly OpenAiRuleProvider _openAiProvider = new();
    private readonly OllamaRuleProvider _ollamaProvider = new();
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly SemaphoreSlim _aiProviderGate = new(1, 1);
    private List<ProxyRule> _allRules = new();
    private string _searchFilter = "";
    private bool _isMaximized = false;
    private CancellationTokenSource? _aiGenerationCts;
    private CancellationTokenSource? _policyAnalysisCts;
    private CancellationTokenSource? _policyExplanationCts;
    private CancellationTokenSource? _runtimeReadinessCts;
    private CancellationTokenSource? _routeSimulationCts;
    private Task _policyAnalysisDrainTask = Task.CompletedTask;
    private Task _routeSimulationDrainTask = Task.CompletedTask;
    private int _policyAnalysisVersion;
    private int _aiModelRefreshVersion;
    private int _policyModelRefreshVersion;
    private int _routeSimulationVersion;
    private AiRuleSuggestion? _currentAiSuggestion;
    private AiRuleValidationResult? _currentAiValidation;
    private PolicyAnalysisReport? _currentPolicyReport;
    private RouteDecisionReport? _currentRouteDecisionReport;
    private RouteDecisionQuery? _currentRouteDecisionQuery;
    private bool _shutdownStarted;
    private bool _shutdownComplete;

    public MainWindow()
    {
        InitializeComponent();

        _service = new AppService();
        _service.StatusChanged += s => PostToUi(() => StatusDetail.Text = s);
        _service.RuntimeStatusChanged += status => PostToUi(() => UpdateRuntimeStatusUi(status));
        _service.ConfigurationStateChanged += () => PostToUi(UpdateConfigurationUi);
        _service.RuntimeLogReceived += line => PostToUi(() =>
        {
            _runtimeLogs.Add(new RuntimeLogLine(DateTime.Now.ToString("HH:mm:ss"), line));
            while (_runtimeLogs.Count > 500)
                _runtimeLogs.RemoveAt(0);
        });

        Loaded += MainWindow_Loaded;
        RulesList.ItemsSource = _rules;
        LogsList.ItemsSource = _runtimeLogs;
        AiDraftList.ItemsSource = _aiDrafts;
        PolicyFindingsList.ItemsSource = _policyFindings;
        PolicyAiAdviceList.ItemsSource = _policyAdvice;
        RouteDecisionTraceList.ItemsSource = _routeDecisionTrace;
        AiProviderCombo.ItemsSource = new[] { "OpenAI（云端）", "Ollama（本地）" };
        AiProviderCombo.SelectedIndex = 0;
        PolicyProviderCombo.ItemsSource = new[] { "OpenAI（云端）", "Ollama（本地）" };
        PolicyProviderCombo.SelectedIndex = 0;

        // 允许标题栏拖动
        MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) DragMove(); };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateConfigurationUi();
            LoadRules();
            LoadSettings();
            RefreshProcessList();
            await RefreshRuntimeReadinessAsync();
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            UpdateRuntimeStatusUi(_service.GetRuntimeStatus());
            await RefreshAiModelsAsync(_lifetimeCts.Token);
            await RefreshPolicyModelsAsync(_lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Normal window shutdown can overlap asynchronous first-load work.
        }
        catch (ObjectDisposedException) when (_shutdownStarted || _shutdownComplete)
        {
            // A provider/runtime may finish disposal while a queued UI continuation unwinds.
        }
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
        if (sender is RadioButton rb) ShowPage(rb.Name);
    }

    private void ShowPage(string pageName)
    {
        PageRules.Visibility = Visibility.Collapsed;
        PageAiAssistant.Visibility = Visibility.Collapsed;
        PagePolicyIntelligence.Visibility = Visibility.Collapsed;
        PageRouteSimulator.Visibility = Visibility.Collapsed;
        PageMonitor.Visibility = Visibility.Collapsed;
        PageProcess.Visibility = Visibility.Collapsed;
        PageSettings.Visibility = Visibility.Collapsed;
        PageAbout.Visibility = Visibility.Collapsed;

        UIElement? page = pageName switch
        {
            "NavRules" => PageRules,
            "NavAi" => PageAiAssistant,
            "NavPolicy" => PagePolicyIntelligence,
            "NavRouteSimulator" => PageRouteSimulator,
            "NavMonitor" => PageMonitor,
            "NavProcess" => PageProcess,
            "NavSettings" => PageSettings,
            "NavAbout" => PageAbout,
            _ => null
        };

        if (page != null) page.Visibility = Visibility.Visible;
        PageTitle.Text = pageName switch
        {
            "NavRules" => "规则管理",
            "NavAi" => "AI 规则助手",
            "NavPolicy" => "AI 策略体检",
            "NavRouteSimulator" => "AI 路由推演",
            "NavMonitor" => "运行日志",
            "NavProcess" => "进程列表",
            "NavSettings" => "设置",
            "NavAbout" => "关于",
            _ => ""
        };
        PageSubtitle.Text = pageName switch
        {
            "NavRules" => " - 拖拽 .exe 添加规则",
            "NavAi" => " - 自然语言生成可审查的规则草案",
            "NavPolicy" => " - 本地确定性检查与可选 AI 解读",
            "NavRouteSimulator" => " - 严格静态 what-if，不是流量遥测",
            "NavMonitor" => " - sing-box 运行日志",
            "NavProcess" => " - 运行中的进程",
            "NavSettings" => " - 配置代理和功能",
            "NavAbout" => " - 版本信息",
            _ => ""
        };

        if (pageName == "NavRouteSimulator")
            RefreshRouteDecisionFreshness();
    }

    #endregion

    #region AI 规则助手

    private IAiRuleProvider GetSelectedAiProvider() =>
        AiProviderCombo.SelectedIndex == 1 ? _ollamaProvider : _openAiProvider;

    private async void AiProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _aiGenerationCts != null || _shutdownStarted) return;
        await RefreshAiModelsAsync(_lifetimeCts.Token);
    }

    private async void RefreshAiModels_Click(object sender, RoutedEventArgs e)
    {
        if (_aiGenerationCts != null || _shutdownStarted) return;
        await RefreshAiModelsAsync(_lifetimeCts.Token);
    }

    private async Task RefreshAiModelsAsync(CancellationToken cancellationToken = default)
    {
        var refreshVersion = ++_aiModelRefreshVersion;
        var provider = GetSelectedAiProvider();
        AiModelCombo.ItemsSource = null;
        AiModelCombo.IsEnabled = false;
        AiGenerateButton.IsEnabled = false;
        AiAcceptButton.IsEnabled = false;
        ResetAiDraft();

        AiPrivacyText.Text = provider.Kind == AiProviderKind.OpenAI
            ? "OpenAI 模式只发送你输入的意图和静态规则格式；请求设置 store=false。不会发送代理凭据、现有规则、日志或进程列表。"
            : "Ollama 模式默认连接本机 127.0.0.1，且只允许字面量 127.0.0.1 或 ::1；不会自动下载模型、启动服务或回退到云端。";

        try
        {
            var models = await RunAiProviderOperationAsync(
                token => provider.ListModelsAsync(token),
                cancellationToken);
            if (refreshVersion != _aiModelRefreshVersion || !ReferenceEquals(provider, GetSelectedAiProvider()))
                return;
            AiModelCombo.ItemsSource = models;
            if (models.Count > 0)
            {
                AiModelCombo.SelectedIndex = 0;
                AiStatusText.Text = provider.Kind == AiProviderKind.OpenAI && !provider.IsAvailable
                    ? "已加载 OpenAI 模型。生成前请设置当前用户环境变量 OPENAI_API_KEY，然后重新打开应用。"
                    : $"已加载 {models.Count} 个可用模型。AI 草案不会自动写入或启用。";
                AiGenerateButton.IsEnabled = true;
            }
            else
            {
                AiStatusText.Text = "Ollama 正在运行，但没有已安装模型。请先在终端执行 ollama pull <模型名>。";
            }
        }
        catch (AiProviderException ex)
        {
            if (refreshVersion == _aiModelRefreshVersion)
                AiStatusText.Text = ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Model discovery cancellation is expected during shutdown or a newer request.
        }
        finally
        {
            if (refreshVersion == _aiModelRefreshVersion && !cancellationToken.IsCancellationRequested)
                AiModelCombo.IsEnabled = true;
        }
    }

    private async void GenerateAi_Click(object sender, RoutedEventArgs e)
    {
        if (_aiGenerationCts != null) return;
        var intent = AiIntentBox.Text.Trim();
        var model = AiModelCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(intent) || string.IsNullOrWhiteSpace(model))
        {
            AiStatusText.Text = "请输入分流意图并选择模型。";
            return;
        }

        ResetAiDraft();
        var cts = new CancellationTokenSource();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(
            cts.Token,
            _lifetimeCts.Token);
        _aiGenerationCts = cts;
        AiProviderCombo.IsEnabled = false;
        AiModelCombo.IsEnabled = false;
        AiIntentBox.IsEnabled = false;
        AiGenerateButton.IsEnabled = false;
        AiCancelButton.IsEnabled = true;
        AiStatusText.Text = "正在生成结构化草案；结果返回后会立即执行本地确定性校验…";

        try
        {
            var provider = GetSelectedAiProvider();
            var suggestion = await RunAiProviderOperationAsync(
                token => provider.GenerateDraftAsync(new AiRuleRequest(intent, model), token),
                requestCts.Token);
            var validation = AiRuleDraftValidator.Validate(suggestion, _service.Config);
            _currentAiSuggestion = suggestion;
            _currentAiValidation = validation;

            foreach (var draft in suggestion.Rules)
                _aiDrafts.Add(AiRulePreviewLine.FromDraft(draft));
            AiDraftCount.Text = $"{_aiDrafts.Count} 条规则";

            if (validation.Success)
            {
                var warnings = suggestion.Warnings.Count == 0
                    ? "无模型警告。"
                    : "警告: " + string.Join("；", suggestion.Warnings);
                AiStatusText.Text = $"本地校验通过。{suggestion.Summary} {warnings} 添加后规则仍为禁用状态。";
                AiAcceptButton.IsEnabled = true;
            }
            else
            {
                AiStatusText.Text = "本地校验未通过: " + string.Join("；", validation.Errors);
            }
        }
        catch (OperationCanceledException)
        {
            AiStatusText.Text = "已取消 AI 规则生成，配置未发生变化。";
        }
        catch (AiProviderException ex)
        {
            AiStatusText.Text = ex.Message;
        }
        catch
        {
            AiStatusText.Text = "AI 规则生成失败。配置未发生变化，请检查提供商后重试。";
        }
        finally
        {
            if (ReferenceEquals(_aiGenerationCts, cts))
            {
                _aiGenerationCts = null;
                cts.Dispose();
            }
            if (!_shutdownStarted)
            {
                AiProviderCombo.IsEnabled = true;
                AiModelCombo.IsEnabled = true;
                AiIntentBox.IsEnabled = true;
                AiCancelButton.IsEnabled = false;
                AiGenerateButton.IsEnabled = AiModelCombo.SelectedItem is string;
            }
        }
    }

    private async Task<T> RunAiProviderOperationAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await _aiProviderGate.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _aiProviderGate.Release();
        }
    }

    private void CancelAi_Click(object sender, RoutedEventArgs e) => _aiGenerationCts?.Cancel();

    private void AcceptAiRules_Click(object sender, RoutedEventArgs e)
    {
        if (_currentAiSuggestion == null || _currentAiValidation is not { Success: true }) return;
        try
        {
            var revalidated = AiRuleDraftValidator.Validate(_currentAiSuggestion, _service.Config);
            if (!revalidated.Success)
            {
                _currentAiValidation = revalidated;
                AiAcceptButton.IsEnabled = false;
                AiStatusText.Text = "现有配置在预览后发生变化，草案重新校验未通过: " + string.Join("；", revalidated.Errors);
                return;
            }

            _service.AcceptDisabledAiRules(revalidated.Rules);
            LoadRules();
            AiStatusText.Text = $"已添加 {revalidated.Rules.Count} 条禁用规则。请到规则管理页逐条检查并手动启用。";
            AiAcceptButton.IsEnabled = false;
            _currentAiSuggestion = null;
            _currentAiValidation = null;
        }
        catch
        {
            AiStatusText.Text = "保存 AI 草案失败；本次添加已回滚，现有配置未被替换。";
        }
    }

    private void ResetAiDraft()
    {
        _currentAiSuggestion = null;
        _currentAiValidation = null;
        _aiDrafts.Clear();
        AiDraftCount.Text = "0 条规则";
        AiAcceptButton.IsEnabled = false;
    }

    #endregion

    #region AI 策略体检

    private IAiPolicyExplainer GetSelectedPolicyExplainer() =>
        PolicyProviderCombo.SelectedIndex == 1 ? _ollamaProvider : _openAiProvider;

    private async void PolicyProvider_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _policyExplanationCts != null || _shutdownStarted) return;
        await RefreshPolicyModelsAsync(_lifetimeCts.Token);
    }

    private async void RefreshPolicyModels_Click(object sender, RoutedEventArgs e)
    {
        if (_policyExplanationCts != null || _shutdownStarted) return;
        await RefreshPolicyModelsAsync(_lifetimeCts.Token);
    }

    private async Task RefreshPolicyModelsAsync(CancellationToken cancellationToken = default)
    {
        var refreshVersion = ++_policyModelRefreshVersion;
        var provider = GetSelectedPolicyExplainer();
        PolicyModelCombo.ItemsSource = null;
        PolicyModelCombo.IsEnabled = false;
        PolicyExplainButton.IsEnabled = false;
        ResetPolicyExplanation();

        try
        {
            var models = await RunAiProviderOperationAsync(
                token => provider.ListModelsAsync(token),
                cancellationToken);
            if (refreshVersion != _policyModelRefreshVersion ||
                !ReferenceEquals(provider, GetSelectedPolicyExplainer()))
            {
                return;
            }

            PolicyModelCombo.ItemsSource = models;
            if (models.Count > 0)
            {
                PolicyModelCombo.SelectedIndex = 0;
                PolicyStatusText.Text = provider.Kind == AiProviderKind.OpenAI && !provider.IsAvailable
                    ? "本地体检可直接使用。AI 解读需要先设置 OPENAI_API_KEY，再重新打开应用。"
                    : "本地体检不会联网；只有点击 AI 解读后才发送去标识结构摘要。";
                PolicyExplainButton.IsEnabled =
                    PolicyFindingsList.SelectedItems.Count is > 0 and <= PolicyDisclosure.MaxFindings;
            }
            else
            {
                PolicyStatusText.Text = "本地体检可直接使用；当前 Ollama 没有已安装模型，AI 解读暂不可用。";
            }
        }
        catch (AiProviderException ex)
        {
            if (refreshVersion == _policyModelRefreshVersion)
                PolicyStatusText.Text = "本地体检仍可使用。AI 模型不可用: " + ex.Message;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown or a newer model refresh canceled this request.
        }
        finally
        {
            if (refreshVersion == _policyModelRefreshVersion && !cancellationToken.IsCancellationRequested)
                PolicyModelCombo.IsEnabled = true;
        }
    }

    private async void AnalyzePolicy_Click(object sender, RoutedEventArgs e)
    {
        await RefreshPolicyAnalysisAsync();
    }

    private async void ExplainPolicy_Click(object sender, RoutedEventArgs e)
    {
        if (_policyExplanationCts != null || _currentPolicyReport == null) return;
        var report = _currentPolicyReport;
        bool matchesBeforePreview;
        try
        {
            matchesBeforePreview = await PolicyReportMatchesCurrentAsync(report, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        if (!matchesBeforePreview)
        {
            await RefreshPolicyAnalysisAsync();
            PolicyStatusText.Text = "策略已变化；已阻止预览旧摘要，请在最新体检中重新选择发现。";
            return;
        }

        var model = PolicyModelCombo.SelectedItem as string;
        var selectedCodes = PolicyFindingsList.SelectedItems
            .OfType<PolicyFindingPreviewLine>()
            .Select(finding => finding.Code)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (string.IsNullOrWhiteSpace(model) || selectedCodes.Count is < 1 or > PolicyDisclosure.MaxFindings)
        {
            PolicyStatusText.Text = "请先在本地发现列表中选择 1–20 项，再选择 AI 模型。";
            return;
        }

        var disclosure = PolicyIntelligence.ToDisclosure(report, selectedCodes);
        var provider = GetSelectedPolicyExplainer();
        var preview = AiPolicyContract.CreateInput(disclosure);
        var providerNotice = provider.Kind == AiProviderKind.OpenAI
            ? "提供商: OpenAI；请求设置 store=false，但提供商侧处理仍受你的账户与当前政策约束。"
            : "提供商: 本机 Ollama；请求仅发送到字面量 127.0.0.1 或 ::1。";
        var confirmed = MessageBox.Show(
            providerNotice + "\n\n将发送的完整逻辑 JSON：\n" + preview +
            "\n\n不包含进程名、域名、IP、端口、规则 ID、备注、路径、代理地址、凭据、日志或进程列表。确认发送本次去标识摘要吗？",
            "确认发送 AI 策略摘要",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question,
            MessageBoxResult.No);
        if (confirmed != MessageBoxResult.Yes)
        {
            PolicyStatusText.Text = "已取消发送；本地体检结果与配置均未变化。";
            return;
        }

        bool matchesAfterConfirmation;
        try
        {
            matchesAfterConfirmation = await PolicyReportMatchesCurrentAsync(report, _lifetimeCts.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }
        if (!matchesAfterConfirmation)
        {
            await RefreshPolicyAnalysisAsync();
            PolicyStatusText.Text = "确认期间策略已变化；未发送旧摘要，请在最新体检中重新选择发现。";
            return;
        }

        var cts = new CancellationTokenSource();
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _lifetimeCts.Token);
        _policyExplanationCts = cts;
        PolicyProviderCombo.IsEnabled = false;
        PolicyModelCombo.IsEnabled = false;
        PolicyExplainButton.IsEnabled = false;
        PolicyCancelButton.IsEnabled = true;
        ResetPolicyExplanation();
        PolicyStatusText.Text = disclosure.OmittedFindingCount == 0
            ? "正在发送去标识结构摘要并等待只读 AI 解读…"
            : $"正在发送最高优先级的 {disclosure.Findings.Count} 项结构发现；另有 {disclosure.OmittedFindingCount} 项仅保留在本地…";

        try
        {
            var explanation = await RunAiProviderOperationAsync(
                token => provider.ExplainPolicyAsync(
                    new AiPolicyExplainRequest(model, disclosure),
                    token),
                requestCts.Token);

            if (!await PolicyReportMatchesCurrentAsync(report, requestCts.Token))
            {
                await RefreshPolicyAnalysisAsync();
                PolicyStatusText.Text = "AI 解读返回前配置已变化；旧结果已丢弃，请基于最新体检重新解读。";
                return;
            }

            PolicyAiSummaryText.Text = explanation.Summary;
            foreach (var priority in explanation.Priorities)
                _policyAdvice.Add(AiPolicyAdviceLine.FromPriority(priority));
            var caveats = explanation.Caveats.Count == 0
                ? "模型未返回额外限制说明。"
                : "限制: " + string.Join("；", explanation.Caveats);
            PolicyStatusText.Text = $"AI 解读完成，共 {explanation.Priorities.Count} 条优先建议。{caveats} 配置未发生变化。";
        }
        catch (OperationCanceledException)
        {
            PolicyStatusText.Text = "已取消 AI 策略解读；本地体检和配置均未变化。";
        }
        catch (AiProviderException ex)
        {
            PolicyStatusText.Text = ex.Message + " 本地体检结果仍然有效，配置未变化。";
        }
        catch
        {
            PolicyStatusText.Text = "AI 策略解读失败；本地体检结果仍然有效，配置未变化。";
        }
        finally
        {
            if (ReferenceEquals(_policyExplanationCts, cts))
            {
                _policyExplanationCts = null;
                cts.Dispose();
            }
            if (!_shutdownStarted)
            {
                PolicyProviderCombo.IsEnabled = true;
                PolicyModelCombo.IsEnabled = true;
                PolicyCancelButton.IsEnabled = false;
                PolicyExplainButton.IsEnabled =
                    PolicyModelCombo.SelectedItem is string &&
                    PolicyFindingsList.SelectedItems.Count is > 0 and <= PolicyDisclosure.MaxFindings;
            }
        }
    }

    private void CancelPolicyExplanation_Click(object sender, RoutedEventArgs e) =>
        _policyExplanationCts?.Cancel();

    private void PolicyFindingSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        PolicyLocateButton.IsEnabled =
            PolicyFindingsList.SelectedItem is PolicyFindingPreviewLine { PrimaryRuleId: not null };
        PolicyExplainButton.IsEnabled =
            _policyExplanationCts == null &&
            PolicyModelCombo.SelectedItem is string &&
            PolicyFindingsList.SelectedItems.Count is > 0 and <= PolicyDisclosure.MaxFindings;
    }

    private void LocatePolicyRule_Click(object sender, RoutedEventArgs e)
    {
        if (PolicyFindingsList.SelectedItem is not PolicyFindingPreviewLine { PrimaryRuleId: not null } selected)
            return;

        NavRules.IsChecked = true;
        SearchBox.Text = string.Empty;
        ShowPage("NavRules");
        LoadRules();
        var rule = _rules.FirstOrDefault(item => item.Id == selected.PrimaryRuleId);
        if (rule == null) return;
        RulesList.SelectedItem = rule;
        RulesList.ScrollIntoView(rule);
        RulesList.Focus();
    }

    private Task RefreshPolicyAnalysisAsync()
    {
        var previous = _policyAnalysisCts;
        previous?.Cancel();
        var version = ++_policyAnalysisVersion;

        if (!_service.IsConfigurationWritable)
        {
            _policyAnalysisCts = null;
            ShowProtectedPolicyState();
            return Task.CompletedTask;
        }

        var snapshot = _service.Config;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _policyAnalysisCts = cts;
        BeginPolicyAnalysisUi();
        var currentTask = RunPolicyAnalysisAsync(snapshot, version, cts);
        _policyAnalysisDrainTask = Task.WhenAll(_policyAnalysisDrainTask, currentTask);
        return currentTask;
    }

    private async Task RunPolicyAnalysisAsync(
        AppConfig snapshot,
        int version,
        CancellationTokenSource cts)
    {
        try
        {
            var latest = await Task.Run(
                () => PolicyIntelligence.Analyze(snapshot, cts.Token),
                cts.Token);
            if (cts.IsCancellationRequested || version != _policyAnalysisVersion || _shutdownStarted)
                return;

            _currentPolicyReport = latest;
            _policyFindings.Clear();
            foreach (var finding in latest.Findings)
                _policyFindings.Add(PolicyFindingPreviewLine.FromFinding(finding));
            PolicyActiveCount.Text = latest.ActiveRuleCount.ToString();
            PolicyCriticalCount.Text = latest.CriticalCount.ToString();
            PolicyWarningCount.Text = latest.WarningCount.ToString();
            PolicyDisabledCount.Text = latest.DisabledRuleCount.ToString();
            PolicyFindingCount.Text = $"{latest.Findings.Count} 项";
            PolicyEmptyText.Text = "未发现可确定的问题。策略体检不等同于真实流量验证。";
            PolicyEmptyText.Visibility = latest.Findings.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PolicyStatusText.Text = !latest.IsComplete
                ? $"本地体检达到分析预算：至少 {latest.OmittedFindingCount} 个项目未完整展开，当前报告不能视为完整结论。"
                : latest.Findings.Count == 0
                    ? "本地体检完成：未发现可确定的问题。此结果不代表真实流量或代理连通性已验证。"
                    : $"本地体检完成：{latest.Findings.Count} 项发现；未调用 AI，未修改配置。";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // A newer snapshot or window shutdown superseded this bounded local scan.
        }
        catch (Exception ex)
        {
            if (version == _policyAnalysisVersion && !_shutdownStarted)
            {
                _currentPolicyReport = null;
                _policyFindings.Clear();
                PolicyFindingCount.Text = "0 项";
                PolicyEmptyText.Text = "本地体检失败，未生成策略结论。";
                PolicyEmptyText.Visibility = Visibility.Visible;
                PolicyStatusText.Text = "本地体检失败: " + SingBoxRuntime.RedactSecrets(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_policyAnalysisCts, cts))
                _policyAnalysisCts = null;
            cts.Dispose();
        }
    }

    private void BeginPolicyAnalysisUi()
    {
        _policyExplanationCts?.Cancel();
        _currentPolicyReport = null;
        _policyFindings.Clear();
        ResetPolicyExplanation();
        PolicyActiveCount.Text = "…";
        PolicyCriticalCount.Text = "…";
        PolicyWarningCount.Text = "…";
        PolicyDisabledCount.Text = "…";
        PolicyFindingCount.Text = "分析中";
        PolicyEmptyText.Text = "正在后台执行可取消的本地确定性体检…";
        PolicyEmptyText.Visibility = Visibility.Visible;
        PolicyLocateButton.IsEnabled = false;
        PolicyExplainButton.IsEnabled = false;
        PolicyStatusText.Text = "正在后台分析当前策略；不会联网、探测端口或修改配置。";
    }

    private void ShowProtectedPolicyState()
    {
        _policyExplanationCts?.Cancel();
        _currentPolicyReport = null;
        _policyFindings.Clear();
        ResetPolicyExplanation();
        PolicyActiveCount.Text = "0";
        PolicyCriticalCount.Text = "0";
        PolicyWarningCount.Text = "0";
        PolicyDisabledCount.Text = "0";
        PolicyFindingCount.Text = "0 项";
        PolicyEmptyText.Text = "配置处于恢复保护状态，未执行策略体检。";
        PolicyEmptyText.Visibility = Visibility.Visible;
        PolicyLocateButton.IsEnabled = false;
        PolicyExplainButton.IsEnabled = false;
        PolicyStatusText.Text = "配置处于恢复保护状态；已阻止把空占位配置误报为健康策略。";
    }

    private void ResetPolicyExplanation()
    {
        _policyAdvice.Clear();
        PolicyAiSummaryText.Text = "AI 尚未解读；本地确定性发现始终优先于模型文字。";
    }

    private async Task<bool> PolicyReportMatchesCurrentAsync(
        PolicyAnalysisReport report,
        CancellationToken cancellationToken)
    {
        var snapshot = _service.Config;
        return await Task.Run(
            () => PolicyIntelligence.MatchesSnapshot(report, snapshot, cancellationToken),
            cancellationToken);
    }

    #endregion

    #region AI 路由推演

    private async void SimulateRoute_Click(object sender, RoutedEventArgs e)
    {
        if (_routeSimulationCts != null || _shutdownStarted) return;
        if (!_service.IsConfigurationWritable)
        {
            ShowProtectedRouteSimulationState();
            return;
        }

        var query = CreateRouteDecisionQuery();
        var snapshot = _service.Config;
        var version = ++_routeSimulationVersion;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        _routeSimulationCts = cts;
        BeginRouteSimulationUi();
        var currentTask = RunRouteSimulationAsync(snapshot, query, version, cts);
        _routeSimulationDrainTask = Task.WhenAll(_routeSimulationDrainTask, currentTask);
        await currentTask;
    }

    private async Task RunRouteSimulationAsync(
        AppConfig snapshot,
        RouteDecisionQuery query,
        int version,
        CancellationTokenSource cts)
    {
        try
        {
            var report = await Task.Run(
                () => PolicyIntelligence.SimulateRoute(snapshot, query, cts.Token),
                cts.Token);
            if (cts.IsCancellationRequested || version != _routeSimulationVersion || _shutdownStarted)
                return;

            if (!report.IsSnapshotBound)
            {
                _currentRouteDecisionReport = null;
                _currentRouteDecisionQuery = null;
                ShowRouteDecisionReport(report);
                return;
            }

            var stillCurrent = await Task.Run(
                () => PolicyIntelligence.MatchesRouteSnapshot(report, _service.Config, query, cts.Token),
                cts.Token);
            if (!stillCurrent)
            {
                InvalidateRouteDecision("推演期间配置已经变化；旧结果已隐藏，请重新推演。");
                return;
            }

            _currentRouteDecisionReport = report;
            _currentRouteDecisionQuery = query;
            ShowRouteDecisionReport(report);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (version == _routeSimulationVersion && !_shutdownStarted)
                RouteDecisionStatusText.Text = "本地推演已取消；配置和 sing-box 均未变化。";
        }
        catch (Exception ex)
        {
            if (version == _routeSimulationVersion && !_shutdownStarted)
            {
                InvalidateRouteDecision("本地推演失败；未生成路由结论。", markStale: false);
                RouteDecisionReasonText.Text = SingBoxRuntime.RedactSecrets(ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_routeSimulationCts, cts))
            {
                _routeSimulationCts = null;
                cts.Dispose();
                if (!_shutdownStarted)
                {
                    RouteSimulateButton.IsEnabled = _service.IsConfigurationWritable;
                    RouteDecisionCancelButton.IsEnabled = false;
                }
            }
        }
    }

    private void BeginRouteSimulationUi()
    {
        _currentRouteDecisionReport = null;
        _currentRouteDecisionQuery = null;
        _routeDecisionTrace.Clear();
        RouteDecisionTraceEmptyText.Visibility = Visibility.Visible;
        RouteDecisionTraceCountText.Text = "0 条";
        RouteDecisionBadgeText.Text = "推演中";
        RouteDecisionActionText.Text = "…";
        RouteDecisionSourceText.Text = "正在按生产规则的规范顺序进行有界求值。";
        RouteDecisionReasonText.Text = "尚未得出结论。";
        RouteDecisionSnapshotText.Text = "正在绑定配置快照与规范化查询。";
        RouteDecisionStatusText.Text = "正在后台执行本地静态 what-if；不会联网、探测代理或读取真实连接。";
        RouteDecisionLocateButton.IsEnabled = false;
        RouteSimulateButton.IsEnabled = false;
        RouteDecisionCancelButton.IsEnabled = true;
    }

    private void ShowRouteDecisionReport(RouteDecisionReport report)
    {
        _routeDecisionTrace.Clear();
        foreach (var step in report.Trace)
            _routeDecisionTrace.Add(RouteDecisionTraceLine.FromStep(step));
        RouteDecisionTraceCountText.Text = $"{report.Trace.Count} 条";
        RouteDecisionTraceEmptyText.Visibility = report.Trace.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        RouteDecisionBadgeText.Text = report.Kind switch
        {
            RouteDecisionKind.MatchedRule => "已证明 · 规则命中",
            RouteDecisionKind.GlobalFallback => "已证明 · 全局默认",
            RouteDecisionKind.Indeterminate => "信息不足",
            RouteDecisionKind.InvalidQuery => "输入无效",
            RouteDecisionKind.InvalidPolicy => "策略无效",
            _ => "未得出结论"
        };
        RouteDecisionActionText.Text = report.Action switch
        {
            ProxyMode.Proxy => "代理",
            ProxyMode.Direct => "直连",
            ProxyMode.Block => "阻止",
            _ => "未证明"
        };
        RouteDecisionSourceText.Text = report.Kind switch
        {
            RouteDecisionKind.MatchedRule =>
                $"规范顺序 #{report.MatchedEvaluationOrder}: {report.MatchedRuleDisplayName}",
            RouteDecisionKind.GlobalFallback => "所有活动规则均可明确排除，使用当前全局默认。",
            RouteDecisionKind.Indeterminate => $"在求值 {report.EvaluatedRuleCount} 条活动规则后保守停止。",
            RouteDecisionKind.InvalidQuery => "查询不满足版本 1 的具体输入契约。",
            RouteDecisionKind.InvalidPolicy => "生产配置构建器拒绝了当前策略。",
            _ => "没有可展示的决策来源。"
        };
        RouteDecisionReasonText.Text = GetRouteDecisionReasonText(report.Reason) +
            (string.IsNullOrWhiteSpace(report.Error)
                ? string.Empty
                : " " + SingBoxRuntime.RedactSecrets(report.Error));
        RouteDecisionSnapshotText.Text = string.IsNullOrWhiteSpace(report.Fingerprint)
            ? "无有效快照指纹。"
            : $"快照与查询指纹 {report.Fingerprint[..12]}…；已核对当前配置。";
        RouteDecisionLocateButton.IsEnabled = report.Kind == RouteDecisionKind.MatchedRule &&
            !string.IsNullOrWhiteSpace(report.MatchedRuleId);
        RouteDecisionStatusText.Text = report.Kind switch
        {
            RouteDecisionKind.MatchedRule or RouteDecisionKind.GlobalFallback =>
                "本地静态推演完成并得出可证明结论；这仍不是实际连接、DNS、代理连通性或真实流量证据。",
            RouteDecisionKind.Indeterminate =>
                "本地静态推演已保守停止；补充缺失的域名或解析后 IP 上下文前，不会猜测后续规则。",
            RouteDecisionKind.InvalidQuery =>
                "请修正输入后重试；未读取网络或运行时状态。",
            RouteDecisionKind.InvalidPolicy =>
                "当前策略无法按生产支持语义构建；未返回可能误导的动作。",
            _ => "未生成路由结论。"
        };
    }

    private RouteDecisionQuery CreateRouteDecisionQuery()
    {
        _ = int.TryParse(RoutePortBox.Text, out var port);
        return new RouteDecisionQuery(
            RouteProcessBox.Text,
            RouteDestinationKindCombo.SelectedIndex == 1 ? RouteDestinationKind.Ip : RouteDestinationKind.Domain,
            RouteDestinationBox.Text,
            port,
            RouteTransportCombo.SelectedIndex == 1 ? RouteTransport.Udp : RouteTransport.Tcp);
    }

    private void RouteDestinationKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (RouteDestinationLabel != null)
            RouteDestinationLabel.Text = RouteDestinationKindCombo.SelectedIndex == 1 ? "具体 IP 地址" : "具体域名";
        RouteQuery_Changed(sender, e);
    }

    private void RouteQuery_Changed(object sender, RoutedEventArgs e)
    {
        if (_currentRouteDecisionReport == null && _routeSimulationCts == null) return;
        _routeSimulationCts?.Cancel();
        _routeSimulationVersion++;
        InvalidateRouteDecision("输入已变化；旧结果已隐藏，请重新推演。", cancelActive: false);
    }

    private void CancelRouteSimulation_Click(object sender, RoutedEventArgs e) =>
        _routeSimulationCts?.Cancel();

    private void LocateRouteDecisionRule_Click(object sender, RoutedEventArgs e)
    {
        RefreshRouteDecisionFreshness();
        var ruleId = _currentRouteDecisionReport?.MatchedRuleId;
        if (string.IsNullOrWhiteSpace(ruleId)) return;

        NavRules.IsChecked = true;
        SearchBox.Text = string.Empty;
        ShowPage("NavRules");
        LoadRules();
        var rule = _rules.FirstOrDefault(item => item.Id == ruleId);
        if (rule == null) return;
        RulesList.SelectedItem = rule;
        RulesList.ScrollIntoView(rule);
        RulesList.Focus();
    }

    private void RefreshRouteDecisionFreshness()
    {
        if (_currentRouteDecisionReport == null || _currentRouteDecisionQuery == null) return;
        try
        {
            if (!PolicyIntelligence.MatchesRouteSnapshot(
                    _currentRouteDecisionReport,
                    _service.Config,
                    _currentRouteDecisionQuery,
                    _lifetimeCts.Token))
            {
                InvalidateRouteDecision("配置已变化；旧结果已隐藏，请重新推演。");
            }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Window shutdown superseded the freshness check.
        }
    }

    private void InvalidateRouteDecision(
        string status,
        bool markStale = true,
        bool cancelActive = true)
    {
        if (cancelActive) _routeSimulationCts?.Cancel();
        _currentRouteDecisionReport = null;
        _currentRouteDecisionQuery = null;
        _routeDecisionTrace.Clear();
        if (RouteDecisionTraceEmptyText == null) return;
        RouteDecisionTraceEmptyText.Visibility = Visibility.Visible;
        RouteDecisionTraceCountText.Text = "0 条";
        RouteDecisionBadgeText.Text = markStale ? "需要重新推演" : "推演失败";
        RouteDecisionActionText.Text = "—";
        RouteDecisionSourceText.Text = "未展示旧决策。";
        RouteDecisionReasonText.Text = status;
        RouteDecisionSnapshotText.Text = "未绑定当前配置快照。";
        RouteDecisionLocateButton.IsEnabled = false;
        RouteDecisionStatusText.Text = status;
    }

    private void ShowProtectedRouteSimulationState()
    {
        _routeSimulationVersion++;
        InvalidateRouteDecision(
            "配置处于恢复保护状态；已阻止对空占位配置进行推演。请先恢复或明确重置配置。",
            markStale: false);
        RouteDecisionBadgeText.Text = "配置保护中";
        RouteSimulateButton.IsEnabled = false;
        RouteDecisionCancelButton.IsEnabled = false;
    }

    private static string GetRouteDecisionReasonText(RouteDecisionReason reason) => reason switch
    {
        RouteDecisionReason.Matched => "已满足首条获胜规则或全局默认的全部可证明条件。",
        RouteDecisionReason.ProcessMismatch => "进程名称明确不匹配。",
        RouteDecisionReason.TransportMismatch => "TCP/UDP 条件明确不匹配。",
        RouteDecisionReason.PortMismatch => "目标端口明确不在规则范围内。",
        RouteDecisionReason.DestinationMismatch => "具体目标明确不匹配该规则的目标条件。",
        RouteDecisionReason.ResolvedIpRequired => "该较早规则还包含 IP/CIDR 条件；没有解析后 IP 就不能排除它。",
        RouteDecisionReason.DomainContextRequired => "该较早规则还包含域名条件；没有域名上下文就不能排除它。",
        RouteDecisionReason.EvaluationBudgetExceeded => "活动规则超过单次 500 条的有界求值预算。",
        RouteDecisionReason.InvalidQuery => "必须提供精确进程名、具体域名或字面量 IP、1–65535 端口，以及 TCP 或 UDP。",
        RouteDecisionReason.InvalidPolicy => "当前配置未通过与生产构建器相同的支持语义校验。",
        _ => "无法确定本次判定依据。"
    };

    #endregion

    #region 规则管理

    private void LoadRules()
    {
        _allRules = PolicyRuntimeOrder.All(_service.Config.Rules).ToList();
        ApplyFilter();
        UpdateStats();
        if (IsLoaded)
            InvalidateRouteDecision("配置规则已刷新；请基于当前快照重新推演。");
        _ = RefreshPolicyAnalysisAsync();
    }

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(_searchFilter)
            ? _allRules
            : _allRules.Where(r =>
                (r.ExeName ?? string.Empty).Contains(_searchFilter, StringComparison.OrdinalIgnoreCase) ||
                (r.ExePath ?? string.Empty).Contains(_searchFilter, StringComparison.OrdinalIgnoreCase))
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
            _service.ClearRules();
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
                var json = AppConfigStore.ReadStrictUtf8(dialog.FileName);
                var import = Newtonsoft.Json.JsonConvert.DeserializeObject<ImportData>(json);
                if (import?.Rules != null)
                {
                    var added = _service.ImportRules(import.Rules);
                    LoadRules();
                    MessageBox.Show($"导入完成: 新增 {added} 条规则", "成功");
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
                    Version = "0.3.0",
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
        if (IsLoaded)
            InvalidateRouteDecision("全局默认已变化；请基于当前快照重新推演。");
        _ = RefreshPolicyAnalysisAsync();
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
        var rules = PolicyRuntimeOrder.Enabled(_service.Config.Rules)
            .Select(item => item.Rule)
            .ToList();
        var list = processes
            .OrderBy(p => p.Value)
            .Select(p => new
            {
                Pid = p.Key,
                Name = p.Value,
                Path = "",
                Status = GetConfiguredCandidate(rules, p.Value)
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
        var proxy = _service.GetPrimaryProxy();
        ProxyHost.Text = proxy?.Host ?? config.SocksHost;
        ProxyPort.Text = (proxy?.Port ?? config.SocksPort).ToString();
        ProxyUsername.Text = proxy?.Username ?? string.Empty;
        ProxyPassword.Password = proxy?.Password ?? string.Empty;
        ProxyTypeCombo.SelectedIndex = (proxy?.ProxyType ?? ProxyType.Socks5) switch
        {
            ProxyType.Http => 1,
            ProxyType.Https => 2,
            _ => 0
        };
        RuntimePathBox.Text = config.SingBoxExecutablePath;
        ModeProxy.IsChecked = config.GlobalMode == GlobalMode.ProxyAll;
        ModeDirect.IsChecked = config.GlobalMode == GlobalMode.DirectAll;
    }

    private void SaveProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ProxyPort.Text, out var port))
        {
            MessageBox.Show("请输入有效的端口号", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var proxyType = GetSelectedProxyType();
        try
        {
            _service.UpdatePrimaryProxy(
                proxyType,
                ProxyHost.Text,
                port,
                ProxyUsername.Text,
                ProxyPassword.Password);
            InvalidateRouteDecision("代理配置已变化；请基于当前快照重新推演。");
            _ = RefreshPolicyAnalysisAsync();
            ProxyHost.Text = LocalProxyEndpoint.NormalizeOrThrow(ProxyHost.Text, port);
            StatusDetail.Text = $"本地 {proxyType} 代理已保存: {ProxyHost.Text}:{port}";
            ProxyTestStatus.Text = "设置已保存。端口测试仍需单独执行，保存不代表代理可用。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "无法保存代理设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(ProxyPort.Text, out var port))
        {
            ProxyTestStatus.Text = "请输入有效的本地代理端口。";
            return;
        }

        if (!LocalProxyEndpoint.TryNormalize(ProxyHost.Text, port, out var normalizedHost, out var error))
        {
            ProxyTestStatus.Text = error;
            return;
        }

        TestProxyButton.IsEnabled = false;
        ProxyTestStatus.Text = $"正在检查 {normalizedHost}:{port} 的 TCP 端口…";
        try
        {
            var connected = await _service.TestLocalProxyAsync(normalizedHost, port);
            ProxyTestStatus.Text = connected
                ? "本机端口可以连接。此结果不验证代理协议、账号密码或真实互联网流量。"
                : "无法连接本机端口。请先确认代理程序正在监听该地址和端口。";
        }
        finally
        {
            TestProxyButton.IsEnabled = _service.IsConfigurationWritable;
        }
    }

    private async void BrowseSingBox_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择已单独安装的 sing-box 可执行文件",
            Filter = "sing-box 可执行文件 (sing-box.exe)|sing-box.exe|可执行文件 (*.exe)|*.exe"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _service.SetSingBoxExecutablePath(dialog.FileName);
            RuntimePathBox.Text = Path.GetFullPath(dialog.FileName);
            await RefreshRuntimeReadinessAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            MessageBox.Show(ex.Message, "无法使用 sing-box", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RefreshRuntime_Click(object sender, RoutedEventArgs e) =>
        await RefreshRuntimeReadinessAsync();

    private async void ClearSingBoxPath_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _service.ClearSingBoxExecutablePath();
            RuntimePathBox.Text = string.Empty;
            await RefreshRuntimeReadinessAsync();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "无法更改 sing-box 路径", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AiHealthCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_shutdownStarted) return;
        var button = (Button)sender;
        button.IsEnabled = false;
        AiHealthText.Text = "正在检查…";
        try
        {
            var provider = GetSelectedAiProvider();
            var model = AiModelCombo.SelectedItem as string;
            var check = await AiProviderDiagnostics.CheckAsync(provider, model, _lifetimeCts.Token);
            var providerLabel = check.Kind == AiProviderKind.OpenAI ? "OpenAI" : "Ollama";
            var stateLabel = check.State switch
            {
                AiProviderHealthState.Ready => "就绪",
                AiProviderHealthState.NotConfigured => "未配置",
                AiProviderHealthState.Misconfigured => "配置不完整",
                AiProviderHealthState.Unreachable => "本地服务不可达",
                _ => "未知状态"
            };
            AiHealthText.Text = $"{providerLabel}：{stateLabel}\n{string.Join("\n", check.Details)}";
        }
        catch (AiProviderException ex)
        {
            AiHealthText.Text = ex.Message;
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            // Shutdown during diagnostics keeps the last visible state.
        }
        catch (Exception ex)
        {
            AiHealthText.Text = $"诊断未能完成：{SingBoxRuntime.RedactSecrets(ex.Message)}";
        }
        finally
        {
            if (!_shutdownStarted) button.IsEnabled = true;
        }
    }

    private async Task RefreshRuntimeReadinessAsync()
    {
        if (!_service.IsConfigurationWritable)
        {
            RuntimeReadinessText.Text = "配置保护期间不会检查或启动 sing-box。请先完成配置恢复。";
            RuntimeVersionText.Text = "版本：已阻止检查";
            return;
        }

        RefreshRuntimeButton.IsEnabled = false;
        RuntimeReadinessText.Text = "正在读取实际路径和版本…";
        _runtimeReadinessCts?.Cancel();
        _runtimeReadinessCts?.Dispose();
        var cts = new CancellationTokenSource();
        _runtimeReadinessCts = cts;
        try
        {
            var readiness = await _service.ProbeRuntimeReadinessAsync(cts.Token);
            RuntimePathBox.Text = readiness.ExecutablePath ?? _service.Config.SingBoxExecutablePath;
            RuntimeVersionText.Text = "版本：" + (readiness.Version ?? "未识别");
            RuntimeReadinessText.Text = readiness.IsReady
                ? "已就绪：版本满足 v1.13+。规则变更仍会先执行 sing-box check。"
                : readiness.Error ?? "sing-box 未就绪。";
            if (!_service.GetRuntimeStatus().IsRunning)
            {
                StatusDetail.Text = readiness.IsReady
                    ? "sing-box 已批准且版本兼容；正在等待或应用当前配置。"
                    : readiness.Error ?? "sing-box 未就绪。";
            }
        }
        catch (OperationCanceledException)
        {
            RuntimeReadinessText.Text = "sing-box 就绪检查已取消。";
        }
        finally
        {
            if (ReferenceEquals(_runtimeReadinessCts, cts))
            {
                _runtimeReadinessCts = null;
                cts.Dispose();
            }
            RefreshRuntimeButton.IsEnabled = _service.IsConfigurationWritable;
        }
    }

    private ProxyType GetSelectedProxyType() => ProxyTypeCombo.SelectedIndex switch
    {
        1 => ProxyType.Http,
        2 => ProxyType.Https,
        _ => ProxyType.Socks5
    };

    private void UpdateConfigurationUi()
    {
        var writable = _service.IsConfigurationWritable;
        ConfigRecoveryBanner.Visibility = writable ? Visibility.Collapsed : Visibility.Visible;
        PageRules.IsEnabled = writable;
        PageAiAssistant.IsEnabled = writable;
        PagePolicyIntelligence.IsEnabled = writable;
        PageRouteSimulator.IsEnabled = writable;
        RuntimeSettingsCard.IsEnabled = writable;
        ProxySettingsCard.IsEnabled = writable;
        ModeDirect.IsEnabled = writable;
        ModeProxy.IsEnabled = writable;

        if (!writable)
        {
            ShowProtectedRouteSimulationState();
            var backup = _service.ConfigurationRecoveryBackupPath;
            var recoveryCopyAvailable = !string.IsNullOrWhiteSpace(backup) && File.Exists(backup);
            ResetConfigButton.IsEnabled = recoveryCopyAvailable;
            RecoverConfigButton.IsEnabled = recoveryCopyAvailable;
            ResetConfigButton.ToolTip = ResetConfigButton.IsEnabled
                ? "恢复副本存在；确认后可以重置活动配置。"
                : "恢复副本不可用。为避免覆盖唯一原件，重置已禁用；请先手动复制原文件并重新启动应用。";
            RecoverConfigButton.ToolTip = RecoverConfigButton.IsEnabled
                ? "先验证所选配置的安全语义，再替换活动配置。"
                : "恢复副本不可用。为避免覆盖唯一原件，导入替换已禁用；请先手动复制原文件并重新启动应用。";
            var reason = string.IsNullOrWhiteSpace(_service.ConfigurationError)
                ? string.Empty
                : "原因：" + _service.ConfigurationError + " ";
            ConfigRecoveryText.Text = string.IsNullOrWhiteSpace(backup)
                ? reason + "配置文件不可安全读取，且无法创建恢复副本。原文件仍未被修改；所有保存、导入替换、重置和 sing-box 启动均已阻止。请先手动复制原文件并重新启动应用。"
                : reason + $"配置文件不可安全读取。原文件未被修改，恢复副本位于：{backup}。所有保存和 sing-box 启动均已阻止。";
            StatusDetail.Text = "配置保护已启动；等待用户恢复";
        }
        else
        {
            ResetConfigButton.IsEnabled = false;
            RecoverConfigButton.IsEnabled = false;
            RouteSimulateButton.IsEnabled = _routeSimulationCts == null;
        }
    }

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add(_service.ConfigDirectory);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
        }
        catch
        {
            MessageBox.Show(_service.ConfigDirectory, "配置目录", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void RecoverConfig_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入有效的 IntentRoute AI 配置",
            Filter = "JSON 文件 (*.json)|*.json"
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            _service.RecoverConfigurationFromFile(dialog.FileName);
            UpdateConfigurationUi();
            LoadSettings();
            LoadRules();
            await RefreshRuntimeReadinessAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Newtonsoft.Json.JsonException or AppConfigProtectionException)
        {
            MessageBox.Show("所选文件无法安全读取，现有配置仍保持保护状态。\n\n" + ex.Message,
                "恢复失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ResetConfig_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "这会用全新的默认配置替换当前损坏的 config.json。恢复副本会保留。是否继续？",
            "确认重置配置",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return;

        try
        {
            _service.ResetUnusableConfiguration();
            UpdateConfigurationUi();
            LoadSettings();
            LoadRules();
            await RefreshRuntimeReadinessAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "重置失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void UpdateRuntimeStatusUi(SingBoxRuntimeStatus status)
    {
        var brushKey = status.State switch
        {
            SingBoxRuntimeState.Running when status.IsRunning => "SuccessBrush",
            SingBoxRuntimeState.RunningStale when status.IsRunning => "WarningBrush",
            SingBoxRuntimeState.Probing or SingBoxRuntimeState.Starting or SingBoxRuntimeState.Checking => "WarningBrush",
            SingBoxRuntimeState.Failed => "DangerBrush",
            _ => "TextMutedBrush"
        };
        SetStatusBrush(brushKey);
        if (!string.IsNullOrWhiteSpace(status.ExecutablePath))
            RuntimePathBox.Text = status.ExecutablePath;
        if (!string.IsNullOrWhiteSpace(status.Version))
            RuntimeVersionText.Text = "版本：" + status.Version;
    }

    private void SetStatusBrush(string resourceKey)
    {
        if (FindResource(resourceKey) is Brush brush)
            StatusDot.Fill = brush;
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

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        base.OnClosing(e);
        if (_shutdownStarted) return;
        _shutdownStarted = true;
        IsEnabled = false;
        StatusDetail.Text = "正在安全停止 sing-box…";
        _lifetimeCts.Cancel();
        _aiModelRefreshVersion++;
        _policyModelRefreshVersion++;
        _policyAnalysisVersion++;
        _routeSimulationVersion++;
        _aiGenerationCts?.Cancel();
        _policyAnalysisCts?.Cancel();
        _policyExplanationCts?.Cancel();
        _routeSimulationCts?.Cancel();
        _runtimeReadinessCts?.Cancel();
        try
        {
            await Task.WhenAll(_policyAnalysisDrainTask, _routeSimulationDrainTask);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("IntentRoute AI policy-analysis shutdown failed: " + SingBoxRuntime.RedactSecrets(ex.Message));
        }
        try
        {
            await _service.DisposeAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("IntentRoute AI runtime shutdown failed: " + SingBoxRuntime.RedactSecrets(ex.Message));
        }

        try
        {
            await _aiProviderGate.WaitAsync();
            try
            {
                _openAiProvider.Dispose();
                _ollamaProvider.Dispose();
            }
            finally
            {
                _aiProviderGate.Release();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("IntentRoute AI provider shutdown failed: " + SingBoxRuntime.RedactSecrets(ex.Message));
        }
        finally
        {
            _aiGenerationCts?.Dispose();
            _policyExplanationCts?.Dispose();
            _routeSimulationCts?.Dispose();
            _runtimeReadinessCts?.Dispose();
            _lifetimeCts.Dispose();
            _aiProviderGate.Dispose();
            _shutdownComplete = true;
            _shutdownStarted = false;
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private void PostToUi(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess())
                action();
            else
                _ = Dispatcher.BeginInvoke(action);
        }
        catch
        {
            // The window may already be shutting down; runtime cleanup must not wait on UI dispatch.
        }
    }

    private static string GetConfiguredCandidate(IEnumerable<ProxyRule> rules, string processName)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.ExeName, "*", StringComparison.Ordinal) ||
            string.Equals(r.ExeName, processName, StringComparison.OrdinalIgnoreCase));
        return rule?.Mode switch
        {
            ProxyMode.Proxy => "存在代理候选",
            ProxyMode.Direct => "存在直连候选",
            ProxyMode.Block => "存在阻止候选",
            _ => "无进程名候选"
        };
    }

    private sealed record RuntimeLogLine(string Time, string Message);

    private sealed record RouteDecisionTraceLine(
        string Order,
        string RuleName,
        string Evaluation,
        string Reason)
    {
        public static RouteDecisionTraceLine FromStep(RouteDecisionTraceStep step) => new(
            $"#{step.EvaluationOrder}",
            step.DisplayName,
            step.Evaluation switch
            {
                RouteRuleEvaluation.ProvenMatch => "命中",
                RouteRuleEvaluation.ProvenMiss => "不命中",
                RouteRuleEvaluation.Indeterminate => "信息不足",
                _ => "未知"
            },
            GetRouteDecisionReasonText(step.Reason));
    }

    private sealed record AiRulePreviewLine(
        string ProcessName,
        string Action,
        string Conditions,
        string Rationale,
        string Confidence)
    {
        public static AiRulePreviewLine FromDraft(AiRuleDraft draft)
        {
            var conditions = new List<string>();
            if (!string.IsNullOrWhiteSpace(draft.TargetHosts)) conditions.Add("域名: " + draft.TargetHosts);
            if (!string.IsNullOrWhiteSpace(draft.TargetIps)) conditions.Add("IP: " + draft.TargetIps);
            if (!string.IsNullOrWhiteSpace(draft.TargetPorts)) conditions.Add("端口: " + draft.TargetPorts);
            conditions.Add("协议: " + draft.Protocol);
            return new AiRulePreviewLine(
                draft.ProcessName,
                draft.Action,
                string.Join(" | ", conditions),
                draft.Rationale,
                draft.Confidence.ToString("P0"));
        }
    }

    private sealed record PolicyFindingPreviewLine(
        string Severity,
        string Code,
        string Title,
        string AffectedRules,
        string Guidance,
        string? PrimaryRuleId)
    {
        public static PolicyFindingPreviewLine FromFinding(PolicyFinding finding)
        {
            var severity = finding.Severity switch
            {
                PolicyFindingSeverity.Critical => "高风险",
                PolicyFindingSeverity.Warning => "复核",
                _ => "提示"
            };
            var affected = finding.Rules.Count == 0
                ? "全局默认"
                : string.Join(" → ", finding.Rules.Select(rule =>
                    rule.EvaluationOrder.HasValue
                        ? $"#{rule.EvaluationOrder} {rule.DisplayName}"
                        : $"禁用 {rule.DisplayName}"));
            return new PolicyFindingPreviewLine(
                severity,
                finding.Code,
                finding.Title,
                affected,
                finding.Detail + " " + finding.Recommendation,
                finding.Rules.FirstOrDefault()?.RuleId);
        }
    }

    private sealed record AiPolicyAdviceLine(
        string Code,
        string Explanation,
        string SafeNextStep,
        string Confidence)
    {
        public static AiPolicyAdviceLine FromPriority(AiPolicyPriority priority) => new(
            priority.FindingCode,
            priority.Explanation,
            priority.SafeNextStep,
            priority.Confidence.ToString("P0"));
    }
}
