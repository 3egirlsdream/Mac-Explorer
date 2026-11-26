using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using CodeWF.Markdown;

namespace MacExplorer.Views;

/// <summary>The same native renderer is used by Space preview and the editor.</summary>
public partial class MarkdownPreviewView : UserControl
{
    private static readonly Geometry CopyIcon = Geometry.Parse(Assets.Icons.Copy);

    private string? _path;

    public MarkdownPreviewView()
    {
        InitializeComponent();
        Viewer.CodeBlockToolRender += OnCodeBlockToolRender;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    // The renderer labels its code-block button with text; the app uses icon buttons.
    private static void OnCodeBlockToolRender(object? sender, CodeBlockToolRenderEventArgs e)
    {
        var copyButton = e.HeaderPanel.Children
            .OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains(MarkdownStyleKeys.CopyButton) || button.Classes.Contains("markdown-copy"));
        if (copyButton is null)
        {
            return;
        }

        if (copyButton.Content is string label)
        {
            ToolTip.SetTip(copyButton, label);
            AutomationProperties.SetName(copyButton, label);
        }

        // Use the shared icon-action appearance instead of renderer-specific brushes.
        copyButton.ClearValue(Button.BackgroundProperty);
        copyButton.ClearValue(Button.ForegroundProperty);
        copyButton.Classes.Remove(MarkdownStyleKeys.CopyButton);
        copyButton.Classes.Add("markdown-copy");
        copyButton.Classes.Add("icon-action");
        copyButton.Content = new PathIcon { Data = CopyIcon, Width = 13, Height = 13 };
    }

    public void SetDocument(string text, string filePath)
    {
        if (!string.Equals(_path, filePath, StringComparison.Ordinal))
        {
            Viewer.Markdown = string.Empty;
            Viewer.ImageBasePath = filePath;
            Scroll.Offset = Vector.Zero;
            _path = filePath;
        }
        Viewer.Markdown = text;
    }

    public void Clear()
    {
        // Queue an empty render before detaching, so the renderer cleans up image/code-block resources.
        Viewer.Markdown = string.Empty;
        Viewer.ImageBasePath = null;
        Viewer.Rerender();
        Scroll.Offset = Vector.Zero;
        _path = null;
    }

    internal void SetNavigationGutters(double left, double right)
        => Scroll.Padding = new Thickness(left, 0, right, 0);

    internal static bool OwnsCopyGesture(KeyEventArgs e)
        => IsCopyGesture(e) && e.Source is Visual source
            && (source is MarkdownPreviewView || source.GetVisualAncestors().Any(ancestor => ancestor is MarkdownPreviewView));

    private static bool IsCopyGesture(KeyEventArgs e)
        => e.Key == Key.C && e.KeyModifiers is KeyModifiers.Meta or KeyModifiers.Control;

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsCopyGesture(e)) return;

        // The viewer reports a block range. Character selection belongs to the focused text block.
        e.Handled = true;
        if (Viewer.HasSelection)
        {
            try { await Viewer.CopySelectionAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Markdown copy failed: {ex}"); }
        }
        else if (e.Source is SelectableTextBlock textBlock)
        {
            textBlock.Copy();
        }
    }
}

/// <summary>Applies editor typography to values assigned locally by the Markdown renderer.</summary>
internal sealed class EditorProse : AvaloniaObject
{
    public enum BlockPart { None, Title, Section, Quote }

    public static readonly AttachedProperty<BlockPart> PartProperty =
        AvaloniaProperty.RegisterAttached<EditorProse, Control, BlockPart>("Part");

    public static BlockPart GetPart(Control control) => control.GetValue(PartProperty);
    public static void SetPart(Control control, BlockPart value) => control.SetValue(PartProperty, value);

    static EditorProse()
    {
        PartProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            switch (control, change.GetNewValue<BlockPart>())
            {
                case (Border title, BlockPart.Title):
                    title.Margin = new Thickness(0, 0, 0, 16);
                    title.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("EditorDividerBrush"));
                    break;
                case (Border section, BlockPart.Section):
                    section.Margin = new Thickness(0, 28, 0, 12);
                    break;
                case (SelectableTextBlock heading, BlockPart.Section):
                    heading.FontWeight = FontWeight.SemiBold;
                    break;
                case (Border quote, BlockPart.Quote):
                    quote.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("EditorQuoteLineBrush"));
                    break;
                case (SelectableTextBlock paragraph, BlockPart.Quote):
                    paragraph.Margin = default;
                    paragraph.Bind(SelectableTextBlock.ForegroundProperty, new DynamicResourceExtension("EditorQuoteTextBrush"));
                    break;
            }
        });
    }
}
