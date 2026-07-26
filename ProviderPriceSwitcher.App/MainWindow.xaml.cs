using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly JsonSettingsRepository _settingsRepository;
    private readonly PricingRefreshService _refreshService;
    private readonly OmpConfigurationSwitcher _switcher;
    private readonly OmpProcessService _processService;
    private LocalAppSettings _settings;
    private CancellationTokenSource? _checkCancellation;
    private string _statusText = "准备就绪";
    private string _currentProvider = "未读取";
    private string _recommendedProvider = "等待检查";
    private string _lastCheckedText = "尚未检查";
    private ProviderChoice? _selectedProvider;
    private string? _selectedWorkingDirectory;
    private PricingRefreshResult? _lastResult;

    public MainViewModel(JsonSettingsRepository settingsRepository, PricingRefreshService refreshService, OmpConfigurationSwitcher switcher, OmpProcessService processService, LocalAppSettings settings)
    {
        _settingsRepository = settingsRepository;
        _refreshService = refreshService;
        _switcher = switcher;
        _processService = processService;
        _settings = settings;
        CheckCommand = new AsyncCommand(CheckAsync, () => _checkCancellation is null);
        CancelCommand = new RelayCommand(() => _checkCancellation?.Cancel(), () => _checkCancellation is not null);
        SwitchAndStartCommand = new AsyncCommand(SwitchAndStartAsync, () => SelectedProvider is not null && !string.IsNullOrWhiteSpace(SelectedWorkingDirectory));
        ManageSitesCommand = new RelayCommand(ManageSites);
        SettingsCommand = new RelayCommand(EditSettings);
        SyncSettings();
    }

    public ObservableCollection<PriceRow> Rows { get; } = [];
    public ObservableCollection<ProviderChoice> ProviderChoices { get; } = [];
    public ObservableCollection<string> WorkingDirectories { get; } = [];
    public AsyncCommand CheckCommand { get; } public RelayCommand CancelCommand { get; } public AsyncCommand SwitchAndStartCommand { get; }
    public RelayCommand ManageSitesCommand { get; } public RelayCommand SettingsCommand { get; }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string CurrentProvider { get => _currentProvider; private set => SetProperty(ref _currentProvider, value); }
    public string RecommendedProvider { get => _recommendedProvider; private set => SetProperty(ref _recommendedProvider, value); }
    public string LastCheckedText { get => _lastCheckedText; private set => SetProperty(ref _lastCheckedText, value); }
    public ProviderChoice? SelectedProvider { get => _selectedProvider; set { if (SetProperty(ref _selectedProvider, value)) { OnPropertyChanged(nameof(SelectionHint)); SwitchAndStartCommand.RaiseCanExecuteChanged(); } } }
    public string? SelectedWorkingDirectory { get => _selectedWorkingDirectory; set { if (SetProperty(ref _selectedWorkingDirectory, value)) SwitchAndStartCommand.RaiseCanExecuteChanged(); } }
    public static string SelectionHint => "低分组仅为价格提示，不阻止切换。";

    public async Task InitializeAsync()
    {
        await ReadCurrentProviderAsync();
        LoadPersistedPrices();
    }

    private async Task ReadCurrentProviderAsync()
    { try { if (File.Exists(_settings.OmpConfigPath)) { var preview = _switcher.Preview(await File.ReadAllTextAsync(_settings.OmpConfigPath), "temporary"); CurrentProvider = preview.CurrentProvider ?? "未识别"; } else CurrentProvider = "配置文件不存在"; } catch (Exception ex) { CurrentProvider = "读取失败"; StatusText = "无法读取 OMP 配置：" + ex.Message; } }

    private async Task CheckAsync()
    {
        if (_checkCancellation is not null) return;
        _checkCancellation = new CancellationTokenSource(); CheckCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); StatusText = "正在检查已启用站点的价格…";
        try
        {
            await ReadCurrentProviderAsync();
            _lastResult = await _refreshService.RefreshAsync(_settings, LocalAppSettings.DefaultUsageProfile, CurrentProvider, _checkCancellation.Token);
            ApplyAutomaticRatios(_lastResult);
            MapResult(_lastResult, LocalAppSettings.DefaultUsageProfile);
            LastCheckedText = _lastResult.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); StatusText = "检查完成。";
        }
        catch (OperationCanceledException) { StatusText = "已取消检查。"; }
        catch (Exception ex) { StatusText = "检查价格失败：" + ex.Message; MessageBox.Show(StatusText, "检查失败", MessageBoxButton.OK, MessageBoxImage.Warning); }
        finally { _checkCancellation.Dispose(); _checkCancellation = null; CheckCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); SwitchAndStartCommand.RaiseCanExecuteChanged(); }
    }

    private void MapResult(PricingRefreshResult result, UsageProfile usage)
    {
        Rows.Clear();
        var previousSelection = SelectedProvider?.ProviderId;
        foreach (var site in _settings.Sites)
        {
            var state = result.Sites.FirstOrDefault(x => x.ProviderId == site.ProviderId);
            var pricing = state?.PricingResult;
            var snapshot = pricing?.Snapshot ?? state?.PreviousSnapshot ?? result.LatestSnapshots.GetValueOrDefault(site.ProviderId);
            AddRows(site, snapshot, usage, state, pricing);
        }
        var recommended = result.Recommendation.Selected?.Site.ProviderId;
        RecommendedProvider = recommended ?? "无可自动推荐项";
        LoadProviderChoices(recommended ?? previousSelection);
    }

    private void LoadPersistedPrices(string? preferredProvider = null)
    {
        var snapshots = _refreshService.LoadSnapshots();
        var usage = LocalAppSettings.DefaultUsageProfile;
        Rows.Clear();
        DateTimeOffset? latest = null;
        foreach (var site in _settings.Sites)
        {
            var snapshot = snapshots.GetValueOrDefault(site.ProviderId);
            AddRows(site, snapshot, usage, null, null);
            if (snapshot is not null && (latest is null || snapshot.RefreshedAt > latest)) latest = snapshot.RefreshedAt;
        }
        LastCheckedText = latest?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "尚未检查";
        RecommendedProvider = "等待手动检查";
        LoadProviderChoices(preferredProvider ?? CurrentProvider);
        StatusText = snapshots.Count > 0 ? "已加载上次检查结果；点击“检查价格”更新。" : "尚无价格记录；点击“检查价格”。";
    }

    private void AddRows(SiteConfiguration site, PricingSnapshot? snapshot, UsageProfile usage, PricingRefreshSiteResult? state, ProviderPriceSwitcher.Adapters.SitePricingResult? pricing)
    {
        var unavailable = snapshot is not null && !snapshot.Matches(site);
        var failed = state is not null && state.Status == PricingRefreshSiteStatus.Failed;
        var stale = unavailable || failed;
        var warning = pricing?.Warnings.Count > 0;
        var status = state is null
            ? site.Enabled ? snapshot is null ? "未检查" : unavailable ? "不可用" : "已保存" : "已禁用"
            : state.Status == PricingRefreshSiteStatus.Succeeded ? warning ? "成功（警告）" : "成功"
            : state.Status == PricingRefreshSiteStatus.Disabled ? "已禁用"
            : state.FailureKind == PricingRefreshFailureKind.Authentication ? "需认证"
            : "失败";
        var issue = state?.FailureMessage ?? (warning ? string.Join("；", pricing!.Warnings) : string.Empty);
        if (snapshot is null)
        {
            Rows.Add(new PriceRow(site.ProviderId, status, site.CurrentGroup + " [当前]", site.CurrentGroupRatio, null, usage, "—", site.GroupRatioSource, null, issue, stale, warning, true));
            return;
        }
        var effective = site.CurrentGroupRatio is > 0
            ? snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource)
            : snapshot;
        var minimumSame = string.Equals(effective.MinimumGroup, site.CurrentGroup, StringComparison.Ordinal);
        var currentLabel = site.CurrentGroup + (minimumSame ? " [当前][最低]" : " [当前]");
        Rows.Add(new PriceRow(site.ProviderId, status, currentLabel, effective.CurrentGroupRatio ?? site.CurrentGroupRatio, effective.Prices, usage, "—", effective.GroupRatioSource, effective.RefreshedAt, issue, stale, warning, true));
        if (!minimumSame && !string.IsNullOrWhiteSpace(effective.MinimumGroup) && effective.MinimumGroupPrices is not null)
            Rows.Add(new PriceRow(site.ProviderId, status, effective.MinimumGroup + " [最低]", effective.MinimumGroupRatio, effective.MinimumGroupPrices, usage, "—", "自动", effective.RefreshedAt, state?.FailureKind == PricingRefreshFailureKind.Authentication ? "需先绑定或更新凭据" : "权限未验证", stale, warning));
    }


    private void ApplyAutomaticRatios(PricingRefreshResult result)
    {
        var changed = false;
        var sites = _settings.Sites.Select(site =>
        {
            if (!result.SuccessfulResults.TryGetValue(site.ProviderId, out var pricing) || pricing.Snapshot.CurrentGroupRatio is not > 0) return site;
            changed = true;
            return site with { CurrentGroupRatio = pricing.Snapshot.CurrentGroupRatio, GroupRatioSource = "自动" };
        }).ToList();
        if (!changed) return;
        _settings = _settings with { Sites = sites };
        _settingsRepository.Save(_settings);
    }

    private async Task SwitchAndStartAsync()
    {
        var choice = SelectedProvider; if (choice is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(_settings.OmpConfigPath); var preview = _switcher.Preview(text, choice.ProviderId);
            if (!preview.IsValid) { StatusText = "无法生成切换预览：" + preview.Error; return; }
            var switched = await _switcher.SwitchFileAsync(_settings.OmpConfigPath, choice.ProviderId);
            if (!switched.Succeeded) { StatusText = "配置切换失败，OMP 未启动：" + switched.Error; return; }
            var directory = SelectedWorkingDirectory!; _settings = _settings with { LastOmpWorkingDirectory = directory, OmpWorkingDirectories = _settings.OmpWorkingDirectories.Append(directory).Distinct(StringComparer.OrdinalIgnoreCase).ToList() }; _settingsRepository.Save(_settings); SyncSettings();
            var start = _processService.Start(new OmpProcessStartRequest(directory));
            StatusText = start.Succeeded
                ? (start.ExistingProcess.Exists ? "检测到已有 OMP 进程；已请求启动新进程。" : "OMP 已启动。")
                : "配置已切换，但 OMP 启动失败；配置未回滚。" + start.ErrorMessage;
        }
        catch (Exception ex) { StatusText = "执行切换时发生错误：" + ex.Message; }
    }

    private void ManageSites() { var previousSelection = SelectedProvider?.ProviderId; var dialog = new SitesDialog(_settings, _settingsRepository, _refreshService, CurrentProvider); dialog.ShowDialog(); _settings = dialog.Settings; SyncSettings(); LoadPersistedPrices(previousSelection); }
    private async void EditSettings() { var dialog = new SettingsDialog(_settings); if (dialog.ShowDialog() == true) { _settings = dialog.Settings; _settingsRepository.Save(_settings); SyncSettings(); await ReadCurrentProviderAsync(); LoadPersistedPrices(); } }
    private void LoadProviderChoices(string? preferredProvider)
    {
        ProviderChoices.Clear();
        foreach (var providerId in _settings.Sites.Select(x => x.ProviderId).Distinct(StringComparer.Ordinal))
            ProviderChoices.Add(new ProviderChoice(providerId));
        SelectedProvider = ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, preferredProvider, StringComparison.Ordinal))
            ?? ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, CurrentProvider, StringComparison.Ordinal))
            ?? ProviderChoices.FirstOrDefault();
    }
    private void SyncSettings() { WorkingDirectories.Clear(); foreach (var item in _settings.OmpWorkingDirectories.Distinct(StringComparer.OrdinalIgnoreCase)) WorkingDirectories.Add(item); SelectedWorkingDirectory = WorkingDirectories.FirstOrDefault(x => string.Equals(x, _settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase)) ?? WorkingDirectories.FirstOrDefault(); }
}

