using System.Windows;
using ProviderPriceSwitcher.Core;
using ProviderPriceSwitcher.Application;
namespace ProviderPriceSwitcher.App;
public interface ISiteEditorDialogFactory { SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner); }
public sealed partial class SiteEditorDialog : Window
{
    private readonly SiteEditorViewModel _viewModel;
    private bool _closePending;
    public SiteConfiguration? Site => _viewModel.SavedSite;
    public SiteEditorDialog(SiteEditorViewModel viewModel, Window owner)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; Owner = owner; Width = 720; Height = 760; MinHeight = 620; WindowStartupLocation = WindowStartupLocation.CenterOwner;
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
    private void InferenceKeyBoxChanged(object sender, RoutedEventArgs e) => InferenceKeyPlaceholder.Visibility = InferenceKeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    private void DeleteInferenceKeyClick(object sender, RoutedEventArgs e) => _viewModel.DeleteInferenceKey();
}
public sealed class SiteEditorDialogFactory(Func<SiteConfiguration?, LocalAppSettings, SiteEditorViewModel> create) : ISiteEditorDialogFactory
{
    public SiteEditorDialog Create(SiteConfiguration? original, LocalAppSettings settings, Window owner) => new(create(original, settings), owner);
}
