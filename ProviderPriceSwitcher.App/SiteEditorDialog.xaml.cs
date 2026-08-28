using System.Windows;
using ProviderPriceSwitcher.Application;
using ProviderPriceSwitcher.Core;

namespace ProviderPriceSwitcher.App;

public interface ISiteEditorDialogFactory
{
    SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner);
    SiteEditorDialog Create(SupplierEditorSession session, LocalAppSettings settings, Window owner);
}

public sealed partial class SiteEditorDialog : Window
{
    private readonly SiteEditorViewModel _viewModel;
    private bool _closePending;
    private bool _deletingInferenceKey;

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
        if (_deletingInferenceKey) return;
        _deletingInferenceKey = true;
        if (sender is System.Windows.Controls.Button button) button.IsEnabled = false;
        try { await _viewModel.DeleteInferenceKeyAsync(); }
        catch { _viewModel.ReportInferenceKeyDeleteFailure(); }
        finally { _deletingInferenceKey = false; if (sender is System.Windows.Controls.Button completed) completed.IsEnabled = true; }
    }
}

public sealed class SiteEditorDialogFactory : ISiteEditorDialogFactory
{
    private readonly Func<SupplierEditorSession, LocalAppSettings, SiteEditorViewModel> _create;
    private readonly IPricingAdapterRegistry _registry;

    public SiteEditorDialogFactory(
        Func<SupplierEditorSession, LocalAppSettings, SiteEditorViewModel> create,
        IPricingAdapterRegistry registry)
    {
        _create = create ?? throw new ArgumentNullException(nameof(create));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var session = new SupplierEditorSession(_registry, settings, original);
        return Create(session, settings, owner);
    }

    public SiteEditorDialog Create(SupplierEditorSession session, LocalAppSettings settings, Window owner)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(settings);
        return new(_create(session, settings), owner);
    }
}
