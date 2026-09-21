using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.VisualTree;
using LiquidGlassAvaloniaUI;

namespace MacExplorer.Controls;

// Adds the shared material without replacing menu/ComboBox templates or their input behavior.
public sealed class PopupGlass : AvaloniaObject
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<PopupGlass, Border, bool>("IsEnabled");

    public static bool GetIsEnabled(Border border) => border.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Border border, bool value) => border.SetValue(IsEnabledProperty, value);

    static PopupGlass()
    {
        IsEnabledProperty.Changed.AddClassHandler<Border>((border, change) =>
        {
            if (change.GetNewValue<bool>())
            {
                LiquidGlassBackdrop.SetIsExcludedFromForegroundCapture(border, true);
                border.AttachedToVisualTree += OnAttached;
                if (border.IsAttachedToVisualTree()) Install(border);
            }
            else
            {
                LiquidGlassBackdrop.SetIsExcludedFromForegroundCapture(border, false);
                border.AttachedToVisualTree -= OnAttached;
                if (border.Child is GlassPanel panel)
                {
                    var content = panel.ContentHost.Child;
                    panel.ContentHost.Child = null;
                    border.Child = content;
                }
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Install((Border)sender!);

    private static void Install(Border border)
    {
        if (border.Child is GlassPanel) return;
        var content = border.Child;
        border.Child = null;
        border.Child = new GlassPanel(border, content);
    }

    private sealed class GlassPanel : Grid
    {
        public Border ContentHost { get; }

        public GlassPanel(Border owner, Control? content)
        {
            var glass = new LiquidGlassSurface { Classes = { "popup-glass" }, CaptureForeground = true, IsHitTestVisible = false };
            glass.Bind(LiquidGlassSurface.CornerRadiusProperty, new Binding(nameof(Border.CornerRadius)) { Source = owner });
            ContentHost = new Border { Child = content };
            LiquidGlassBackdrop.SetIsExcludedFromCapture(ContentHost, true);
            Children.Add(glass);
            Children.Add(ContentHost);
            // Cover the outer padding while preserving the original content bounds and popup size.
            void UpdatePadding()
            {
                var p = owner.Padding;
                Margin = new Thickness(-p.Left, -p.Top, -p.Right, -p.Bottom);
                ContentHost.Padding = p;
            }
            owner.PropertyChanged += OnOwnerChanged;
            DetachedFromVisualTree += (_, _) => owner.PropertyChanged -= OnOwnerChanged;
            AttachedToVisualTree += (_, _) =>
            {
                owner.PropertyChanged -= OnOwnerChanged;
                owner.PropertyChanged += OnOwnerChanged;
                UpdatePadding();
            };
            void OnOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
            {
                if (e.Property == Border.PaddingProperty) UpdatePadding();
            }
            UpdatePadding();
        }
    }
}
