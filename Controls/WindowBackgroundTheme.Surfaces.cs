using Avalonia.Controls;
using Avalonia.Media;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

public sealed partial class WindowBackgroundTheme
{
    internal Color PopupColor => (Color)_window.FindResource(_window.ActualThemeVariant, "PopupGlassColor")!;

    private void UpdateControlSurfaces(bool dark)
    {
        // Bridge the same semantic and Fluent template resources as the interaction palette.
        SetGroup(InteractionStyleService.GetResourceKeys(InteractionStyleToken.Hover), dark ? "#28FFFFFF" : "#3DFFFFFF");
        SetGroup(InteractionStyleService.GetResourceKeys(InteractionStyleToken.Selected), dark ? "#40689AE6" : "#3093B9F4");
        SetGroup(InteractionStyleService.GetResourceKeys(InteractionStyleToken.SelectedHover), dark ? "#58689AE6" : "#5093B9F4");
        SetGroup(["ControlHoverBrush", "ColorBgHover", "SidebarHoverBrush"], dark ? "#28FFFFFF" : "#3DFFFFFF");
        SetGroup(["ColorBgHoverStrong", "ColorBgActive", "ButtonBackgroundPressed", "ToggleButtonBackgroundPressed",
            "ComboBoxBackgroundPressed", "ComboBoxItemBackgroundPressed", "ListBoxItemBackgroundPressed"], dark ? "#38FFFFFF" : "#66FFFFFF");
        SetGroup(["ColorBgSelectedHover"], dark ? "#58689AE6" : "#5093B9F4");
        SetGroup(["ToggleButtonBackgroundChecked"], dark ? "#40689AE6" : "#3093B9F4");
        SetGroup(["SurfaceBrush", "SurfaceElevatedBrush", "SurfaceMutedBrush", "ColorSurfaceSecondary",
            "ColorBgContent", "SurfaceBackgroundBrush", "ComboBoxBackgroundUnfocused", "ButtonBackground", "ToggleButtonBackground",
            "CheckBoxSurfaceBrush"], dark ? "#281B1D21" : "#28FFFFFF");
        SetGroup(["BorderBrush", "ColorBorder", "ColorBorderSubtle", "ColorBorderPrimary", "ColorDivider"], dark ? "#24FFFFFF" : "#18000000");
        SetActionBrush("WorkspacePrimaryBrush", "AccentBrush", 230);
        SetActionBrush("WorkspacePrimaryHoverBrush", "AccentHoverBrush", 240);
        SetActionBrush("WorkspaceDangerBrush", "DangerBrush", 230);
        SetActionBrush("WorkspaceDangerHoverBrush", "DangerHoverBrush", 240);
        SetGroup(["GlassMenuBg", "GlassPanelBg", "GlassEffectBg", "GlassEffectBgSubmenu"], dark ? "#B81B1D21" : "#A6FFFFFF");
        // Retain the popup lens and enough neutral wash to keep text readable over photos.
        _window.Resources["PopupGlassColor"] = Color.Parse(dark ? "#B81B1D21" : "#A6FFFFFF");
        _surfaceResources.Add("PopupGlassColor");
    }

    private void SetActionBrush(string key, string source, byte alpha)
    {
        if (_window.FindResource(_window.ActualThemeVariant, source) is not ISolidColorBrush brush) return;
        var color = brush.Color;
        _window.Resources[key] = new SolidColorBrush(Color.FromArgb(Math.Min(alpha, color.A), color.R, color.G, color.B));
        _surfaceResources.Add(key);
    }

    private void SetGroup(IEnumerable<string> keys, string color)
    {
        var brush = new SolidColorBrush(Color.Parse(color));
        foreach (var key in keys)
        {
            _window.Resources[key] = brush;
            _surfaceResources.Add(key);
        }
    }
}
