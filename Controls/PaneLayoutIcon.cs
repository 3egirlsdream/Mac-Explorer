using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Filled vector preview of the actual workspace pane arrangement.</summary>
public sealed class PaneLayoutIcon : Control
{
    public static readonly StyledProperty<PaneLayout> LayoutProperty =
        AvaloniaProperty.Register<PaneLayoutIcon, PaneLayout>(nameof(Layout));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<PaneLayoutIcon, IBrush?>(nameof(Foreground), Brushes.LightGray);

    public static readonly StyledProperty<IBrush?> BorderBrushProperty =
        AvaloniaProperty.Register<PaneLayoutIcon, IBrush?>(nameof(BorderBrush));

    public PaneLayout Layout
    {
        get => GetValue(LayoutProperty);
        set => SetValue(LayoutProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public IBrush? BorderBrush
    {
        get => GetValue(BorderBrushProperty);
        set => SetValue(BorderBrushProperty, value);
    }

    static PaneLayoutIcon()
    {
        AffectsRender<PaneLayoutIcon>(LayoutProperty, ForegroundProperty, BorderBrushProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var pen = BorderBrush is { } border ? new Pen(border, 0.75) : null;
        foreach (var pane in GetPaneRects(Layout, Bounds.Size))
            context.DrawRectangle(Foreground, pen, pane, 1.5, 1.5);
    }

    internal static IReadOnlyList<Rect> GetPaneRects(PaneLayout layout, Size size)
    {
        if (size.Width <= 2 || size.Height <= 2)
            return [];

        var definition = MainWindowViewModel.GetPaneLayoutDefinition(layout);
        var area = new Rect(1, 1, size.Width - 2, size.Height - 2);
        var gap = Math.Min(2, Math.Min(area.Width / definition.Columns, area.Height / definition.Rows) / 3);
        var totalColumnWeight = definition.ColumnWeights?.Sum() ?? definition.Columns;
        var totalRowWeight = definition.RowWeights?.Sum() ?? definition.Rows;
        var unitWidth = (area.Width - gap * (definition.Columns - 1)) / totalColumnWeight;
        var unitHeight = (area.Height - gap * (definition.Rows - 1)) / totalRowWeight;
        var panes = new List<Rect>(definition.Slots.Count);

        foreach (var slot in definition.Slots)
        {
            var precedingColumns = definition.ColumnWeights?.Take(slot.Column).Sum() ?? slot.Column;
            var precedingRows = definition.RowWeights?.Take(slot.Row).Sum() ?? slot.Row;
            var columnWeight = definition.ColumnWeights?.Skip(slot.Column).Take(slot.ColumnSpan).Sum() ?? slot.ColumnSpan;
            var rowWeight = definition.RowWeights?.Skip(slot.Row).Take(slot.RowSpan).Sum() ?? slot.RowSpan;
            panes.Add(new Rect(
                area.X + precedingColumns * unitWidth + slot.Column * gap,
                area.Y + precedingRows * unitHeight + slot.Row * gap,
                columnWeight * unitWidth + (slot.ColumnSpan - 1) * gap,
                rowWeight * unitHeight + (slot.RowSpan - 1) * gap));
        }

        return panes;
    }
}
