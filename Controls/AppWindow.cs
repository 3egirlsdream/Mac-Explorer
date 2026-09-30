using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Avalonia.Interactivity;

namespace MacExplorer.Controls;

public class AppWindow : Window
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;
    internal IShortcutService Shortcuts { get; set; } = ShortcutService.Resolve();
    private ShortcutHintController? _shortcutHints;
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

    public AppWindow()
    {
        WindowDecorations = WindowDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = 40;
        Focusable = true;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        TransparencyBackgroundFallback = Brushes.Transparent;

        Opened += (_, _) =>
        {
            ApplyNativeWindowChrome();
            UpdateWindowPseudoClasses();
            _shortcutHints ??= new ShortcutHintController(this, Shortcuts);
        };
        Closed += (_, _) => { _shortcutHints?.Dispose(); _shortcutHints = null; MacWindowChrome.RemoveVibrancy(this); };
        Activated += (_, _) => UpdateWindowPseudoClasses();
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
