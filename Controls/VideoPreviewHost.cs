using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;

namespace MacExplorer.Controls;

internal sealed class VideoPreviewHost : NativeControlHost, IDisposable
{
    private const string Library = "libMacExplorerNativeDrag.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void KeyCallback(ushort code);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void StateCallback(int state);
    private readonly KeyCallback _keyCallback;
    private readonly StateCallback _stateCallback;
    private readonly MemoryDiagnosticLifetime? _memoryLifetime;
    private Control[] _ancestors = [];
    private IntPtr _view;
    private bool _attached;
    private bool _disposed;

    internal VideoPreviewHost(Action<Key> onKey, Action<bool> onLoaded)
    {
        _memoryLifetime = MemoryDiagnostics.Track(this, DiagnosticObjectKind.VideoPreview);
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();
        _keyCallback = code => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && IsEffectivelyVisible) onKey(MacKeyboardMonitor.FromKeyCode(code));
        });
        _stateCallback = state => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) onLoaded(state == 1);
        });
    }

    internal void Load(string path)
    {
        _view = me_video_preview_create(_keyCallback, _stateCallback);
        if (_view == IntPtr.Zero) throw new InvalidOperationException("Unable to create the native video view.");
        me_video_preview_load(_view, path);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _attached = true;
        return new PlatformHandle(_view, "NSView");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _memoryLifetime?.Link((TopLevel.GetTopLevel(this) as AppWindow)?.DiagnosticId ?? 0);
        _memoryLifetime?.SetState(DiagnosticObjectState.Attached);
        base.OnAttachedToVisualTree(e);
        _ancestors = this.GetVisualAncestors().OfType<Control>().ToArray();
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged += OnAncestorChanged;
        ApplyAppearance();
        UpdateActive();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty) UpdateActive();
    }

    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty || e.Property == Window.WindowStateProperty) UpdateActive();
    }

    private void UpdateActive()
    {
        if (_disposed || _view == IntPtr.Zero) return;
        var window = TopLevel.GetTopLevel(this) as Window;
        me_video_preview_active(_view, IsVisible && _ancestors.All(ancestor => ancestor.IsVisible) && window is { IsVisible: true }
            && window.WindowState != WindowState.Minimized);
    }

    private void ApplyAppearance()
    {
        if (_disposed || _view == IntPtr.Zero) return;
        var radius = this.TryFindResource("RadiusLg", out var value) && value is CornerRadius corners ? corners.TopLeft : 10;
        me_video_preview_appearance(_view, ActualThemeVariant == ThemeVariant.Dark, radius);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleaseView();
        _attached = false;
        Dispatcher.UIThread.Post(() => MacAutomationLifetime.Release(this), DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
        _ancestors = [];
        _memoryLifetime?.SetState(DiagnosticObjectState.Disposed);
        if (_view == IntPtr.Zero) return;
        if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle root)
            me_video_preview_restore_focus(_view, root.NSView);
        me_video_preview_clear(_view);
        // Keep the NSView alive until Avalonia releases its native attachment.
        if (!_attached) ReleaseView();
    }

    private void ReleaseView()
    {
        if (_view == IntPtr.Zero) return;
        me_video_preview_destroy(_view);
        _view = IntPtr.Zero;
        GC.KeepAlive(_keyCallback);
        GC.KeepAlive(_stateCallback);
    }

    [DllImport(Library)] private static extern IntPtr me_video_preview_create(KeyCallback key, StateCallback state);
    [DllImport(Library)] private static extern void me_video_preview_load(IntPtr view, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern void me_video_preview_active(IntPtr view, [MarshalAs(UnmanagedType.I1)] bool active);
    [DllImport(Library)] private static extern void me_video_preview_appearance(IntPtr view, [MarshalAs(UnmanagedType.I1)] bool dark, double radius);
    [DllImport(Library)] private static extern void me_video_preview_restore_focus(IntPtr view, IntPtr root);
    [DllImport(Library)] private static extern void me_video_preview_clear(IntPtr view);
    [DllImport(Library)] private static extern void me_video_preview_destroy(IntPtr view);
}
