using Avalonia;
using MacExplorer.Services;
using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Automation;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace MacExplorer.Platforms.MacOS;

/// <summary>Releases Avalonia 12's native accessibility cycle when a view is permanently removed.</summary>
// Version-specific bridge: run MemoryBench, including native AX traversal, when upgrading Avalonia.
internal static class MacAutomationLifetime
{
    private const string WrapperType = "Avalonia.Native.AvnAutomationPeer, Avalonia.Native";
    private const string NodeType = "Avalonia.Native.Interop.IAvnAutomationNode, Avalonia.Native";
    private const string PeerType = "Avalonia.Automation.Peers.AutomationPeer, Avalonia.Controls";
    private const string WrapperTableType =
        "System.Runtime.CompilerServices.ConditionalWeakTable`2[[" + PeerType + "],[" + WrapperType + "]]";
    private const string WrapperEntryType =
        "System.Collections.Generic.KeyValuePair`2[[" + PeerType + "],[" + WrapperType + "]]";
    private const string EmbeddablePeerType = "Avalonia.Controls.Automation.Peers.EmbeddableControlRootAutomationPeer, Avalonia.Controls";

    private static readonly bool Supported = OperatingSystem.IsMacOS()
        && typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly.GetName().Version == new Version(12, 0, 4, 0);
    private static readonly AutomationPeer DetachedPeer = new DisconnectedPeer();

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_inner")]
    private static extern ref AutomationPeer GetInner([UnsafeAccessorType(WrapperType)] object wrapper);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Node")]
    [return: UnsafeAccessorType(NodeType)]
    private static extern object? GetNode([UnsafeAccessorType(WrapperType)] object wrapper);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Node")]
    private static extern void SetNode([UnsafeAccessorType(WrapperType)] object wrapper,
        [UnsafeAccessorType(NodeType)] object? node);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "Dispose")]
    private static extern void DisposeNode([UnsafeAccessorType(NodeType)] object node);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "ChildrenChanged")]
    private static extern ref EventHandler? ChildrenChangedHandlers(AutomationPeer peer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "PropertyChanged")]
    private static extern ref EventHandler<AutomationPropertyChangedEventArgs>? PropertyChangedHandlers(AutomationPeer peer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "FocusChanged")]
    private static extern ref EventHandler? FocusChangedHandlers(WindowBaseAutomationPeer peer);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "FocusChanged")]
    private static extern ref EventHandler? EmbeddableFocusChangedHandlers([UnsafeAccessorType(EmbeddablePeerType)] object peer);

    // The native wrapper is internal. The constant closed generic names give the
    // trimmer and AOT compiler the concrete entry getters and Remove overload.
    private static class WrapperTable
    {
        internal static readonly IEnumerable Entries = GetEntries();
        internal static readonly MethodInfo Remove = Type.GetType(WrapperTableType, throwOnError: true)!
            .GetMethod(nameof(ConditionalWeakTable<AutomationPeer, object>.Remove), [typeof(AutomationPeer)])!;
        internal static readonly (PropertyInfo Key, PropertyInfo Value) Entry = GetEntryProperties();

        [DynamicDependency("s_wrappers", "Avalonia.Native.AvnAutomationPeer", "Avalonia.Native")]
        private static IEnumerable GetEntries() => (IEnumerable)Type.GetType(WrapperType, throwOnError: true)!
            .GetField("s_wrappers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;

        private static (PropertyInfo, PropertyInfo) GetEntryProperties()
        {
            var type = Type.GetType(WrapperEntryType, throwOnError: true)!;
            return (type.GetProperty(nameof(KeyValuePair<AutomationPeer, object>.Key))!,
                type.GetProperty(nameof(KeyValuePair<AutomationPeer, object>.Value))!);
        }
    }

    private sealed class DisconnectedPeer() : ControlAutomationPeer(new Control { IsVisible = false, IsEnabled = false })
    {
        protected override bool IsControlElementCore() => false;
        protected override bool IsContentElementCore() => false;
        protected override string GetNameCore() => string.Empty;
        protected override string GetClassNameCore() => string.Empty;
    }

    [DllImport("libMacExplorerNativeDrag", EntryPoint = "me_automation_fix_children")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool FixNativeChildren();

    internal static bool NativeChildrenFixActive { get; private set; }

    public static void Prepare()
    {
        if (Supported)
            NativeChildrenFixActive = FixNativeChildren();
    }

    public static void Release(Control root) => Capture(root)?.Invoke();

    public static Action? Capture(Control? root, object? releasedContext = null) => CaptureCore(root, releasedContext, false);

    public static void ReleaseDetached() => CaptureCore(null, null, true)?.Invoke();

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetInputRoot")]
    private static extern void SetInputRoot(ITopLevelImpl platform, IInputRoot? root);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Input")]
    private static extern void SetInput(ITopLevelImpl platform, Action<RawInputEventArgs>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Paint")]
    private static extern void SetPaint(ITopLevelImpl platform, Action<Rect>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Resized")]
    private static extern void SetResized(ITopLevelImpl platform, Action<Size, WindowResizeReason>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ScalingChanged")]
    private static extern void SetScalingChanged(ITopLevelImpl platform, Action<double>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_TransparencyLevelChanged")]
    private static extern void SetTransparencyLevelChanged(ITopLevelImpl platform, Action<WindowTransparencyLevel>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Closed")]
    private static extern void SetClosed(ITopLevelImpl platform, Action? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_LostFocus")]
    private static extern void SetLostFocus(ITopLevelImpl platform, Action? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Deactivated")]
    private static extern void SetDeactivated(IWindowBaseImpl platform, Action? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Activated")]
    private static extern void SetActivated(IWindowBaseImpl platform, Action? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_PositionChanged")]
    private static extern void SetPositionChanged(IWindowBaseImpl platform, Action<PixelPoint>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_WindowStateChanged")]
    private static extern void SetWindowStateChanged(IWindowImpl platform, Action<WindowState>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_ExtendClientAreaToDecorationsChanged")]
    private static extern void SetExtendClientAreaToDecorationsChanged(IWindowImpl platform, Action<bool>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_Closing")]
    private static extern void SetClosing(IWindowImpl platform, Func<WindowCloseReason, bool>? callback);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "set_GotInputWhenDisabled")]
    private static extern void SetGotInputWhenDisabled(IWindowImpl platform, Action? callback);

    public static void ReleaseClosedCallbacks(ITopLevelImpl? platform)
    {
        if (platform == null || platform.GetType().Assembly != typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly) return;
        // Native WindowEvents can outlive the NSWindow and its managed window.
        SetInputRoot(platform, null);
        SetInput(platform, null);
        SetPaint(platform, null);
        SetResized(platform, null);
        SetScalingChanged(platform, null);
        SetTransparencyLevelChanged(platform, null);
        SetClosed(platform, null);
        SetLostFocus(platform, null);
        if (platform is IWindowBaseImpl windowBase)
        {
            SetDeactivated(windowBase, null);
            SetActivated(windowBase, null);
            SetPositionChanged(windowBase, null);
        }
        if (platform is IWindowImpl window)
        {
            SetWindowStateChanged(window, null);
            SetExtendClientAreaToDecorationsChanged(window, null);
            SetClosing(window, null);
            SetGotInputWhenDisabled(window, null);
        }
    }

    private static Action? CaptureCore(Control? root, object? releasedContext, bool detachedOnly)
    {
        if (!Supported) return null;

        var controls = root == null ? [] : root.GetVisualDescendants().OfType<Control>().Append(root).ToHashSet();
        var owned = new List<(AutomationPeer Peer, object Wrapper)>();
        foreach (var pair in WrapperTable.Entries)
        {
            var peer = (AutomationPeer)WrapperTable.Entry.Key.GetValue(pair)!;
            if (detachedOnly)
            {
                if (peer is ControlAutomationPeer detached && !detached.Owner.IsAttachedToVisualTree()
                    && (detached.Owner is not TopLevel top || top.TryGetPlatformHandle() == null))
                    owned.Add((peer, WrapperTable.Entry.Value.GetValue(pair)!));
                continue;
            }
            for (var current = peer; current != null; current = current.GetParent())
            {
                if (current is not ControlAutomationPeer control || !(IsOwned(control.Owner, controls)
                    || releasedContext != null && ReferenceEquals(control.Owner.DataContext, releasedContext))) continue;
                owned.Add((peer, WrapperTable.Entry.Value.GetValue(pair)!));
                break;
            }
        }

        return () =>
        {
            foreach (var (peer, wrapper) in owned)
            {
                RemoveCallbacks(peer, wrapper);
                WrapperTable.Remove.Invoke(WrapperTable.Entries, [peer]);
                // macOS can retain an AX element after removal. Its callback must no longer
                // own the old control, while stale accessibility queries remain harmless.
                GetInner(wrapper) = DetachedPeer;
                if (GetNode(wrapper) is not { } node) continue;
                // AvnAutomationNode owns its NSAccessibility element, which owns this callback.
                // Calling the native Dispose breaks that cycle. Clear the field first so the
                // wrapper finalizer cannot call back into the released node a second time.
                SetNode(wrapper, null);
                DisposeNode(node);
            }
            if (owned.Count > 0) Services.MemoryDiagnostics.Record("lifetime.native_nodes_released", new NativeNodesReleased(owned.Count,
                                                                                                         detachedOnly));
            owned.Clear();
        };
    }

    private static void RemoveCallbacks(AutomationPeer peer, object wrapper)
    {
        RemoveWrapperHandlers(ref ChildrenChangedHandlers(peer), wrapper);
        RemoveWrapperHandlers(ref PropertyChangedHandlers(peer), wrapper);
        if (peer is WindowBaseAutomationPeer window)
            RemoveWrapperHandlers(ref FocusChangedHandlers(window), wrapper);
        else if (peer.GetType().FullName == "Avalonia.Controls.Automation.Peers.EmbeddableControlRootAutomationPeer")
            RemoveWrapperHandlers(ref EmbeddableFocusChangedHandlers(peer), wrapper);
    }

    private static void RemoveWrapperHandlers<T>(ref T? handlers, object wrapper) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
            if (ReferenceEquals(handler.Target, wrapper)) handlers = (T?)Delegate.Remove(handlers, handler);
    }

    private static bool IsOwned(Control owner, HashSet<Control> controls)
    {
        for (Avalonia.Visual? visual = owner; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && controls.Contains(control)) return true;
            if (visual is Control { TemplatedParent: Control parent } && controls.Contains(parent)) return true;
            // Carets and focus outlines live in the window's adorner layer, outside the view.
            if (AdornerLayer.GetAdornedElement(visual) is Control adorned && controls.Contains(adorned)) return true;
        }
        return false;
    }
}
