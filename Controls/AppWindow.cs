using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Controls;

public class AppWindow : Window
{
    private WindowState _stateBeforeFullScreen = WindowState.Normal;

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
        };
        Closed += (_, _) => MacWindowChrome.RemoveVibrancy(this);
        Activated += (_, _) => UpdateWindowPseudoClasses();
        Deactivated += (_, _) => UpdateWindowPseudoClasses();
        KeyDown += OnWindowKeyDown;
        UpdateWindowPseudoClasses();
    }

    protected override Type StyleKeyOverride => typeof(AppWindow);

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
        if (e.Key != Key.F
            || !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || !e.KeyModifiers.HasFlag(KeyModifiers.Meta)
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
