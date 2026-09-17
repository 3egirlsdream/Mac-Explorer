using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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
            .FirstOrDefault(button => button.Classes.Contains(MarkdownStyleKeys.CopyButton));
        if (copyButton is null)
        {
            return;
        }

        if (copyButton.Content is string label)
        {
            ToolTip.SetTip(copyButton, label);
            AutomationProperties.SetName(copyButton, label);
        }

        // Release the renderer's brush bindings so the scoped app styles supply the
        // fill, hover and pressed states.
        copyButton.ClearValue(Button.BackgroundProperty);
        copyButton.ClearValue(Button.ForegroundProperty);
        copyButton.Classes.Add("md-copy");
        // The box is 28px wide and the icon 13px, so every inset lands on a half
        // pixel; rounding them is what pushed the glyph off centre.
        copyButton.UseLayoutRounding = false;
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

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // The compatible renderer version provides Ctrl+C; add the macOS equivalent.
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Meta
            && (Viewer.HasSelection || ReferenceEquals(e.Source, Viewer)))
        {
            e.Handled = true;
            try { await Viewer.CopyRenderedTextAsync(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Markdown copy failed: {ex}"); }
        }
    }
}
