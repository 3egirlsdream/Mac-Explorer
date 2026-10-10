using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Controls;

/// <summary>Embeds a PDFKit or Quick Look document without changing application focus.</summary>
internal sealed class DocumentPreviewHost : NativeControlHost, IDisposable
{
    private const string Library = "libMacExplorerNativeDrag.dylib";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void KeyCallback(ushort keyCode);
    private readonly KeyCallback _keyCallback;
    private IntPtr _view;
    private bool _attached;
    private bool _disposed;

    internal DocumentPreviewHost(Action<Key> onKey)
    {
        Focusable = false;
        ActualThemeVariantChanged += (_, _) => ApplyAppearance();
        _keyCallback = code => Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed && _view != IntPtr.Zero && IsEffectivelyVisible)
                onKey(MacKeyboardMonitor.FromKeyCode(code));
        });
    }

    internal int Load(string path, bool pdf, bool navigationKeys)
    {
        _view = me_document_preview_create(_keyCallback, navigationKeys);
        if (_view == IntPtr.Zero) return 0;
        return me_document_preview_load(_view, path, pdf);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        _attached = true;
        return new PlatformHandle(_view, "NSView");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (_disposed || _view == IntPtr.Zero) return;
        var radius = this.TryFindResource("RadiusLg", out var value) && value is CornerRadius corners
            ? corners.TopLeft : 10;
        me_document_preview_appearance(_view, ActualThemeVariant == ThemeVariant.Dark, radius);
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        ReleaseView();
        _attached = false;
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_view == IntPtr.Zero) return;
        if (TopLevel.GetTopLevel(this)?.TryGetPlatformHandle() is IMacOSTopLevelPlatformHandle root)
            me_document_preview_restore_focus(_view, root.NSView);
        me_document_preview_clear(_view);
        // Avalonia detaches immediately but destroys its attachment on a later UI turn.
        // Keep the NSView alive until DestroyNativeControlCore releases that attachment.
        if (!_attached) ReleaseView();
    }

    private void ReleaseView()
    {
        if (_view == IntPtr.Zero) return;
        me_document_preview_destroy(_view);
        _view = IntPtr.Zero;
        GC.KeepAlive(_keyCallback);
    }

    [DllImport(Library)] private static extern IntPtr me_document_preview_create(KeyCallback callback, [MarshalAs(UnmanagedType.I1)] bool navigationKeys);
    [DllImport(Library)] private static extern int me_document_preview_load(IntPtr view, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool pdf);
    [DllImport(Library)] private static extern void me_document_preview_restore_focus(IntPtr view, IntPtr root);
    [DllImport(Library)] private static extern void me_document_preview_appearance(IntPtr view, [MarshalAs(UnmanagedType.I1)] bool dark, double radius);
    [DllImport(Library)] private static extern void me_document_preview_clear(IntPtr view);
    [DllImport(Library)] private static extern void me_document_preview_destroy(IntPtr view);
}
