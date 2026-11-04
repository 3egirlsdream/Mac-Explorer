using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Controls;

/// <summary>Scopes translucent surfaces and popup materials to a main window; independent dialogs keep their defaults.</summary>
public sealed partial class WindowBackgroundTheme : AvaloniaObject, IDisposable, IMemoryDiagnosticSource
{
    public static readonly AttachedProperty<BackgroundThemeService?> ServiceProperty =
        AvaloniaProperty.RegisterAttached<WindowBackgroundTheme, Control, BackgroundThemeService?>("Service", inherits: true);
    public static BackgroundThemeService? GetService(Control control) => control.GetValue(ServiceProperty);
    private static readonly AttachedProperty<WindowBackgroundTheme?> ControllerProperty =
        AvaloniaProperty.RegisterAttached<WindowBackgroundTheme, Control, WindowBackgroundTheme?>("Controller", inherits: true);
    internal static WindowBackgroundTheme? GetController(Control control) => control.GetValue(ControllerProperty);
    private static readonly SemaphoreSlim RenderSlots = new(2, 2);
    private static readonly string[] ClearSurfaces =
        ["WorkspaceContentBrush", "WorkspaceSurfaceBrush", "WorkspaceInfoBrush"];
    private static readonly string[] ChromeResources = ["TitleBarBackgroundBrush", "ViewModeToggleBg", "ViewModeToggleHoverBg",
        "ViewModeToggleSelectedBg", "WorkspacePreviewBrush", "WorkspaceControlHoverBrush", "WorkspaceControlPressedBrush", "WorkspaceControlSelectedBrush",
        "WorkspaceControlBorderBrush", "WorkspaceDividerBrush", "WorkspaceInfoTagBrush", "WorkspacePreviewBadgeBrush", "WorkspacePreviewActionBrush",
        "TextSecondaryBrush", "TextMutedBrush", "TextSubtleBrush"];
    private readonly HashSet<string> _surfaceResources = [];
    private readonly AppWindow _window;
    private readonly BackgroundThemeService _service;
    private CancellationTokenSource? _renderCancellation;
    private Task? _renderTask;
    private Task? _initialShowTask;
    private bool _initialShowCompleted;
    private bool _disposed;
    internal BackgroundThemeComposition? Frame { get; private set; }
    internal bool IsActive => _service.IsActive && Frame != null;
    internal bool IsRendering => _renderCancellation != null;
    internal event Action? FrameChanged;

    public WindowBackgroundTheme(AppWindow window, BackgroundThemeService service)
    {
        _window = window; _service = service;
        MemoryDiagnostics.Register(this);
        window.SetValue(ServiceProperty, service);
        window.SetValue(ControllerProperty, this);
        window.BackgroundContent = new BackgroundThemeSurface();
        service.Changed += Update;
        window.ActualThemeVariantChanged += OnThemeChanged;
        window.Closed += OnClosed;
        window.SizeChanged += OnSizeChanged;
        window.PropertyChanged += OnWindowPropertyChanged;
        Update();
        _ = service.InitializeAsync();
    }

    MemoryDiagnosticState IMemoryDiagnosticSource.GetMemoryState() => new("background-frame", Frame == null ? 0 : 2,
        Frame is { } frame ? ((long)frame.Image.PixelSize.Width * frame.Image.PixelSize.Height
            + (long)frame.Frosted.PixelSize.Width * frame.Frosted.PixelSize.Height) * 4 : 0, IsRendering ? 1 : 0);

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    internal Task ShowWhenReadyAsync(Action show)
    {
        if (_disposed) return Task.CompletedTask;
        if (_initialShowTask is { IsCompleted: false }) return _initialShowTask;
        if (_initialShowCompleted || !_service.Options.Enabled
            || (_service.Options.ImageFile.Length == 0 && _service.Options.PresetId.Length == 0))
        {
            _initialShowCompleted = true;
            show();
            return Task.CompletedTask;
        }
        return _initialShowTask = ShowInitialAsync(show);
    }

    private async Task ShowInitialAsync(Action show)
    {
        try
        {
            await _service.InitializeForDisplayAsync();
            if (!_disposed && _service.IsActive)
            {
                ScheduleRender();
                while (_renderTask is { } pending)
                {
                    await pending;
                    if (ReferenceEquals(_renderTask, pending)) break;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Initial background preparation failed: {ex.Message}");
        }
        _initialShowCompleted = true;
        if (!_disposed) show();
    }
    private void OnThemeChanged(object? sender, EventArgs e) => Update();
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleRender();
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property.Name == nameof(TopLevel.RenderScaling)) ScheduleRender();
    }

    private void Update()
    {
        foreach (var key in ClearSurfaces)
        {
            if (_service.IsActive) _window.Resources[key] = Brushes.Transparent;
            else _window.Resources.Remove(key);
        }
        if (_service.IsActive)
        {
            _window.Resources["WorkspaceSidebarRefraction"] = _window.ActualThemeVariant == ThemeVariant.Dark ? 24d : 36d;
            _window.Resources["WorkspaceSidebarRefractionHeight"] = 24d;
            _window.Resources["WorkspaceTabBrush"] = Brushes.Transparent;
            if (_window.TryFindResource("ThemedWorkspaceStripeBrush", _window.ActualThemeVariant, out var stripe))
                _window.Resources["WorkspaceStripeBrush"] = stripe;
        }
        else
        {
            _window.Resources.Remove("WorkspaceSidebarRefraction");
            _window.Resources.Remove("WorkspaceSidebarRefractionHeight");
            _window.Resources.Remove("WorkspaceTabBrush");
            _window.Resources.Remove("WorkspaceStripeBrush");
        }
        UpdateChrome();
        ScheduleRender();
    }

