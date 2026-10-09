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

namespace MacExplorer.Controls;

public class AppWindow : Window
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    internal IShortcutService Shortcuts { get; set; } = ShortcutService.Resolve();
    private ShortcutHintController? _shortcutHints;
    private SubscriptionService? _subscription;
    private Grid? _subscriptionHost;
    internal bool IsShortcutHintVisible => _shortcutHints?.IsVisible == true;

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

        Opened += (_, _) =>
        {
            _subscription ??= SubscriptionAccess.Current;
            if (_subscription != null) _subscription.Changed += OnSubscriptionChanged;
            UpdateSubscription();
            ApplyNativeWindowChrome();
            UpdateWindowPseudoClasses();
            _shortcutHints ??= new ShortcutHintController(this, Shortcuts);
        };
        Closed += (_, _) =>
        {
            if (_subscription != null) _subscription.Changed -= OnSubscriptionChanged;
            _shortcutHints?.Dispose(); _shortcutHints = null; MacWindowChrome.RemoveVibrancy(this);
        };
        Activated += (_, _) => { UpdateWindowPseudoClasses(); if (_subscription != null) _ = _subscription.RefreshAsync(); };
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

    public void ApplyNativeWindowChrome() => MacWindowChrome.MakeTransparent(this);

    public void ToggleFullScreen()
    {
        if (!CanMaximize || IsModalInteractionBlocked)
            return;

        if (WindowState == WindowState.FullScreen)
        {
            WindowState = _stateBeforeFullScreen;
            return;
        }

        _stateBeforeFullScreen = WindowState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
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
        }
        else if (change.Property == IsModalInteractionBlockedProperty)
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
