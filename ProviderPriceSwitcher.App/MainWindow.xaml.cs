using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Infrastructure;

namespace ProviderPriceSwitcher.App;

public interface IExternalUriLauncher
{
    void Launch(Uri uri);
}

public sealed class ShellUriLauncher : IExternalUriLauncher
{
    public void Launch(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

public partial class MainWindow : Window
{
    private readonly IExternalUriLauncher _uriLauncher;

    public MainWindow(MainViewModel viewModel, IExternalUriLauncher? uriLauncher = null)
    {
        InitializeComponent();
        _uriLauncher = uriLauncher ?? new ShellUriLauncher();
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.InitializeCommand.Execute(null);
    }

    private void PriceGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGrid grid)
            TryLaunchPriceRow(grid, e.OriginalSource, e.ChangedButton);
    }

    internal bool TryLaunchPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, System.Windows.Input.MouseButton button)
    {
        if (button != System.Windows.Input.MouseButton.Left || !TryGetHitPriceRow(grid, originalSource, out var row) || row is not { IsSiteFirstRow: true, KeysUri: not null })
            return false;

        _uriLauncher.Launch(row.KeysUri);
        return true;
    }

    internal static bool TryGetHitPriceRow(System.Windows.Controls.DataGrid grid, object? originalSource, out PriceRow? row)
    {
        row = null;
        if (originalSource is not System.Windows.DependencyObject source)
            return false;

        var hitRow = System.Windows.Controls.ItemsControl.ContainerFromElement(grid, source) as System.Windows.Controls.DataGridRow;
        row = hitRow?.DataContext as PriceRow;
        return row is not null;
    }

    public static Uri? BuildKeysUri(Uri? baseUrl, string? configurationApiAddress)
    {
        if (baseUrl is null || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)) return null;
        var address = string.IsNullOrWhiteSpace(configurationApiAddress) ? "/keys" : configurationApiAddress;
        if (!address.StartsWith('/') || address.StartsWith("//", StringComparison.Ordinal) || address.Contains('?') || address.Contains('#')) return null;
        var builder = new UriBuilder(baseUrl) { Query = string.Empty, Fragment = string.Empty };
        builder.Path = builder.Path.TrimEnd('/') + address;
        return builder.Uri;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly IPricingSnapshotQuery _snapshotQuery;
    private readonly IOmpCurrentProviderQuery _currentProviderQuery;
    private readonly IPricingAdapterRegistry _adapterRegistry;
    private readonly PricingCheckUseCase _pricingCheck;
    private readonly SettingsUseCase _settingsUseCase;
    private readonly SwitchAndStartUseCase _switchAndStart;
    private readonly IUserNotificationService _notifications;
    private readonly ISitesDialogFactory _sitesDialogFactory;
    private readonly ILogger<MainViewModel> _logger;
    private static readonly Action<ILogger, string, Exception?> LogUiFailure =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(200, "UiFailure"), "UI operation failed: {FailureKind}");
    private LocalAppSettings _settings;
    private CancellationTokenSource? _checkCancellation;
    private string _statusText = "准备就绪";
    private string _currentProvider = "未读取";
    private string _recommendedProvider = "等待检查";
    private string _lastCheckedText = "尚未检查";
    private ProviderChoice? _selectedProvider;
    private string? _selectedWorkingDirectory;
    private PricingRefreshResult? _lastResult;
    public MainViewModel(PricingCheckUseCase pricingCheck, SettingsUseCase settingsUseCase, SwitchAndStartUseCase switchAndStart, IOmpCurrentProviderQuery currentProviderQuery, IPricingSnapshotQuery snapshotQuery, IPricingAdapterRegistry adapterRegistry, LocalAppSettings settings, ISitesDialogFactory sitesDialogFactory, IUserNotificationService notifications, ILogger<MainViewModel> logger)
    {
        _pricingCheck = pricingCheck; _settingsUseCase = settingsUseCase; _switchAndStart = switchAndStart; _currentProviderQuery = currentProviderQuery; _snapshotQuery = snapshotQuery; _adapterRegistry = adapterRegistry; _settings = settings; _sitesDialogFactory = sitesDialogFactory; _notifications = notifications; _logger = logger;
        InitializeCommand = new AsyncCommand(InitializeAsync, HandleCommandError); CheckCommand = new AsyncCommand(CheckAsync, HandleCommandError, () => _checkCancellation is null); CancelCommand = new RelayCommand(() => _checkCancellation?.Cancel(), () => _checkCancellation is not null); SwitchAndStartCommand = new AsyncCommand(SwitchAndStartAsync, HandleCommandError, () => SelectedProvider is not null && !string.IsNullOrWhiteSpace(SelectedWorkingDirectory)); ManageSitesCommand = new RelayCommand(ManageSites); SettingsCommand = new AsyncCommand(EditSettingsAsync, HandleCommandError); SyncSettings();
    }

    public ObservableCollection<PriceRow> Rows { get; } = [];
    public ObservableCollection<ProviderChoice> ProviderChoices { get; } = [];
    public ObservableCollection<string> WorkingDirectories { get; } = [];
    public AsyncCommand InitializeCommand { get; }
    public AsyncCommand CheckCommand { get; }
    public RelayCommand CancelCommand { get; }
    public AsyncCommand SwitchAndStartCommand { get; }
    public RelayCommand ManageSitesCommand { get; }
    public AsyncCommand SettingsCommand { get; }
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
    {
        try
        {
            var result = await _currentProviderQuery.ReadAsync(_settings.OmpRootDirectory, _checkCancellation?.Token ?? CancellationToken.None);
            CurrentProvider = result.Status switch
            {
                OmpCurrentProviderStatus.Identified => result.ProviderId!,
                OmpCurrentProviderStatus.ConfigurationFileMissing => "配置文件不存在",
                OmpCurrentProviderStatus.Unrecognized => "未识别",
                _ => "读取失败"
            };
            if (result.Status == OmpCurrentProviderStatus.ReadFailed)
            {
                StatusText = UserErrorMessages.ConfigurationReadFailed;
                LogUiFailure(_logger, "ConfigurationReadFailed", null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            CurrentProvider = "读取失败";
            StatusText = UserErrorMessages.ConfigurationReadFailed;
            LogUiFailure(_logger, "ConfigurationReadFailed", null);
        }
    }

    private async Task CheckAsync()
    {
        if (_checkCancellation is not null) return;
        _checkCancellation = new CancellationTokenSource(); CheckCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged(); StatusText = "正在检查已启用站点的价格…";
        try
        {
            await ReadCurrentProviderAsync();
            var outcome = await _pricingCheck.ExecuteAsync(_settings, CurrentProvider, _checkCancellation.Token);
            _settings = outcome.Settings;
            _lastResult = outcome.RefreshResult;
            MapResult(_lastResult, LocalAppSettings.DefaultUsageProfile);
            LastCheckedText = _lastResult.CompletedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture); StatusText = "检查完成。";
        }
        catch (OperationCanceledException) { StatusText = "已取消检查。"; }
        catch (Exception) { throw; }
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
        var result = _snapshotQuery.Load();
        if (!result.IsSuccess)
        {
            StatusText = "无法读取上次价格记录，请检查本地数据文件。";
            LogUiFailure(_logger, "SnapshotReadFailed", null);
            return;
        }

        var snapshots = result.Snapshots;
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

    private void AddRows(SiteConfiguration site, PricingSnapshot? snapshot, UsageProfile usage, PricingRefreshSiteResult? state, SitePricingResult? pricing)
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
        var issue = state?.Status == PricingRefreshSiteStatus.Failed
            ? UserErrorMessages.ForPricingFailure(state.FailureKind)
            : warning ? "价格数据包含提示，请谨慎核对。" : string.Empty;
        if (snapshot is null)
        {
            Rows.Add(new PriceRow(site.ProviderId, status, site.CurrentGroup + " [当前]", site.CurrentGroupRatio, null, usage, "—", site.GroupRatioSource, null, issue, stale, warning, true, site.BaseUrl, site.ConfigurationApiAddress));
            return;
        }
        var effective = site.CurrentGroupRatio is > 0
            ? snapshot.WithCurrentRatio(site.CurrentGroupRatio.Value, site.GroupRatioSource)
            : snapshot;
        var minimumSame = string.Equals(effective.MinimumGroup, site.CurrentGroup, StringComparison.Ordinal);
        var currentLabel = site.CurrentGroup + (minimumSame ? " [当前][最低]" : " [当前]");
        Rows.Add(new PriceRow(site.ProviderId, status, currentLabel, effective.CurrentGroupRatio ?? site.CurrentGroupRatio, effective.Prices, usage, "—", effective.GroupRatioSource, effective.RefreshedAt, issue, stale, warning, true, site.BaseUrl, site.ConfigurationApiAddress));
        if (!minimumSame && !string.IsNullOrWhiteSpace(effective.MinimumGroup) && effective.MinimumGroupPrices is not null)
            Rows.Add(new PriceRow(site.ProviderId, status, effective.MinimumGroup + " [最低]", effective.MinimumGroupRatio, effective.MinimumGroupPrices, usage, "—", "自动", effective.RefreshedAt, state?.FailureKind == PricingRefreshFailureKind.Authentication ? "需先绑定或更新凭据" : "仅供手动选择，未参与自动推荐", stale, warning));
    }



    private async Task SwitchAndStartAsync()
    {
        var choice = SelectedProvider;
        if (choice is null || string.IsNullOrWhiteSpace(SelectedWorkingDirectory)) return;
        try
        {
            var outcome = await _switchAndStart.ExecuteAsync(_settings, choice.ProviderId, SelectedWorkingDirectory);
            _settings = outcome.Settings;
            SyncSettings();
            StatusText = UserErrorMessages.ForSwitchStatus(outcome.Status, choice.ProviderId);
        }
        catch (Exception)
        {
            StatusText = UserErrorMessages.Unexpected;
            LogUiFailure(_logger, "SwitchAndStartUnexpected", null);
        }
    }

    private async Task EditSettingsAsync() { var dialog = new SettingsDialog(_settings); if (dialog.ShowDialog() == true) { _settings = _settingsUseCase.Save(dialog.Settings); SyncSettings(); await ReadCurrentProviderAsync(); LoadPersistedPrices(); } }
    private void HandleCommandError(Exception exception)
    {
        if (exception is OperationCanceledException) return;
        LogUiFailure(_logger, "CommandUnexpected", exception);
        _notifications.ShowError(StatusText, "操作失败");
    }

    private void LoadProviderChoices(string? preferredProvider)
    {
        ProviderChoices.Clear();
        foreach (var providerId in _settings.Sites.Select(x => x.ProviderId).Distinct(StringComparer.Ordinal)) ProviderChoices.Add(new ProviderChoice(providerId));
        SelectedProvider = ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, preferredProvider, StringComparison.Ordinal)) ?? ProviderChoices.FirstOrDefault(x => string.Equals(x.ProviderId, CurrentProvider, StringComparison.Ordinal));
    }
    private void ManageSites() { var previousSelection = SelectedProvider?.ProviderId; var dialog = _sitesDialogFactory.Create(_settings, CurrentProvider); dialog.ShowDialog(); _settings = dialog.Settings; SyncSettings(); LoadPersistedPrices(previousSelection); }
    private void SyncSettings() { WorkingDirectories.Clear(); foreach (var item in _settings.OmpWorkingDirectories.Distinct(StringComparer.OrdinalIgnoreCase)) WorkingDirectories.Add(item); SelectedWorkingDirectory = WorkingDirectories.FirstOrDefault(x => string.Equals(x, _settings.LastOmpWorkingDirectory, StringComparison.OrdinalIgnoreCase)) ?? WorkingDirectories.FirstOrDefault(); }
}

