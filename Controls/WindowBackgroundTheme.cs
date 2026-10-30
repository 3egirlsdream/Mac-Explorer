using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Scopes theme surfaces to a main window; dialogs and popup resources stay unchanged.</summary>
public sealed class WindowBackgroundTheme : AvaloniaObject, IDisposable
{
    public static readonly AttachedProperty<BackgroundThemeService?> ServiceProperty =
        AvaloniaProperty.RegisterAttached<WindowBackgroundTheme, Control, BackgroundThemeService?>("Service", inherits: true);
    public static BackgroundThemeService? GetService(Control control) => control.GetValue(ServiceProperty);
    private static readonly string[] ClearSurfaces =
        ["WorkspaceContentBrush", "WorkspaceSurfaceBrush", "WorkspaceInfoBrush"];
    private readonly AppWindow _window;
    private readonly BackgroundThemeService _service;

    public WindowBackgroundTheme(AppWindow window, BackgroundThemeService service)
    {
        _window = window; _service = service;
        window.SetValue(ServiceProperty, service);
        window.BackgroundContent = new BackgroundThemeSurface();
        service.Changed += Update;
        window.ActualThemeVariantChanged += OnThemeChanged;
        window.Closed += OnClosed;
        Update();
        _ = service.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();
    private void OnThemeChanged(object? sender, EventArgs e) => Update();

    private void Update()
    {
        foreach (var key in ClearSurfaces)
        {
            if (_service.IsActive) _window.Resources[key] = Brushes.Transparent;
            else _window.Resources.Remove(key);
        }
        if (_service.IsActive)
        {
            _window.Resources["WorkspaceTabBrush"] = Brushes.Transparent;
            if (_window.TryFindResource("ThemedWorkspaceStripeBrush", _window.ActualThemeVariant, out var stripe))
                _window.Resources["WorkspaceStripeBrush"] = stripe;
        }
        else
        {
            _window.Resources.Remove("WorkspaceTabBrush");
            _window.Resources.Remove("WorkspaceStripeBrush");
        }
    }

    public void Dispose()
    {
        _service.Changed -= Update;
        _window.ActualThemeVariantChanged -= OnThemeChanged;
        _window.Closed -= OnClosed;
        _window.ClearValue(ServiceProperty);
    }
}
