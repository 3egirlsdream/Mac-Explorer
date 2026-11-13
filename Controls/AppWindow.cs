using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Avalonia.Interactivity;
using MacExplorer.Services.Subscriptions;
using Avalonia.Threading;
using Avalonia.Platform;

namespace MacExplorer.Controls;

public class AppWindow : Window
{
    private readonly MemoryDiagnosticLifetime? _memoryLifetime;
    internal long DiagnosticId => _memoryLifetime?.Id ?? 0;
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    internal IShortcutService Shortcuts { get; set; } = ShortcutService.Resolve();
    private ShortcutHintController? _shortcutHints;
    private SubscriptionService? _subscription;
    private Grid? _subscriptionHost;
    private PremiumFeature? _upgradeFeature;
    private bool _nativeChromeRefreshPending;
    private ITopLevelImpl? _nativeLifetime;
    internal WindowRenderDiagnostics? RenderDiagnostics { get; private set; }
    internal bool IsShortcutHintVisible => _shortcutHints?.IsVisible == true;

    public static readonly StyledProperty<bool> ShowTitleBarProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(ShowTitleBar), true);

    public bool ShowTitleBar
    {
        get => GetValue(ShowTitleBarProperty);
        set => SetValue(ShowTitleBarProperty, value);
    }

    public static readonly StyledProperty<bool> ShowTitleBarDividerProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(ShowTitleBarDivider), true);

    public bool ShowTitleBarDivider
    {
        get => GetValue(ShowTitleBarDividerProperty);
        set => SetValue(ShowTitleBarDividerProperty, value);
    }

    public static readonly StyledProperty<bool> ExtendContentToTopProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(ExtendContentToTop));

    public static readonly StyledProperty<bool> FullHeightContentProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(FullHeightContent));

    public bool ExtendContentToTop
    {
        get => GetValue(ExtendContentToTopProperty);
        set => SetValue(ExtendContentToTopProperty, value);
    }

    public bool FullHeightContent
    {
        get => GetValue(FullHeightContentProperty);
        set => SetValue(FullHeightContentProperty, value);
    }

    public static readonly StyledProperty<Control?> TitleBarContentProperty =
        AvaloniaProperty.Register<AppWindow, Control?>(nameof(TitleBarContent));

    public static readonly StyledProperty<Control?> TitleBarBackgroundContentProperty =
        AvaloniaProperty.Register<AppWindow, Control?>(nameof(TitleBarBackgroundContent));

    public static readonly StyledProperty<Control?> BackgroundContentProperty =
        AvaloniaProperty.Register<AppWindow, Control?>(nameof(BackgroundContent));

    public Control? BackgroundContent
    {
        get => GetValue(BackgroundContentProperty);
        set => SetValue(BackgroundContentProperty, value);
    }

    public static readonly StyledProperty<bool> IsUpgradeOpenProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(IsUpgradeOpen));
    public bool IsUpgradeOpen { get => GetValue(IsUpgradeOpenProperty); private set => SetValue(IsUpgradeOpenProperty, value); }

    public static readonly StyledProperty<bool> IsModalInteractionBlockedProperty =
        AvaloniaProperty.Register<AppWindow, bool>(nameof(IsModalInteractionBlocked));

    public Control? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public Control? TitleBarBackgroundContent
    {
        get => GetValue(TitleBarBackgroundContentProperty);
        set => SetValue(TitleBarBackgroundContentProperty, value);
    }

    public bool IsModalInteractionBlocked
    {
        get => GetValue(IsModalInteractionBlockedProperty);
        set => SetValue(IsModalInteractionBlockedProperty, value);
    }

    internal WindowState RestorableWindowState => _stateBeforeFullScreen;

    public AppWindow() : this(null) { }

    internal AppWindow(SubscriptionService? subscription)
    {
        _memoryLifetime = MemoryDiagnostics.Track(this, DiagnosticObjectKind.Window);
        _subscription = subscription;
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 40;
        Focusable = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        TransparencyBackgroundFallback = Brushes.Transparent;

        // WindowOpenedEvent runs before the initial layout and native Show, including ShowDialog.
        AddHandler(WindowOpenedEvent, (_, _) => MacWindowChrome.PrepareToShow(this));

        Opened += (_, _) =>
        {
            _memoryLifetime?.SetState(DiagnosticObjectState.Attached);
            MemoryDiagnostics.Operation(DiagnosticAction.WindowOpened, DiagnosticId);
            MacAutomationLifetime.Prepare();
            MacNativeFileDrop.Enable(this);
            _nativeLifetime = PlatformImpl;
            _subscription ??= FeatureAccess.Current?.Subscription ?? SubscriptionAccess.Current;
            if (_subscription != null) _subscription.Changed += OnSubscriptionChanged;
            UpdateSubscription();
            ApplyNativeWindowChrome();
            UpdateLayout();
            UpdateWindowPseudoClasses();
            if (MemoryDiagnostics.IsEnabled && OperatingSystem.IsMacOS())
                RenderDiagnostics ??= new WindowRenderDiagnostics(this);
            _shortcutHints ??= new ShortcutHintController(this, Shortcuts);
        };
        Closed += (_, _) =>
        {
            _memoryLifetime?.SetState(DiagnosticObjectState.Closed);
            RenderDiagnostics?.Dispose();
            RenderDiagnostics = null;
            MacNativeFileDrop.Disable(this);
            if (_subscription != null) _subscription.Changed -= OnSubscriptionChanged;
            _shortcutHints?.Dispose(); _shortcutHints = null; MacWindowChrome.RemoveVibrancy(this);
            MacAutomationLifetime.Release(this);
            var closedPlatform = _nativeLifetime;
            _nativeLifetime = null;
            Dispatcher.UIThread.Post(() =>
            {
                MacAutomationLifetime.ReleaseClosedCallbacks(closedPlatform);
                MacAutomationLifetime.ReleaseDetached();
            }, DispatcherPriority.Background);
        };
        Activated += (_, _) => { RequestNativeWindowChrome(); UpdateWindowPseudoClasses(); if (_subscription != null) _ = _subscription.RefreshAsync(); };
        Deactivated += (_, _) => UpdateWindowPseudoClasses();
        KeyDown += OnWindowKeyDown;
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!Shortcuts.Capture(e.Key, e.KeyModifiers)) return;
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        UpdateWindowPseudoClasses();
    }

    protected override Type StyleKeyOverride => typeof(AppWindow);

    internal Grid? WindowOverlayHost { get; private set; }

    internal virtual bool IsShortcutAvailable(ShortcutDefinition definition) => definition.Id switch
    {
        ShortcutIds.FullScreen => CanMaximize && !IsModalInteractionBlocked,
        "native.hide" or "native.hide-others" or "native.quit" => true,
        "fixed.escape" or "fixed.selection" => true,
        _ => false
    };

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        WindowOverlayHost = e.NameScope.Find<Grid>("WindowOverlayHost");
        _subscriptionHost = e.NameScope.Find<Grid>("SubscriptionHost");
        UpdateSubscription();
    }

    private void OnSubscriptionChanged() => Dispatcher.UIThread.Post(UpdateSubscription);

    internal void ShowUpgrade(PremiumFeature feature = PremiumFeature.None)
    {
        AppToast.For(this).CloseAll();
        _upgradeFeature = feature;
        UpdateSubscription();
    }

    internal void CloseUpgrade()
    {
        _upgradeFeature = null;
        UpdateSubscription();
    }

    private void UpdateSubscription()
    {
        if (_upgradeFeature != null && _subscription?.Snapshot.HasAccess(TimeProvider.System.GetUtcNow()) == true)
            _upgradeFeature = null;
        var visible = _upgradeFeature != null;
        IsUpgradeOpen = visible;
        PseudoClasses.Set(":subscription-locked", visible);
        if (_subscriptionHost == null) return;
        if (visible && _subscriptionHost.Children.Count == 0)
        {
            var view = new Views.SubscriptionView(_subscription ?? SubscriptionAccess.Current);
            view.CloseRequested += CloseUpgrade;
            _subscriptionHost.Children.Add(view);
        }
        if (_subscriptionHost.Children.FirstOrDefault() is Views.SubscriptionView subscription)
            subscription.Feature = _upgradeFeature ?? PremiumFeature.None;
        _subscriptionHost.IsVisible = visible;
        if (!visible) _subscriptionHost.Children.Clear();
        else
        {
            _shortcutHints?.Cancel();
            if (ContextMenu is { IsOpen: true }) ContextMenu.Close();
        }
    }

    public void ApplyNativeWindowChrome()
    {
        _nativeChromeRefreshPending = false;
        MacWindowChrome.MakeTransparent(this);
    }

    private void RequestNativeWindowChrome()
    {
        if (_nativeChromeRefreshPending) return;
        _nativeChromeRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (_nativeChromeRefreshPending) ApplyNativeWindowChrome();
        });
    }

    public void ToggleFullScreen()
    {
        if (!CanMaximize || IsModalInteractionBlocked)
            return;

        if (WindowState == WindowState.FullScreen)
        {
            RenderDiagnostics?.RecordRequest(_stateBeforeFullScreen, "fullscreen-toggle");
            WindowState = _stateBeforeFullScreen;
            return;
        }

        _stateBeforeFullScreen = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        RenderDiagnostics?.RecordRequest(WindowState.FullScreen, "fullscreen-toggle");
        WindowState = WindowState.FullScreen;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty)
            MemoryDiagnostics.Operation(DiagnosticAction.WindowVisibility, DiagnosticId, visible: IsVisible);
        if (change.Property == WindowStateProperty)
        {
            if (WindowState is WindowState.Normal or WindowState.Maximized)
                _stateBeforeFullScreen = WindowState;
            UpdateWindowPseudoClasses();
            RequestNativeWindowChrome();
        }
        else if (change.Property == IsVisibleProperty)
        {
            RequestNativeWindowChrome();
        }
        else if (change.Property == IsModalInteractionBlockedProperty
            || change.Property == ExtendContentToTopProperty || change.Property == FullHeightContentProperty
            || change.Property == ShowTitleBarProperty)
        {
            UpdateWindowPseudoClasses();
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _upgradeFeature != null) { CloseUpgrade(); e.Handled = true; return; }
        if (e.Handled || !Shortcuts.Matches(ShortcutIds.FullScreen, e)
            || IsModalInteractionBlocked)
            return;

        ToggleFullScreen();
        e.Handled = true;
    }

    private void UpdateWindowPseudoClasses()
    {
        PseudoClasses.Set(":titlebar-hidden", !ShowTitleBar);
        PseudoClasses.Set(":extended-content", ExtendContentToTop);
        PseudoClasses.Set(":full-height-content", FullHeightContent);
        PseudoClasses.Set(":normal", WindowState == WindowState.Normal);
        PseudoClasses.Set(":maximized", WindowState == WindowState.Maximized);
        PseudoClasses.Set(":fullscreen", WindowState == WindowState.FullScreen);
        PseudoClasses.Set(":active", IsActive);
        PseudoClasses.Set(":inactive", !IsActive);
        PseudoClasses.Set(":modal-blocked", IsModalInteractionBlocked);
    }
}

public class DialogWindow : AppWindow
{
    public DialogWindow()
    {
        ExtendContentToTop = true;
        CanResize = false;
        CanMinimize = false;
        CanMaximize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        };
    }

    protected override Type StyleKeyOverride => typeof(AppWindow);
}

public class ToolWindow : AppWindow
{
    protected override Type StyleKeyOverride => typeof(AppWindow);
}