public sealed record ProviderChoice(string ProviderId) { public string Display => ProviderId; }
public sealed class PriceRow
{
    public PriceRow(string providerId, string status, string group, decimal? ratio, TokenPrices? prices, UsageProfile usage, string cacheHitRate, string ratioSource, DateTimeOffset? checkedAt, string issue, bool isStale, bool hasWarning, bool isSiteFirstRow = false, Uri? baseUrl = null, string? configurationApiAddress = null)
    { ProviderId = providerId; Status = status; Group = group; Ratio = ratio?.ToString("0.####", CultureInfo.InvariantCulture) ?? "—"; InputPrice = prices?.InputPerMillion.ToString("0.####") ?? "—"; CachedPrice = prices?.CachedInputPerMillion.ToString("0.####") ?? "—"; OutputPrice = prices?.OutputPerMillion.ToString("0.####") ?? "—"; EstimatedCost = prices is null ? "—" : PricingCalculator.Calculate(usage, prices).ToString("0.####"); CacheHitRate = cacheHitRate; RatioSource = string.IsNullOrWhiteSpace(ratioSource) ? "—" : ratioSource; UpdatedAt = checkedAt?.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "—"; Issue = issue; IsStale = isStale; HasWarning = hasWarning; IsSiteFirstRow = isSiteFirstRow; KeysUri = isSiteFirstRow ? MainWindow.BuildKeysUri(baseUrl, configurationApiAddress) : null; }
    public string ProviderId { get; }
    public string Status { get; }
    public string Group { get; }
    public string Ratio { get; }
    public string InputPrice { get; }
    public string CachedPrice { get; }
    public string OutputPrice { get; }
    public string EstimatedCost { get; }
    public string CacheHitRate { get; }
    public string RatioSource { get; }
    public string UpdatedAt { get; }
    public string Issue { get; }
    public bool IsStale { get; }
    public bool HasWarning { get; }
    public bool IsSiteFirstRow { get; }
    public Uri? KeysUri { get; }
}
