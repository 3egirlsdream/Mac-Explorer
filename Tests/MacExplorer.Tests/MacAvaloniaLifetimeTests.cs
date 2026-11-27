using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Platform;
using MacExplorer.Platforms.MacOS;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Memory")]
[Trait("Kind", "UI")]
public sealed class MacAvaloniaLifetimeTests
{
    [AvaloniaFact(Explicit = true)]
    public void NativeAutomationWrapperDropsClosedWindowAndDisposesItsNode()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Avalonia's macOS implementation.");
        var window = new Window();
        var peer = new WindowBaseAutomationPeer(window);
        var wrapperType = typeof(AvaloniaNativePlatformOptions).Assembly.GetType("Avalonia.Native.AvnAutomationPeer")!;
        var wrap = wrapperType.GetMethod("Wrap")!;
        var wrapper = wrap.Invoke(null, [peer])!;
        var nodeProperty = wrapperType.GetProperty("Node")!;
        var node = (AutomationNodeProxy)DispatchProxy.Create(nodeProperty.PropertyType, typeof(AutomationNodeProxy));
        wrapperType.GetMethod("SetNode")!.Invoke(wrapper, [node]);

        EventHandler keep = (_, _) => { };
        peer.ChildrenChanged += keep;

        MacAutomationLifetime.Release(window);

        Assert.Equal(1, node.DisposeCalls);
        Assert.Null(nodeProperty.GetValue(wrapper));
        Assert.NotSame(peer, wrapperType.GetField("_inner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrapper));
        var childrenChanged = typeof(AutomationPeer).GetField("ChildrenChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Equal(keep, childrenChanged.GetValue(peer));

        var replacement = wrap.Invoke(null, [peer]);
        try { Assert.NotSame(wrapper, replacement); }
        finally
        {
            MacAutomationLifetime.Release(window);
            window.Close();
        }
    }

    public class AutomationNodeProxy : DispatchProxy
    {
        public int DisposeCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IDisposable.Dispose)) DisposeCalls++;
            return null;
        }
    }

    [AvaloniaFact(Explicit = true)]
    public void ClosedNativeWindowReleasesItsInputRootAndCallbacks()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires Avalonia's macOS window type.");
        var assembly = typeof(AvaloniaNativePlatformOptions).Assembly;
        var windowType = assembly.GetType("Avalonia.Native.WindowImpl")!;
        // The close callback cleanup only touches managed fields.
        var platform = (ITopLevelImpl)RuntimeHelpers.GetUninitializedObject(windowType);
        GC.SuppressFinalize(platform);
        var root = new Window();
        var inputRoot = assembly.GetType("Avalonia.Native.TopLevelImpl")!
            .GetField("_inputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!;
        try
        {
            var presentationSource = typeof(TopLevel).GetProperty("InputRoot", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(root);
            windowType.GetMethod("SetInputRoot")!.Invoke(platform, [presentationSource]);
            var closed = windowType.GetProperty("Closed")!;
            var positionChanged = windowType.GetProperty("PositionChanged")!;
            var closing = windowType.GetProperty("Closing")!;
            closed.SetValue(platform, (Action)(() => { }));
            positionChanged.SetValue(platform, (Action<PixelPoint>)(_ => { }));
            closing.SetValue(platform, (Func<WindowCloseReason, bool>)(_ => true));

            MacAutomationLifetime.ReleaseClosedCallbacks(platform);

            Assert.Null(inputRoot.GetValue(platform));
            Assert.Null(closed.GetValue(platform));
            Assert.Null(positionChanged.GetValue(platform));
            Assert.Null(closing.GetValue(platform));
        }
        finally { root.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void SystemFontDecoratorReusesGlyphForTheSameWeight()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "Requires the macOS font decorator.");
        MacFontLifetime.Prepare();
        var fonts = Assert.IsType<MacFontLifetime>(FontManager.Current.SystemFonts);

        Assert.True(fonts.TryGetGlyphTypeface("System Font", FontStyle.Normal, FontWeight.Medium, FontStretch.Normal, out var glyph));
        Assert.NotNull(glyph);
        Assert.True(fonts.TryGetGlyphTypeface(".AppleSystemUIFont", FontStyle.Normal, FontWeight.Medium, FontStretch.Normal, out var alias));
        Assert.Same(glyph, alias);
    }
}
