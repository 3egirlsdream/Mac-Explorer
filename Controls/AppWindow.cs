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
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    internal IShortcutService Shortcuts { get; set; } = ShortcutService.Resolve();
    private ShortcutHintController? _shortcutHints;
    private SubscriptionService? _subscription;
    private Grid? _subscriptionHost;
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
            MacAutomationLifetime.Prepare();
            MacNativeFileDrop.Enable(this);
            _nativeLifetime = PlatformImpl;
            _subscription ??= SubscriptionAccess.Current;
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
        Activated += (_, _) => { RequestNativeWindowChrome(); UpdateWindowPseudoClasses(); SchedulePostActivationDisplayRefresh(); if (_subscription != null) _ = _subscription.RefreshAsync(); };
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

    private void UpdateSubscription()
    {
        var locked = _subscription?.IsLocked ?? SubscriptionAccess.IsLocked;
        PseudoClasses.Set(":subscription-locked", locked);
        if (_subscriptionHost == null) return;
        if (locked && _subscriptionHost.Children.Count == 0)
            _subscriptionHost.Children.Add(new Views.SubscriptionView(_subscription ?? SubscriptionAccess.Current));
        _subscriptionHost.IsVisible = locked;
        if (locked)
        {
            _shortcutHints?.Cancel();
            if (ContextMenu is { IsOpen: true }) ContextMenu.Close();
        }
    }

    public void ApplyNativeWindowChrome()
    {
        _nativeChromeRefreshPending = false;
        MacWindowChrome.MakeTransparent(this);
        // Stage Manager fully occludes the window while the app is in the background;
        // when the window returns, the content layer can be left without a valid
        // drawable and freezes on a stale frame until a fullscreen toggle forces a
        // re-composite. Ask the native layer to redisplay so the render loop produces
        // a fresh frame as soon as the window is visible again.
        MacWindowChrome.RequestDisplayRefresh(this);
    }

    private void SchedulePostActivationDisplayRefresh()
    {
        if (!OperatingSystem.IsMacOS()) return;
        // The refresh inside ApplyNativeWindowChrome runs on the activation turn and
        // can still race Stage Manager's restore transition. Schedule one follow-up
        // after the transition settles so the first valid drawable is re-requested.
        DispatcherTimer.RunOnce(
            () => MacWindowChrome.RequestDisplayRefresh(this),
            TimeSpan.FromMilliseconds(300),
            DispatcherPriority.Background);
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
