using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views;

internal partial class DiffPreviewView : UserControl
{
    private readonly Pane _before;
    private readonly Pane _after;
    private bool _syncingScroll;

    public DiffPreviewView()
    {
        InitializeComponent();
        _before = ConfigurePane(Editor);
        _after = ConfigurePane(AfterEditor);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private Pane ConfigurePane(TextEditor editor)
    {
        var pane = new Pane(this, editor);
        editor.Options.CutCopyWholeLine = false;
        editor.Options.AllowScrollBelowDocument = false;
        editor.Options.LineHeightFactor = 1.4;
        editor.TextArea.TextView.LineTransformers.Add(pane);
        editor.TextArea.TextView.BackgroundRenderers.Add(pane);
        editor.TextArea.LeftMargins.Add(pane.Gutter);
        // The default command normalizes newlines; projected rows also contain alignment padding.
        editor.TextArea.CommandBindings.Insert(0, new RoutedCommandBinding(ApplicationCommands.Copy,
            (_, _) => CopyOriginalText(pane), (_, e) => { e.CanExecute = editor.SelectionLength > 0; e.Handled = true; }));
        return pane;
    }

    internal void SetDocument(DiffPreviewDocument document)
    {
        _syncingScroll = true;
        try
        {
            var comparison = document.Comparison;
            var sideBySide = comparison != null;
            BeforeLabel.IsVisible = AfterLabel.IsVisible = Divider.IsVisible = AfterEditor.IsVisible = sideBySide;
            Grid.SetColumnSpan(Editor, sideBySide ? 1 : 3);
            _before.SetLines(comparison?.Before, document.Lines);
            _after.SetLines(comparison?.After, []);
            Editor.Document = new TextDocument(comparison == null ? document.Text : DiffComparison.Project(comparison.Before));
            AfterEditor.Document = new TextDocument(comparison == null ? "" : DiffComparison.Project(comparison.After));
            Editor.ScrollToHome();
            AfterEditor.ScrollToHome();
        }
        finally { _syncingScroll = false; }
    }

    internal void Clear()
    {
        _syncingScroll = true;
        try
        {
            _before.SetLines(null, []);
            _after.SetLines(null, []);
            Editor.Document = new TextDocument();
            AfterEditor.Document = new TextDocument();
        }
        finally { _syncingScroll = false; }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged += OnThemeChanged;
        Editor.TextArea.TextView.ScrollOffsetChanged += OnBeforeScroll;
        AfterEditor.TextArea.TextView.ScrollOffsetChanged += OnAfterScroll;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnThemeChanged;
        Editor.TextArea.TextView.ScrollOffsetChanged -= OnBeforeScroll;
        AfterEditor.TextArea.TextView.ScrollOffsetChanged -= OnAfterScroll;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnBeforeScroll(object? sender, EventArgs e) => SyncScroll(Editor, AfterEditor);
    private void OnAfterScroll(object? sender, EventArgs e) => SyncScroll(AfterEditor, Editor);

    private void SyncScroll(TextEditor source, TextEditor target)
    {
        if (_syncingScroll || _before.Lines == null) return;
        _syncingScroll = true;
        try
        {
            var scroll = (IScrollable)target.TextArea.TextView;
            scroll.Offset = new Vector(scroll.Offset.X, source.TextArea.TextView.VerticalOffset);
        }
        finally { _syncingScroll = false; }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        foreach (var pane in new[] { _before, _after })
        {
            pane.Editor.TextArea.TextView.Redraw();
            pane.Gutter.InvalidateVisual();
        }
    }

    private async void CopyOriginalText(Pane pane)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        var editor = pane.Editor;
        var text = editor.SelectedText;
        if (pane.Lines is { } lines)
        {
            var start = editor.Document.GetLocation(editor.SelectionStart);
            var end = editor.Document.GetLocation(editor.SelectionStart + editor.SelectionLength);
            text = DiffComparison.Copy(lines, start.Line - 1, start.Column - 1, end.Line - 1, end.Column - 1);
        }
        try { await clipboard.SetTextAsync(text); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Diff preview copy failed: {ex}"); }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers != command) return;
        var editor = AfterEditor.IsKeyboardFocusWithin ? AfterEditor : Editor;
        if (e.Key == Key.C) { editor.Copy(); e.Handled = true; }
        else if (e.Key == Key.A) { editor.SelectAll(); e.Handled = true; }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as IBrush : null;

    private sealed class Pane : DocumentColorizingTransformer, IBackgroundRenderer
    {
        private readonly DiffPreviewView _owner;
        private DiffLineKind[] _rawKinds = [];
        internal TextEditor Editor { get; }
        internal DiffPaneLine?[]? Lines { get; private set; }
        internal NumberMargin Gutter { get; }
        public KnownLayer Layer => KnownLayer.Background;

        internal Pane(DiffPreviewView owner, TextEditor editor)
        {
            _owner = owner;
            Editor = editor;
            Gutter = new NumberMargin(owner, this) { Margin = new Thickness(0, 0, 8, 0) };
        }

        internal void SetLines(DiffPaneLine?[]? lines, DiffLineKind[] rawKinds)
        {
            Lines = lines;
            _rawKinds = rawKinds;
            Gutter.IsVisible = lines != null;
            Gutter.InvalidateMeasure();
            Gutter.InvalidateVisual();
        }

        private DiffLineKind Kind(int index) => Lines != null
            ? (index < Lines.Length ? Lines[index]?.Kind ?? DiffLineKind.Context : DiffLineKind.Context)
            : (index < _rawKinds.Length ? _rawKinds[index] : DiffLineKind.Context);

        protected override void ColorizeLine(DocumentLine line)
        {
            if (line.Length == 0) return;
            var key = Kind(line.LineNumber - 1) switch
            {
                DiffLineKind.Added => "SuccessBrush",
                DiffLineKind.Removed => "DangerBrush",
                DiffLineKind.Header or DiffLineKind.Hunk => "AccentBrush",
                _ => "TextPrimaryBrush"
            };
            if (_owner.Brush(key) is { } brush)
                ChangeLinePart(line.Offset, line.EndOffset, element => element.TextRunProperties.SetForegroundBrush(brush));
        }

        public void Draw(TextView textView, DrawingContext context)
        {
            if (Lines == null || !textView.VisualLinesValid) return;
            foreach (var visual in textView.VisualLines)
            {
                var index = visual.FirstDocumentLine.LineNumber - 1;
                if (index >= Lines.Length) continue;
                var key = Kind(index) switch
                {
                    DiffLineKind.Added => "SuccessBrush",
                    DiffLineKind.Removed => "DangerBrush",
                    _ => Lines[index] == null ? "TextMutedBrush" : null
                };
                if (key == null || _owner.Brush(key) is not { } brush) continue;
                using var opacity = context.PushOpacity(0.08);
                context.FillRectangle(brush, new Rect(0, visual.VisualTop - textView.VerticalOffset, textView.Bounds.Width, visual.Height));
            }
        }
    }
}
