using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace MacExplorer.Views;

internal partial class DiffPreviewView
{
    private sealed class NumberMargin(DiffPreviewView owner, Pane pane) : AbstractMargin
    {
        internal string? GetNumber(int documentLine) => pane.Lines is { } lines && documentLine > 0 && documentLine <= lines.Length
            ? lines[documentLine - 1]?.Number?.ToString(CultureInfo.InvariantCulture) : null;

        protected override Size MeasureOverride(Size availableSize)
        {
            var max = pane.Lines?.Select(line => line?.Number ?? 0).DefaultIfEmpty(0).Max() ?? 0;
            return new Size(Format(new string('9', Math.Max(2, max.ToString(CultureInfo.InvariantCulture).Length))).Width, 0);
        }

        public override void Render(DrawingContext context)
        {
            if (TextView is not { VisualLinesValid: true } view) return;
            foreach (var line in view.VisualLines)
            {
                var number = GetNumber(line.FirstDocumentLine.LineNumber);
                if (number == null) continue;
                var text = Format(number);
                var top = line.GetTextLineVisualYPosition(line.TextLines[0], VisualYPosition.TextTop) - view.VerticalOffset;
                context.DrawText(text, new Point(Bounds.Width - text.Width, top));
            }
        }

        private FormattedText Format(string text) => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(pane.Editor.FontFamily), pane.Editor.FontSize, owner.Brush("TextMutedBrush") ?? Brushes.Gray);
    }
}
