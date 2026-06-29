using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace MacExplorer.Controls;

public partial class WindowTitleBar : UserControl
{
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<WindowTitleBar, string?>(nameof(Title));

    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public WindowTitleBar()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => OwnerWindow?.Close();
        MinimizeButton.Click += (_, _) =>
        {
            if (OwnerWindow is { CanMinimize: true })
                SetWindowState(WindowState.Minimized);
        };
        MaximizeButton.Click += (_, _) => OwnerWindow?.ToggleMaximize();
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateWindowControlAvailability();
    }

    private AppWindow? OwnerWindow => TopLevel.GetTopLevel(this) as AppWindow;

    private void UpdateWindowControlAvailability()
    {
        if (OwnerWindow is not { } window)
            return;

        MinimizeButton.IsEnabled = window.CanMinimize;
        MaximizeButton.IsEnabled = window.CanMaximize;
    }

    private void SetWindowState(WindowState state)
    {
        if (OwnerWindow is { } window) window.WindowState = state;
    }

    private void OnDragRegionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || OwnerWindow is not { } window)
            return;

        if (e.ClickCount == 2)
        {
            if (window.CanMaximize)
                window.ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (window.WindowState is WindowState.Maximized or WindowState.FullScreen)
        {
            e.Handled = true;
            return;
        }

        window.BeginMoveDrag(e);
        e.Handled = true;
    }
}
