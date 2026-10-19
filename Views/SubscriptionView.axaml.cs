using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Services.Subscriptions;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class SubscriptionView : UserControl
{
    private SubscriptionService? _service;
    public SubscriptionView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
    }
    private void UpdateLayoutMode()
    {
        var narrow = Bounds.Width < 720;
        IntroductionGrid.ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,*");
        Grid.SetColumn(FeaturePanel, narrow ? 0 : 1);
        Grid.SetRow(FeaturePanel, narrow ? 1 : 0);
        WorkflowImage.Height = narrow ? 140 : 260;
        WorkflowImage.Margin = new Avalonia.Thickness(0, 0, narrow ? 0 : 24, narrow ? 12 : 0);
    }
    internal SubscriptionView(SubscriptionService? service) : this() => _service = service;
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service ??= App.Services?.GetService<SubscriptionService>();
        if (LocalizationService.Current is { } language) language.LanguageChanged += OnChanged;
        if (_service == null) return;
        _service.Changed += OnChanged;
        Update();
    }
    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        if (LocalizationService.Current is { } language) language.LanguageChanged -= OnChanged;
        if (_service != null) _service.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }
    private void OnChanged() => Dispatcher.UIThread.Post(Update);
    private void Update()
    {
        if (_service == null) return;
        StatusText.Text = _service.Snapshot.State == SubscriptionState.VerificationFailed ? LocalizationText.Get("subscription.unverified") : LocalizationText.Get("subscription.required");
        var product = _service.Product;
        PriceText.Text = product == null ? LocalizationText.Get(_service.ProductError == null ? "subscription.loading_price" : "subscription.no_price") : LocalizationText.Get("subscription.yearly_price", product.Price);
        TermsText.Text = product?.TrialEligible == true
            ? LocalizationText.Get("subscription.trial_terms", product.TrialDays, product.Price)
            : LocalizationText.Get("subscription.renewal_terms");
        SubscribeButton.Content = product?.TrialEligible == true ? LocalizationText.Get("subscription.trial_button", product.TrialDays) : LocalizationText.Get("subscription.subscribe");
        SubscribeButton.IsEnabled = product != null && !_service.IsBusy;
        RestoreButton.IsEnabled = RetryButton.IsEnabled = ManageButton.IsEnabled = !_service.IsBusy;
        MessageText.Text = _service.ActionMessage ?? _service.ProductError;
        MessageText.IsVisible = !string.IsNullOrEmpty(MessageText.Text);
    }
    private async void Subscribe(object? sender, RoutedEventArgs e) { if (_service != null) await _service.PurchaseAsync(); }
    private async void Restore(object? sender, RoutedEventArgs e) { if (_service != null) await _service.RestoreAsync(); }
    private async void Retry(object? sender, RoutedEventArgs e)
    {
        if (_service != null) await Task.WhenAll(_service.RefreshAsync(), _service.LoadProductAsync());
    }
    private async void Manage(object? sender, RoutedEventArgs e) { if (_service != null) await _service.ManageAsync(); }
    private async void Privacy(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(SubscriptionLinks.Privacy));
    }
    private async void Terms(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await top.Launcher.LaunchUriAsync(new Uri(SubscriptionLinks.Terms));
    }
    private void Quit(object? sender, RoutedEventArgs e)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.TryShutdown();
    }
}

internal static class SubscriptionLinks
{
    internal static string Privacy => typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>().First(a => a.Key == "PrivacyPolicyUrl").Value!;
    internal const string Terms = "https://www.apple.com/legal/internet-services/itunes/dev/stdeula/";
}
