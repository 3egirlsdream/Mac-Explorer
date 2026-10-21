using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Avalonia.VisualTree;

namespace MacExplorer.Platforms.MacOS;

/// <summary>Releases Avalonia 12's native accessibility cycle when a view is permanently removed.</summary>
// Version-specific bridge: run MemoryBench, including native AX traversal, when upgrading Avalonia.
internal static class MacAutomationLifetime
{
    private static readonly Type? WrapperType = typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly
        .GetType("Avalonia.Native.AvnAutomationPeer");
    private static readonly FieldInfo? Wrappers = WrapperType?.GetField("s_wrappers", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly MethodInfo? RemoveWrapper = Wrappers?.FieldType.GetMethod("Remove", [typeof(AutomationPeer)]);
    private static readonly PropertyInfo? Node = WrapperType?.GetProperty("Node");
    private static readonly FieldInfo? Inner = WrapperType?.GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo? DisposeNode = Node?.PropertyType.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
    private static readonly AutomationPeer DetachedPeer = new DisconnectedPeer();

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
        if (OperatingSystem.IsMacOS()
            && typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly.GetName().Version == new Version(12, 0, 4, 0))
            NativeChildrenFixActive = FixNativeChildren();
    }

    public static void Release(Control root) => Capture(root)?.Invoke();

    public static Action? Capture(Control? root, object? releasedContext = null) => CaptureCore(root, releasedContext, false);

    public static void ReleaseDetached() => CaptureCore(null, null, true)?.Invoke();

    public static void ReleaseClosedCallbacks(ITopLevelImpl? platform)
    {
        if (platform == null || platform.GetType().Assembly != typeof(Avalonia.AvaloniaNativePlatformOptions).Assembly) return;
        // Native WindowEvents can outlive the NSWindow. Clear its closed input source and
        // callbacks after Avalonia's Closed callback has completed its own disposal.
        platform.GetType().GetMethod("SetInputRoot")!.Invoke(platform, [null]);
        foreach (var property in platform.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (property.CanWrite && typeof(Delegate).IsAssignableFrom(property.PropertyType)) property.SetValue(platform, null);
    }

    private static Action? CaptureCore(Control? root, object? releasedContext, bool detachedOnly)
    {
        if (!OperatingSystem.IsMacOS() || Wrappers?.GetValue(null) is not IEnumerable wrappers || Node == null || Inner == null || DisposeNode == null || RemoveWrapper == null)
            return null;

        var controls = root == null ? [] : root.GetVisualDescendants().OfType<Control>().Append(root).ToHashSet();
        var owned = new List<(AutomationPeer Peer, object Wrapper)>();
        foreach (var pair in wrappers)
        {
            var type = pair.GetType();
            if (type.GetProperty("Key")!.GetValue(pair) is not AutomationPeer peer) continue;
            if (detachedOnly)
            {
                if (peer is ControlAutomationPeer detached && !detached.Owner.IsAttachedToVisualTree()
                    && (detached.Owner is not TopLevel top || top.TryGetPlatformHandle() == null))
                    owned.Add((peer, type.GetProperty("Value")!.GetValue(pair)!));
                continue;
            }
            for (var current = peer; current != null; current = current.GetParent())
            {
                if (current is not ControlAutomationPeer control || !(IsOwned(control.Owner, controls)
                    || releasedContext != null && ReferenceEquals(control.Owner.DataContext, releasedContext))) continue;
                owned.Add((peer, type.GetProperty("Value")!.GetValue(pair)!));
                break;
            }
        }

        return () =>
        {
            foreach (var (peer, wrapper) in owned)
            {
                RemoveCallbacks(peer, wrapper);
                RemoveWrapper.Invoke(wrappers, [peer]);
                // macOS can retain an AX element after removal. Its callback must no longer
                // own the old control, while stale accessibility queries remain harmless.
                Inner.SetValue(wrapper, DetachedPeer);
                if (Node.GetValue(wrapper) is not { } node) continue;
                // AvnAutomationNode owns its NSAccessibility element, which owns this callback.
                // Calling the native Dispose breaks that cycle. Clear the field first so the
                // wrapper finalizer cannot call back into the released node a second time.
                Node.SetValue(wrapper, null);
                DisposeNode.Invoke(node, null);
            }
            owned.Clear();
        };
    }

    private static void RemoveCallbacks(AutomationPeer peer, object wrapper)
    {
        for (var type = peer.GetType(); type != null; type = type.BaseType)
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (!typeof(Delegate).IsAssignableFrom(field.FieldType) || field.GetValue(peer) is not Delegate handlers) continue;
            Delegate? remaining = handlers;
            foreach (var handler in handlers.GetInvocationList())
                if (ReferenceEquals(handler.Target, wrapper)) remaining = Delegate.Remove(remaining, handler);
            if (!ReferenceEquals(remaining, handlers)) field.SetValue(peer, remaining);
        }
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
