using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.ViewModels;

namespace MacExplorer.Controls;

/// <summary>Small Finder-style diagram used by the pane layout picker.</summary>
public sealed class PaneLayoutIcon : Control
{
    public static readonly StyledProperty<PaneLayout> LayoutProperty =
        AvaloniaProperty.Register<PaneLayoutIcon, PaneLayout>(nameof(Layout));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<PaneLayoutIcon, IBrush?>(nameof(Foreground), Brushes.White);

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

    static PaneLayoutIcon()
    {
        AffectsRender<PaneLayoutIcon>(LayoutProperty, ForegroundProperty);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Foreground == null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        const double gap = 4;
        var area = new Rect(1, 1, Bounds.Width - 2, Bounds.Height - 2);
        var pen = new Pen(Foreground, 2);

        void Outline(Rect rect) => context.DrawRectangle(null, pen, rect, 0.7, 0.7);
        void Fill(Rect rect) => context.DrawRectangle(Foreground, null, rect, 0.7, 0.7);

        void Columns(int count)
        {
            var width = (area.Width - gap * (count - 1)) / count;
            for (var index = 0; index < count; index++)
                Outline(new Rect(area.X + index * (width + gap), area.Y, width, area.Height));
        }

        void Rows(int count)
        {
            var height = (area.Height - gap * (count - 1)) / count;
            for (var index = 0; index < count; index++)
                Outline(new Rect(area.X, area.Y + index * (height + gap), area.Width, height));
        }

        void MainWithStack(bool mainOnLeft, int secondaryCount)
        {
            var mainWidth = area.Width * 0.35;
            var secondaryX = mainOnLeft ? area.X + mainWidth + gap * 1.5 : area.X;
            var secondaryWidth = area.Width - mainWidth - gap * 1.5;
            var mainX = mainOnLeft ? area.X : area.Right - mainWidth;
            Fill(new Rect(mainX, area.Y, mainWidth, area.Height));

            var height = (area.Height - gap * (secondaryCount - 1)) / secondaryCount;
            for (var index = 0; index < secondaryCount; index++)
                Outline(new Rect(secondaryX, area.Y + index * (height + gap), secondaryWidth, height));
        }

        switch (Layout)
        {
            case PaneLayout.Single:
                Outline(new Rect(area.X + area.Width * 0.3, area.Y + area.Height * 0.2,
                    area.Width * 0.4, area.Height * 0.6));
                break;
            case PaneLayout.TwoColumns: Columns(2); break;
            case PaneLayout.TwoRows: Rows(2); break;
            case PaneLayout.ThreeColumns: Columns(3); break;
            case PaneLayout.ThreeRows: Rows(3); break;
            case PaneLayout.MainLeftTwoRowsRight: MainWithStack(true, 2); break;
            case PaneLayout.MainRightTwoRowsLeft: MainWithStack(false, 2); break;
            case PaneLayout.FourGrid:
            {
                var width = (area.Width - gap) / 2;
                var height = (area.Height - gap) / 2;
                for (var row = 0; row < 2; row++)
                    for (var column = 0; column < 2; column++)
                        Outline(new Rect(area.X + column * (width + gap), area.Y + row * (height + gap), width, height));
                break;
            }
            case PaneLayout.FourColumns: Columns(4); break;
            case PaneLayout.FourRows: Rows(4); break;
            case PaneLayout.MainLeftThreeRowsRight: MainWithStack(true, 3); break;
            case PaneLayout.MainRightThreeRowsLeft: MainWithStack(false, 3); break;
        }
    }
}
