#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Subscriptions;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

/// <summary>Real native windows with an isolated clock and a clearly synthetic Apple store.</summary>
internal static class FeatureAccessSmoke
{
    internal static bool Enabled => RuntimePaths.TestRoot != null
        && Environment.GetEnvironmentVariable("MACEXPLORER_FEATURE_ACCESS_SMOKE") is "1" or "expired";
    internal static bool ManualExpired => Enabled
        && Environment.GetEnvironmentVariable("MACEXPLORER_FEATURE_ACCESS_SMOKE") == "expired";

    internal static async Task PrepareManualExpiredAsync(MainWindow window)
    {
        var access = App.Services.GetRequiredService<FeatureAccessService>();
        var subscription = App.Services.GetRequiredService<SubscriptionService>();
        await access.StartAsync(); subscription.Start();
        await Task.WhenAll(subscription.RefreshAsync(), subscription.LoadProductAsync());
        await File.WriteAllTextAsync(Path.Combine(RuntimePaths.TestRoot!, "manual-expired-state.json"),
            JsonSerializer.Serialize(new { state = access.Snapshot.State.ToString(),
                premiumEnabled = access.CanUse(PremiumFeature.All), syntheticStore = true,
                channel = DistributionChannel.IsAppStore ? "AppStore" : "Website" }));
        window.Activate();
    }
    internal static readonly SmokeClock Clock = new();
    internal static readonly SmokeStore Store = new();
    internal sealed class SmokeClock : TimeProvider
    {
        internal DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    internal sealed class SmokeStore : ISubscriptionStore
    {
        public event Action? TransactionsChanged { add { } remove { } }
        internal SubscriptionSnapshot Value = new(SubscriptionState.Unsubscribed);
        public void Start() { }
        public Task<SubscriptionSnapshot> CheckAsync(CancellationToken token) => Task.FromResult(Value);
        public Task<SubscriptionProduct> LoadProductAsync(CancellationToken token) => Task.FromResult(new SubscriptionProduct("TEST ¥9.90", false, 0));
        public Task<PurchaseOutcome> PurchaseAsync(CancellationToken token) => Task.FromResult(PurchaseOutcome.Cancelled);
        public Task RestoreAsync(CancellationToken token) => Task.CompletedTask;
        public Task ManageAsync(CancellationToken token) => Task.CompletedTask;
        public void Dispose() { }
    }

    internal static async Task RunAsync(MainWindow window)
    {
        var root = RuntimePaths.TestRoot!;
        var output = Path.Combine(root, "feature-access"); Directory.CreateDirectory(output);
        var checks = new List<string>(); string? error = null;
        var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        AppWindow? second = null;
        try
        {
            var access = App.Services.GetRequiredService<FeatureAccessService>();
            var subscription = App.Services.GetRequiredService<SubscriptionService>();
            await access.StartAsync(); subscription.Start();
            await Task.WhenAll(subscription.RefreshAsync(), subscription.LoadProductAsync());
            Check(access.CanUse(PremiumFeature.All), "first-use-full-trial");
            var files = Directory.CreateDirectory(Path.Combine(root, "Documents", "Free file operations")).FullName;
            var source = Path.Combine(files, "source.txt"); await File.WriteAllTextAsync(source, "keep");
            var delivery = App.Services.GetRequiredService<FileDeliveryService>(); delivery.StagePaths([source]);
            var background = App.Services.GetRequiredService<BackgroundThemeService>();
            await background.SelectPresetAsync("animal-panda"); background.Update(true, 48);
            var options = background.Options;
            var model = (MainWindowViewModel)window.DataContext!;
            await model.FileList.NavigateToAsync(files); model.SetPaneLayout(PaneLayout.TwoColumns);
            await Capture(window, "trial-main");
            Check(background.IsActive && model.IsMultiPane, "trial-background-and-split");

            AppToast.For(window).CloseAll();
            Clock.Now = Clock.Now.AddHours(168); access.Refresh(); await Task.Delay(250);
            Check(!background.IsActive && background.Options == options, "expired-background-keeps-configuration");
            Check(!model.IsMultiPane, "expired-split-returns-to-single-tab");
            Check(delivery.StagedPaths.SequenceEqual([source]), "expired-delivery-keeps-staging");
            try { delivery.StagePaths([source]); throw new InvalidOperationException("New staging was allowed."); }
            catch (FeatureAccessDeniedException) { checks.Add("expired-delivery-rejects-new-drop"); }
            var service = App.Services.GetRequiredService<IFileService>();
            var destination = Directory.CreateDirectory(Path.Combine(files, "destination")).FullName;
            await service.CopyAsync(source, destination);
            Check(await File.ReadAllTextAsync(Path.Combine(destination, "source.txt")) == "keep", "free-copy-after-expiry");
            await model.FileList.NavigateToAsync(files);
            Check(window.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "AccessStatusText"
                && text.IsEffectivelyVisible && text.Text == LocalizationText.Get("access.free")), "free-sidebar-status-visible");
            await Capture(window, "free-main");

            second = new AppWindow(subscription) { Width = 420, Height = 560, Title = "Feature access QA" };
            second.Show();
            foreach (var dark in new[] { false, true })
            {
                window.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.MinWidth = 360; window.Width = dark ? 640 : 980; window.Height = 700;
                window.ShowUpgrade(PremiumFeature.BackgroundThemes);
                await Capture(window, dark ? "upgrade-dark-narrow" : "upgrade-light");
                Check(!second.GetVisualDescendants().OfType<SubscriptionView>().Any(), "upgrade-does-not-cover-other-window");
                var host = window.GetVisualDescendants().OfType<Grid>().First(c => c.Name == "SubscriptionHost");
                var view = (SubscriptionView)host.Children[0];
                view.FindControl<Button>("ContinueButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(!host.IsVisible, "upgrade-can-continue-free");
            }
            Store.Value = new(SubscriptionState.Subscribed, Clock.Now.AddYears(1));
            await subscription.RefreshAsync(); await Task.Delay(250);
            Check(background.IsActive && background.Options == options, "purchase-restores-background");
            background.Update(false, 48);
            Store.Value = new(SubscriptionState.Unsubscribed); await subscription.RefreshAsync();
            Store.Value = new(SubscriptionState.Subscribed, Clock.Now.AddYears(1)); await subscription.RefreshAsync();
            Check(!background.IsActive, "explicitly-disabled-background-stays-disabled");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            second?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { checks, error, syntheticStore = true, realStoreKitPurchaseVerified = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Feature access smoke: {output}");
            desktop.Shutdown(error == null ? 0 : 1);
        }

        void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        async Task Capture(Control control, string name)
        {
            control.UpdateLayout(); await Task.Delay(250);
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }
}
#endif
