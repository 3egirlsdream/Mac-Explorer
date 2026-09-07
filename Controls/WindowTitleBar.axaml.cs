using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;

namespace MacExplorer.Controls;

public partial class WindowTitleBar : UserControl
{
    private AppWindow? _ownerWindow;

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WindowTitleBar, string?>(nameof(Title));

    public static readonly StyledProperty<Control?> TitleBarContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, Control?>(nameof(TitleBarContent));

    public static readonly StyledProperty<Control?> TitleBarBackgroundContentProperty =
        AvaloniaProperty.Register<WindowTitleBar, Control?>(nameof(TitleBarBackgroundContent));

    public Control? TitleBarBackgroundContent
    {
        get => GetValue(TitleBarBackgroundContentProperty);
        set => SetValue(TitleBarBackgroundContentProperty, value);
    }

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public Control? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public WindowTitleBar()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) =>
        {
            if (_ownerWindow is { IsModalInteractionBlocked: false } window)
                window.Close();
        };
        MinimizeButton.Click += (_, _) =>
        {
            if (_ownerWindow is { CanMinimize: true, IsModalInteractionBlocked: false } window)
                window.WindowState = WindowState.Minimized;
        };
        FullScreenButton.Click += (_, _) => _ownerWindow?.ToggleFullScreen();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ownerWindow = TopLevel.GetTopLevel(this) as AppWindow;
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged += OnOwnerPropertyChanged;
            _ownerWindow.Activated += OnOwnerActivationChanged;
            _ownerWindow.Deactivated += OnOwnerActivationChanged;
        }
        UpdateWindowControlState();
        UpdateTitleBarContentState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_ownerWindow != null)
        {
            _ownerWindow.PropertyChanged -= OnOwnerPropertyChanged;
            _ownerWindow.Activated -= OnOwnerActivationChanged;
            _ownerWindow.Deactivated -= OnOwnerActivationChanged;
            _ownerWindow = null;
        }
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TitleBarContentProperty)
            UpdateTitleBarContentState();
    }

    private void OnOwnerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.CanMinimizeProperty
            || e.Property == Window.CanMaximizeProperty
            || e.Property == Window.WindowStateProperty
            || e.Property == AppWindow.IsModalInteractionBlockedProperty)
            UpdateWindowControlState();
    }

    private void OnOwnerActivationChanged(object? sender, EventArgs e) => UpdateWindowControlState();

    private void UpdateTitleBarContentState()
    {
        var hasContent = TitleBarContent is not null;
        TitleBarContentRow.IsVisible = hasContent;
        TitleText.IsVisible = !hasContent;
    }

    private void UpdateWindowControlState()
    {
        if (_ownerWindow is not { } window)
            return;

        var blocked = window.IsModalInteractionBlocked;
        CloseButton.IsEnabled = !blocked;
        MinimizeButton.IsEnabled = window.CanMinimize && !blocked;
        FullScreenButton.IsEnabled = window.CanMaximize && !blocked;

        var isFullScreen = window.WindowState == WindowState.FullScreen;
        var fullScreenLabel = isFullScreen ? "退出全屏" : "进入全屏";
        ToolTip.SetTip(FullScreenButton, fullScreenLabel);
        AutomationProperties.SetName(FullScreenButton, fullScreenLabel);
        PseudoClasses.Set(":fullscreen", isFullScreen);
        PseudoClasses.Set(":inactive", !window.IsActive);
        PseudoClasses.Set(":modal-blocked", blocked);
    }
}
