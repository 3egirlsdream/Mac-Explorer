using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MacExplorer.Views;

namespace MacExplorer.Services.Impl;

internal readonly record struct WindowPlacement(
    double NormalWidth,
    double NormalHeight,
    PixelPoint Position,
    bool IsMaximized);

internal sealed class WindowPlacementService
{
    private readonly Dictionary<Window, WindowPlacement> _normalPlacements = [];

    public void Prepare(MainWindow window, bool isPrimary, MainWindow? source)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        var placement = isPrimary ? CenterOnPrimary(window, 1280, 800, false) : CreateSecondary(window, source);
        Apply(window, placement);
        _normalPlacements[window] = placement with { IsMaximized = false };
        window.SizeChanged += (_, _) => CaptureNormal(window);
        window.PositionChanged += (_, _) => CaptureNormal(window);
        window.Closed += (_, _) => _normalPlacements.Remove(window);
    }

    private WindowPlacement CreateSecondary(Window window, MainWindow? source)
    {
        if (source == null || !_normalPlacements.TryGetValue(source, out var sourcePlacement))
            return CenterOnPrimary(window, 1280, 800, false);

        var screen = source.Screens.ScreenFromPoint(sourcePlacement.Position)
                     ?? source.Screens.ScreenFromWindow(source)
                     ?? window.Screens.Primary;
        if (screen == null)
            return sourcePlacement with { IsMaximized = false };

        var offset = (int)Math.Round(24 * screen.Scaling);
        var candidate = sourcePlacement with
        {
            Position = new PixelPoint(sourcePlacement.Position.X + offset, sourcePlacement.Position.Y + offset),
            IsMaximized = false
        };
        return WindowPlacementMath.FitsWithinWorkingArea(candidate, screen.WorkingArea, screen.Scaling)
            ? ClampToScreen(candidate, screen, window.MinWidth, window.MinHeight)
            : CenterOnScreen(candidate.NormalWidth, candidate.NormalHeight, false, screen, window.MinWidth, window.MinHeight);
    }

    private void CaptureNormal(Window window)
    {
        if (window.WindowState != WindowState.Normal || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
            return;
        _normalPlacements[window] = new WindowPlacement(
            window.Bounds.Width,
            window.Bounds.Height,
            window.Position,
            false);
    }

    private static void Apply(Window window, WindowPlacement placement)
    {
        window.Width = placement.NormalWidth;
        window.Height = placement.NormalHeight;
        window.Position = placement.Position;
    }

    private static WindowPlacement CenterOnPrimary(Window window, double width, double height, bool maximized)
    {
        var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
        return screen == null
            ? new WindowPlacement(width, height, default, maximized)
            : CenterOnScreen(width, height, maximized, screen, window.MinWidth, window.MinHeight);
    }

    private static WindowPlacement CenterOnScreen(
        double width, double height, bool maximized, Screen screen, double minWidth, double minHeight)
        => WindowPlacementMath.Center(width, height, maximized, screen.WorkingArea, screen.Scaling, minWidth, minHeight);

    private static WindowPlacement ClampToScreen(
        WindowPlacement placement, Screen screen, double minWidth, double minHeight)
        => WindowPlacementMath.Clamp(placement, screen.WorkingArea, screen.Scaling, minWidth, minHeight);
}

internal static class WindowPlacementMath
{
    internal static WindowPlacement Center(
        double width,
        double height,
        bool maximized,
        PixelRect workingArea,
        double scaling,
        double minWidth,
        double minHeight)
    {
        var maxWidth = workingArea.Width / scaling;
        var maxHeight = workingArea.Height / scaling;
        width = Math.Min(maxWidth, Math.Max(Math.Min(minWidth, maxWidth), width));
        height = Math.Min(maxHeight, Math.Max(Math.Min(minHeight, maxHeight), height));
        var pixelWidth = (int)Math.Round(width * scaling);
        var pixelHeight = (int)Math.Round(height * scaling);
        return new WindowPlacement(
            width,
            height,
            new PixelPoint(
                workingArea.X + (workingArea.Width - pixelWidth) / 2,
                workingArea.Y + (workingArea.Height - pixelHeight) / 2),
            maximized);
    }

    internal static WindowPlacement Clamp(
        WindowPlacement placement,
        PixelRect workingArea,
        double scaling,
        double minWidth,
        double minHeight)
    {
        var sized = Center(
            placement.NormalWidth,
            placement.NormalHeight,
            placement.IsMaximized,
            workingArea,
            scaling,
            minWidth,
            minHeight);
        var pixelWidth = (int)Math.Round(sized.NormalWidth * scaling);
        var pixelHeight = (int)Math.Round(sized.NormalHeight * scaling);
        var x = Math.Clamp(placement.Position.X, workingArea.X, workingArea.Right - pixelWidth);
        var y = Math.Clamp(placement.Position.Y, workingArea.Y, workingArea.Bottom - pixelHeight);
        return sized with { Position = new PixelPoint(x, y) };
    }

    internal static bool HasUsefulIntersection(WindowPlacement placement, PixelRect workingArea, double scaling)
    {
        var width = (int)Math.Round(placement.NormalWidth * scaling);
        var height = (int)Math.Round(placement.NormalHeight * scaling);
        var right = Math.Min(placement.Position.X + width, workingArea.Right);
        var bottom = Math.Min(placement.Position.Y + height, workingArea.Bottom);
        var intersectionWidth = right - Math.Max(placement.Position.X, workingArea.X);
        var intersectionHeight = bottom - Math.Max(placement.Position.Y, workingArea.Y);
        var usefulSize = (int)Math.Round(64 * scaling);
        return intersectionWidth >= usefulSize && intersectionHeight >= usefulSize;
    }

    internal static bool FitsWithinWorkingArea(WindowPlacement placement, PixelRect workingArea, double scaling)
    {
        var width = (int)Math.Round(placement.NormalWidth * scaling);
        var height = (int)Math.Round(placement.NormalHeight * scaling);
        return placement.Position.X >= workingArea.X
               && placement.Position.Y >= workingArea.Y
               && placement.Position.X + width <= workingArea.Right
               && placement.Position.Y + height <= workingArea.Bottom;
    }
}
