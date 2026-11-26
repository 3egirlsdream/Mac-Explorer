using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace MacExplorer.Controls;

internal sealed class EditorLineNumberMargin : LineNumberMargin
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<EditorLineNumberMargin, IBrush?>(nameof(Background));

    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<EditorLineNumberMargin, IBrush?>(nameof(DividerBrush));

    public static readonly StyledProperty<IBrush?> ActiveForegroundProperty =
        AvaloniaProperty.Register<EditorLineNumberMargin, IBrush?>(nameof(ActiveForeground));

    private const double LeftPadding = 8;
    private const double RightPadding = 12;
    private const double DividerWidth = 1;

    static EditorLineNumberMargin()
    {
        AffectsRender<EditorLineNumberMargin>(BackgroundProperty, DividerBrushProperty, ActiveForegroundProperty,
            TextBlock.ForegroundProperty);
        AffectsMeasure<EditorLineNumberMargin>(TextBlock.FontFamilyProperty, TextBlock.FontSizeProperty);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    public IBrush? ActiveForeground
    {
        get => GetValue(ActiveForegroundProperty);
        set => SetValue(ActiveForegroundProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var digits = base.MeasureOverride(availableSize);
        return new Size(Math.Max(48, digits.Width + LeftPadding + RightPadding + DividerWidth), digits.Height);
    }

    protected override void OnTextViewChanged(TextView oldTextView, TextView newTextView)
    {
        if (TextArea is { } oldArea)
            oldArea.Caret.PositionChanged -= OnCaretPositionChanged;
        base.OnTextViewChanged(oldTextView, newTextView);
        if (TextArea is { } newArea)
            newArea.Caret.PositionChanged += OnCaretPositionChanged;
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(Background, null, bounds);
        context.DrawRectangle(DividerBrush, null, new Rect(bounds.Width - DividerWidth, 0, DividerWidth, bounds.Height));

        if (TextView is not { VisualLinesValid: true } textView ||
            textView.TranslatePoint(default, this) is not { } origin)
            return;

        var caretLine = TextArea?.Caret.Line;
        var foreground = GetValue(TextBlock.ForegroundProperty);
        var activeTypeface = new Typeface(Typeface.FontFamily, Typeface.Style, FontWeight.Medium);
        using var clip = context.PushClip(new Rect(0, origin.Y, bounds.Width, textView.Bounds.Height));
        foreach (var line in textView.VisualLines)
        {
            var number = line.FirstDocumentLine.LineNumber;
            var active = number == caretLine;
            var text = new FormattedText(number.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, active ? activeTypeface : Typeface, EmSize, active ? ActiveForeground : foreground);
            var firstRow = line.TextLines[0];
            var y = line.GetTextLineVisualYPosition(firstRow, VisualYPosition.TextTop) - textView.VerticalOffset + origin.Y;
            context.DrawText(text, new Point(bounds.Width - RightPadding - DividerWidth - text.Width, y));
        }
    }
}