public sealed record ProviderChoice(string ProviderId) { public string Display => ProviderId; }
public sealed class PriceRow
{
    public PriceRow(string providerId, string status, string group, decimal? ratio, TokenPrices? prices, UsageProfile usage, string cacheHitRate, string ratioSource, DateTimeOffset? checkedAt, string issue, bool isStale, bool hasWarning, bool isSiteFirstRow = false)
    { ProviderId = providerId; Status = status; Group = group; Ratio = ratio?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—"; InputPrice = prices?.InputPerMillion.ToString("0.####") ?? "—"; CachedPrice = prices?.CachedInputPerMillion.ToString("0.####") ?? "—"; OutputPrice = prices?.OutputPerMillion.ToString("0.####") ?? "—"; EstimatedCost = prices is null ? "—" : PricingCalculator.Calculate(usage, prices).ToString("0.####"); CacheHitRate = cacheHitRate; RatioSource = string.IsNullOrWhiteSpace(ratioSource) ? "—" : ratioSource; UpdatedAt = checkedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"; Issue = issue; IsStale = isStale; HasWarning = hasWarning; IsSiteFirstRow = isSiteFirstRow; }
    public string ProviderId { get; } public string Status { get; } public string Group { get; } public string Ratio { get; } public string InputPrice { get; } public string CachedPrice { get; } public string OutputPrice { get; } public string EstimatedCost { get; } public string CacheHitRate { get; } public string RatioSource { get; } public string UpdatedAt { get; } public string Issue { get; } public bool IsStale { get; } public bool HasWarning { get; } public bool IsSiteFirstRow { get; }
}