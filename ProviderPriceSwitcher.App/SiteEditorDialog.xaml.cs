using System.Windows;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public interface ISiteEditorDialogFactory
{
    SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner);
    SiteEditorDialog Create(SupplierEditorSession session, Window owner);
}

public sealed partial class SiteEditorDialog : Window
{
    private readonly SiteEditorViewModel _viewModel;
    private bool _closePending;

    public SiteEditorViewModel ViewModel => _viewModel;
    public SupplierEditorSession Session => _viewModel.Session;
    public SiteConfiguration? Site => _viewModel.SavedSite;

    public SiteEditorDialog(SiteEditorViewModel viewModel, Window owner)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Owner = owner;
        Width = 720;
        Height = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        viewModel.Saved += (_, _) => DialogResult = true;
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closePending || !_viewModel.IsProbing) return;
        e.Cancel = true;
        _closePending = true;
        await _viewModel.CloseAsync();
        if (IsVisible) Close();
    }

    private void SaveCredentialClick(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password;
        var cookie = CookieBox.Password;
        try { _viewModel.SaveCredential(token, cookie); }
        finally { TokenBox.Clear(); CookieBox.Clear(); }
    }

    private void ClearCredentialClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ClearCredential()) { TokenBox.Clear(); CookieBox.Clear(); }
    }

    private void SaveInferenceKeyClick(object sender, RoutedEventArgs e)
    {
        try { _viewModel.SaveInferenceKey(InferenceKeyBox.Password); }
        finally { InferenceKeyBox.Clear(); }
    }

    private void TokenBoxChanged(object sender, RoutedEventArgs e) => TokenPlaceholder.Visibility = TokenBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void CookieBoxChanged(object sender, RoutedEventArgs e) => CookiePlaceholder.Visibility = CookieBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void InferenceKeyBoxChanged(object sender, RoutedEventArgs e) => InferenceKeyPlaceholder.Visibility = InferenceKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void GroupOptionSelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox combo
            && combo.IsKeyboardFocusWithin
            && e.AddedItems.OfType<string>().FirstOrDefault() is { } selected)
            _viewModel.CurrentGroup = selected;
    }
    private async void DeleteInferenceKeyClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button) button.IsEnabled = false;
        try { await _viewModel.DeleteInferenceKeyAsync(); }
        catch { _viewModel.ReportInferenceKeyDeleteFailure(); }
        finally { if (sender is System.Windows.Controls.Button completed) completed.IsEnabled = true; }
    }
}

public sealed class SiteEditorDialogFactory : ISiteEditorDialogFactory
{
    private readonly Func<SupplierEditorSession, SiteEditorViewModel> _create;
    private readonly PricingProbeUseCase _probe;
    private readonly IPricingAdapterRegistry _registry;
    private readonly ISiteAccessCredentialStore? _credentials;
    private readonly IInferenceApiKeyUseCase _inferenceKeyUseCase;
    private readonly IUserNotificationService _notifications;

    public SiteEditorDialogFactory(
        Func<SupplierEditorSession, SiteEditorViewModel> create,
        PricingProbeUseCase probe,
        IPricingAdapterRegistry registry,
        IUserNotificationService notifications,
        IInferenceApiKeyUseCase inferenceKeyUseCase)
        : this(create, probe, registry, null, notifications, inferenceKeyUseCase)
    {
    }

    public SiteEditorDialogFactory(
        Func<SupplierEditorSession, SiteEditorViewModel> create,
        PricingProbeUseCase probe,
        IPricingAdapterRegistry registry,
        ISiteAccessCredentialStore? credentials,
        IUserNotificationService notifications,
        IInferenceApiKeyUseCase inferenceKeyUseCase)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _credentials = credentials;
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _inferenceKeyUseCase = inferenceKeyUseCase ?? throw new ArgumentNullException(nameof(inferenceKeyUseCase));
    }

    public SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var session = new SupplierEditorSession(_probe, _registry.Descriptors, settings.Model, _credentials, _inferenceKeyUseCase, settings.RequestTimeoutSeconds, original, _notifications);
        return Create(session, owner);
    }

    public SiteEditorDialog Create(SupplierEditorSession session, Window owner)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new(_create(session), owner);
    }
}