    private void UpdateChrome()
    {
        if (!IsActive)
        {
            _window.Classes.Remove("background-themed");
            foreach (var key in ChromeResources) _window.Resources.Remove(key);
            foreach (var key in _surfaceResources) _window.Resources.Remove(key);
            _surfaceResources.Clear();
            return;
        }
        var colors = Frame!.TitleColors;
        _window.Resources["TitleBarBackgroundBrush"] = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            GradientStops = new GradientStops { new(colors[0], 0), new(colors[1], 0.5), new(colors[2], 1) }
        };
        var dark = _window.ActualThemeVariant == ThemeVariant.Dark;
        // Keep preset text readable at the default image strength; custom photo colors remain unchanged.
        if (_service.Options.PresetId.Length > 0)
        {
            SetBrush("TextSecondaryBrush", dark ? "#E5EAF0" : "#303946");
            SetBrush("TextMutedBrush", dark ? "#E5EAF0" : "#303946");
            SetBrush("TextSubtleBrush", dark ? "#E5EAF0" : "#303946");
        }
        else
        {
            _window.Resources.Remove("TextSecondaryBrush");
            _window.Resources.Remove("TextMutedBrush");
            _window.Resources.Remove("TextSubtleBrush");
        }
        SetBrush("ViewModeToggleBg", dark ? "#281B1D21" : "#28FFFFFF");
        SetBrush("ViewModeToggleHoverBg", dark ? "#28FFFFFF" : "#52FFFFFF");
        SetBrush("ViewModeToggleSelectedBg", dark ? "#42FFFFFF" : "#7AFFFFFF");
        SetBrush("WorkspacePreviewBrush", dark ? "#281B1D21" : "#28FFFFFF");
        SetBrush("WorkspaceControlHoverBrush", dark ? "#28FFFFFF" : "#3DFFFFFF");
        SetBrush("WorkspaceControlPressedBrush", dark ? "#38FFFFFF" : "#66FFFFFF");
        SetBrush("WorkspaceControlSelectedBrush", dark ? "#40689AE6" : "#3093B9F4");
        SetBrush("WorkspaceControlBorderBrush", dark ? "#24FFFFFF" : "#18000000");
        SetBrush("WorkspaceDividerBrush", dark ? "#24FFFFFF" : "#18000000");
        SetBrush("WorkspaceInfoTagBrush", dark ? "#281B1D21" : "#30FFFFFF");
        UpdateControlSurfaces(dark);
        var tint = colors[1];
        var badge = Color.FromArgb(220, (byte)(tint.R * 0.3), (byte)(tint.G * 0.3), (byte)(tint.B * 0.3));
        _window.Resources["WorkspacePreviewBadgeBrush"] = new SolidColorBrush(badge);
        _window.Resources["WorkspacePreviewActionBrush"] = new SolidColorBrush(Color.FromArgb(235, badge.R, badge.G, badge.B));
        _window.Classes.Add("background-themed");

        void SetBrush(string key, string color) => _window.Resources[key] = new SolidColorBrush(Color.Parse(color));
    }

    internal void RestoreAppearanceResources()
    {
        if (IsActive) UpdateChrome();
    }

    private void ScheduleRender()
    {
        _renderCancellation?.Cancel();
        if (_disposed) return;
        if (!_service.IsActive)
        {
            var old = Frame; Frame = null;
            FrameChanged?.Invoke(); old?.Dispose();
            return;
        }
        var size = _window.ClientSize;
        // Before Show, ClientSize may still reflect the platform default rather than placement.
        if (!_window.IsVisible && double.IsFinite(_window.Width) && double.IsFinite(_window.Height))
            size = new Size(_window.Width, _window.Height);
        if (size.Width <= 0 || size.Height <= 0) return;
        var cancellation = new CancellationTokenSource();
        _renderCancellation = cancellation;
        var scaling = _window.RenderScaling > 0 ? _window.RenderScaling : 1;
        _renderTask = RenderAsync(size, _window.ActualThemeVariant == ThemeVariant.Dark, scaling, cancellation);
    }

    private async Task RenderAsync(Size size, bool dark, double scaling, CancellationTokenSource cancellation)
    {
        BackgroundThemeComposition? frame = null;
        var token = cancellation.Token;
        try
        {
            // Coalesce slider/resize bursts. Every surface keeps using the same prior frame meanwhile.
            if (Frame != null) await Task.Delay(60, token);
            using var artwork = _service.AcquireArtwork();
            if (artwork == null) return;
            var options = _service.Options;
            frame = await Task.Run(async () =>
            {
                await RenderSlots.WaitAsync(token).ConfigureAwait(false);
                try { return BackgroundThemeComposition.Create(artwork.Texture, options, size, dark, scaling, token); }
                finally { RenderSlots.Release(); }
            }, token);
            token.ThrowIfCancellationRequested();
            if (_disposed) return;
            var previous = Frame;
            Frame = frame; frame = null;
            UpdateChrome();
            FrameChanged?.Invoke();
            previous?.Dispose();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            System.Diagnostics.Debug.WriteLine($"Background composition failed: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
            if (ReferenceEquals(_renderCancellation, cancellation)) _renderCancellation = null;
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _renderCancellation?.Cancel();
        _service.Changed -= Update;
        _window.ActualThemeVariantChanged -= OnThemeChanged;
        _window.Closed -= OnClosed;
        _window.SizeChanged -= OnSizeChanged;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        var old = Frame; Frame = null;
        FrameChanged?.Invoke(); FrameChanged = null; old?.Dispose();
        _window.ClearValue(ServiceProperty);
        _window.ClearValue(ControllerProperty);
    }
}
