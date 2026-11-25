#import "../../Platforms/MacOS/WindowShadow.mm"
#import "../../Platforms/MacOS/MemoryDiagnostics.mm"
#include <cassert>
#include <cstdio>

@interface MERedrawProbe : MEWindowShadowView
@property(nonatomic) NSUInteger invalidations;
@end
@implementation MERedrawProbe
- (void)setNeedsDisplay:(BOOL)value {
    if (value) self.invalidations++;
    [super setNeedsDisplay:value];
}
@end

static void Pump() {
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:0.05];
    while ([deadline timeIntervalSinceNow] > 0)
        [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:deadline];
}

static void CheckReadOnlyRenderSnapshot() {
    NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 600, 400)
        styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
    window.releasedWhenClosed = NO;
    window.title = @"private-window-name";
    NSView *view = window.contentView;
    view.wantsLayer = YES;
    CAMetalLayer *layer = [CAMetalLayer new];
    view.layer = layer;
    // Deliberately stale drawable: reading diagnostics must preserve it for investigation.
    layer.drawableSize = CGSizeMake(123, 45);
    uint8_t buffer[4096];
    int length = me_window_render_snapshot((__bridge void *)view, buffer, sizeof(buffer));
    assert(length > 0 && length < sizeof(buffer));
    NSData *data = [NSData dataWithBytes:buffer length:length];
    NSDictionary *snapshot = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    assert([snapshot[@"layerKind"] isEqual:@"Metal"]);
    assert(([snapshot[@"drawableSize"] isEqual:@[@123, @45]]));
    assert(CGSizeEqualToSize(layer.drawableSize, CGSizeMake(123, 45)));
    assert(([snapshot[@"viewSize"] isEqual:@[@600, @400]]));
    assert(![[[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] containsString:window.title]);
    uint8_t small[] = {42, 43};
    assert(me_window_render_snapshot((__bridge void *)view, small, 1) == -1);
    assert(small[0] == 42 && small[1] == 43);
    assert(me_window_render_snapshot(nullptr, buffer, sizeof(buffer)) == 0);
    [window close];
}

@interface MEScaleWindow : NSWindow
@property(nonatomic) CGFloat testScale;
@end
@implementation MEScaleWindow
- (CGFloat)backingScaleFactor { return self.testScale ?: 1; }
@end

static void CheckRenderLayerReplacementAndBackingChanges() {
    __weak NSView *released;
    @autoreleasepool {
        MEScaleWindow *window = [[MEScaleWindow alloc] initWithContentRect:NSMakeRect(0, 0, 600, 400)
            styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
        window.releasedWhenClosed = NO;
        window.testScale = 2;
        NSView *view = window.contentView;
        view.wantsLayer = YES;
        me_window_chrome_prepare((__bridge void *)view);
        assert(!view.layer.backgroundColor || CGColorGetAlpha(view.layer.backgroundColor) == 0);
        me_window_background_update((__bridge void *)view, 1, 1, 1, 1, 14);

        // Avalonia installs its actual render target after the first window preparation.
        CAMetalLayer *metal = [CAMetalLayer new];
        metal.drawableSize = CGSizeMake(1200, 800);
        view.layer = metal;
        assert(CGColorGetAlpha(metal.backgroundColor) == 1);
        assert(metal.cornerRadius == 14 && metal.masksToBounds);
        assert(![metal.contentsGravity isEqualToString:kCAGravityResize]);
        assert(metal.contentsScale == 2);
        assert(![metal actionForKey:@"bounds"] && ![metal actionForKey:@"contents"]);
        // Changing frame geometry must not stretch an outstanding drawable or resize it here.
        view.frame = NSMakeRect(0, 0, 900, 450);
        assert(CGSizeEqualToSize(metal.drawableSize, CGSizeMake(1200, 800)));
        assert(![metal.contentsGravity isEqualToString:kCAGravityResize]);
        assert(!metal.presentsWithTransaction);
        window.testScale = 1;
        [NSNotificationCenter.defaultCenter postNotificationName:NSWindowDidChangeBackingPropertiesNotification object:window];
        assert(metal.contentsScale == 1);
        CALayer *software = [CALayer new];
        view.layer = software;
        assert(![software.contentsGravity isEqualToString:kCAGravityResize]);
        assert(CGColorGetAlpha(software.backgroundColor) == 1);
        assert(software.cornerRadius == 14 && software.masksToBounds);
        released = view;
        window.contentView = [NSView new];
        view = nil;
        [window close];
        window = nil;
    }
    assert(!released);
}

static NSColor *RenderedPixel(CALayer *layer, NSInteger x, NSInteger y) {
    NSInteger width = layer.bounds.size.width, height = layer.bounds.size.height;
    NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nullptr
        pixelsWide:width pixelsHigh:height bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES
        isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
    memset(bitmap.bitmapData, 0, bitmap.bytesPerRow * height);
    [layer renderInContext:[NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap].CGContext];
    return [bitmap colorAtX:x y:y];
}

static void CheckPendingFrameBackground() {
    NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(0, 0, 600, 400)
        styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
    window.releasedWhenClosed = NO;
    NSView *view = window.contentView;
    view.wantsLayer = YES;
    CALayer *layer = view.layer;
    me_window_background_update((__bridge void *)view, 1, 1, 1, 1, 14);
    // Simulate a renderer that has not delivered another frame after a large resize.
    NSBitmapImageRep *frame = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nullptr pixelsWide:600 pixelsHigh:400
        bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace
        bytesPerRow:0 bitsPerPixel:0];
    CGContextRef context = [NSGraphicsContext graphicsContextWithBitmapImageRep:frame].CGContext;
    CGContextSetRGBFillColor(context, 0.8, 0.2, 0, 1);
    CGContextFillRect(context, CGRectMake(0, 0, 600, 400));
    layer.contents = (__bridge id)frame.CGImage;
    [window setContentSize:NSMakeSize(900, 550)];
    assert(NSEqualSizes(layer.bounds.size, NSMakeSize(900, 550)));
    NSColor *exposed = RenderedPixel(layer, 850, 450);
    assert(exposed.alphaComponent == 1 && exposed.redComponent > 0.99);
    assert(RenderedPixel(layer, 0, 0).alphaComponent < 0.01);
    me_window_background_update((__bridge void *)view, 0.1, 0.12, 0.14, 1, 14);
    exposed = RenderedPixel(layer, 850, 450);
    assert(exposed.alphaComponent == 1 && fabs(exposed.redComponent - 0.1) < 0.01);
    // Maximized/fullscreen surfaces have square corners and remain filled.
    me_window_background_update((__bridge void *)view, 0.1, 0.12, 0.14, 1, 0);
    assert(RenderedPixel(layer, 0, 0).alphaComponent == 1);
    [window close];
}

int main() {
    @autoreleasepool {
        assert(getenv("MACEXPLORER_TEST_ROOT"));
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyProhibited];
        CheckRenderLayerReplacementAndBackingChanges();
        CheckPendingFrameBackground();
        CheckReadOnlyRenderSnapshot();
        NSWindow *owner = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 600, 400)
            styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskResizable
            backing:NSBackingStoreBuffered defer:NO];
        owner.releasedWhenClosed = NO;
        owner.contentView.wantsLayer = YES;
        CALayer *contentLayer = owner.contentView.layer;
        CABasicAnimation *expansion = [CABasicAnimation animationWithKeyPath:@"bounds"];
        expansion.fromValue = [NSValue valueWithRect:NSMakeRect(0, 0, 100, 100)];
        expansion.toValue = [NSValue valueWithRect:contentLayer.bounds];
        expansion.duration = 10;
        [contentLayer addAnimation:expansion forKey:@"bounds"];
        assert([contentLayer animationForKey:@"bounds"]);
        me_window_chrome_prepare((__bridge void *)owner.contentView);
        assert(!owner.isVisible && owner.animationBehavior == NSWindowAnimationBehaviorNone);
        assert(![contentLayer animationForKey:@"bounds"]);
        assert(![contentLayer actionForKey:@"bounds"]);
        assert(![contentLayer actionForKey:@"position"]);
        assert(![contentLayer actionForKey:@"contents"]);
        [owner orderFront:nil];
        me_window_shadow_update((__bridge void *)owner.contentView, 14, true);
        me_window_shadow_update((__bridge void *)owner.contentView, 14, true);
        Pump();
        MEWindowShadow *shadow = objc_getAssociatedObject(owner, &MEWindowShadowKey);
        assert(shadow && owner.childWindows.count == 1);
        assert(shadow.window.parentWindow == owner && shadow.window.isVisible);
        assert(NSEqualRects(contentLayer.bounds, owner.contentView.bounds));
        assert(shadow.window.ignoresMouseEvents && !shadow.window.canBecomeKeyWindow
            && !shadow.window.canBecomeMainWindow && !shadow.window.hasShadow);
        assert(NSEqualRects(shadow.window.frame, NSInsetRect(owner.frame, -40, -40)));

        MERedrawProbe *probe = [[MERedrawProbe alloc] initWithFrame:shadow.surface.frame];
        probe.cornerRadius = shadow.surface.cornerRadius;
        probe.shadowAlpha = shadow.surface.shadowAlpha;
        shadow.surface = probe;
        shadow.window.contentView = probe;
        Pump();
        probe.invalidations = 0;
        me_window_shadow_update((__bridge void *)owner.contentView, 14, true);
        assert(probe.invalidations == 0);
        me_window_shadow_update((__bridge void *)owner.contentView, 12, true);
        assert(probe.invalidations > 0);
        me_window_shadow_update((__bridge void *)owner.contentView, 14, true);

        NSBitmapImageRep *bitmap = [shadow.surface bitmapImageRepForCachingDisplayInRect:shadow.surface.bounds];
        [shadow.surface cacheDisplayInRect:shadow.surface.bounds toBitmapImageRep:bitmap];
        CGFloat scale = bitmap.pixelsWide / shadow.surface.bounds.size.width;
        NSInteger middle = bitmap.pixelsHigh / 2;
        assert([bitmap colorAtX:bitmap.pixelsWide / 2 y:middle].alphaComponent == 0);
        assert([bitmap colorAtX:(NSInteger)(36 * scale) y:middle].alphaComponent > 0);
        assert([bitmap colorAtX:0 y:middle].alphaComponent < 0.01);

        [owner setFrame:NSMakeRect(180, 160, 720, 500) display:YES];
        Pump();
        assert(NSEqualRects(shadow.window.frame, NSInsetRect(owner.frame, -40, -40)));
        me_window_shadow_update((__bridge void *)owner.contentView, 14, false);
        assert(owner.childWindows.count == 0 && !shadow.window.isVisible);
        me_window_shadow_update((__bridge void *)owner.contentView, 14, true);
        assert(owner.childWindows.count == 1);
        [NSNotificationCenter.defaultCenter postNotificationName:NSWindowWillEnterFullScreenNotification object:owner];
        assert(owner.childWindows.count == 0 && !shadow.window.isVisible);
        [NSNotificationCenter.defaultCenter postNotificationName:NSWindowDidExitFullScreenNotification object:owner];
        assert(owner.childWindows.count == 1 && shadow.window.isVisible);
        NSWindow *dialog = [[NSWindow alloc] initWithContentRect:NSMakeRect(260, 240, 320, 200)
            styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
        dialog.releasedWhenClosed = NO;
        me_window_chrome_prepare((__bridge void *)dialog.contentView);
        assert(dialog.animationBehavior == NSWindowAnimationBehaviorNone);
        [owner addChildWindow:dialog ordered:NSWindowAbove];
        me_window_shadow_update((__bridge void *)dialog.contentView, 14, true);
        MEWindowShadow *dialogShadow = objc_getAssociatedObject(dialog, &MEWindowShadowKey);
        assert(dialogShadow && dialogShadow != shadow);
        assert(dialogShadow.window.parentWindow == dialog && dialogShadow.window.isVisible);
        assert(shadow.window.parentWindow == owner && shadow.window.isVisible);
        [dialog orderOut:nil];
        Pump();
        assert(!dialogShadow.window.isVisible);
        [dialog orderFront:nil];
        me_window_shadow_update((__bridge void *)dialog.contentView, 14, true);
        Pump();
        assert(dialogShadow.window.isVisible);
        dialog.level = NSFloatingWindowLevel;
        me_window_shadow_update((__bridge void *)dialog.contentView, 14, true);
        assert(dialogShadow.window.level == NSFloatingWindowLevel);
        // A menu-bar panel leaves less room above it than the shadow margin.
        // The decoration must extend off-screen instead of being pushed down by AppKit.
        NSRect panelFrame = dialog.frame;
        panelFrame.origin.y = NSMaxY(dialog.screen.visibleFrame) - panelFrame.size.height - 6;
        [dialog setFrame:panelFrame display:YES];
        me_window_shadow_update((__bridge void *)dialog.contentView, 14, true);
        Pump();
        assert(NSEqualRects(dialogShadow.window.frame, NSInsetRect(dialog.frame, -40, -40)));
        [dialog orderOut:nil];
        Pump();
        [dialog orderFront:nil];
        me_window_shadow_update((__bridge void *)dialog.contentView, 14, true);
        Pump();
        assert(NSEqualRects(dialogShadow.window.frame, NSInsetRect(dialog.frame, -40, -40)));
        [owner removeChildWindow:dialog];
        [dialog close];
        assert(!dialogShadow.window.isVisible && !objc_getAssociatedObject(dialog, &MEWindowShadowKey));
        assert(owner.childWindows.count == 1 && shadow.window.isVisible);

        // Verify transform maps container corners to visual corners for both (0,0) and (0.5,0.5) anchor points.
        {
            NSRect container = NSMakeRect(0, 80, 2560, 1320);
            NSRect visual = NSMakeRect(640, 300, 1280, 800);
            BOOL flips[2] = {NO, YES};
            CGPoint anchors[2] = {CGPointMake(0, 0), CGPointMake(0.5, 0.5)};
            for (BOOL flipped : flips) {
                for (CGPoint anchor : anchors) {
                    CALayer *probeLayer = [CALayer new];
                    probeLayer.bounds = CGRectMake(0, 0, container.size.width, container.size.height);
                    probeLayer.anchorPoint = anchor;
                    probeLayer.geometryFlipped = flipped;
                    CATransform3D xf = METransformContainerToVisual(container, visual, probeLayer);
                    CGFloat ax = anchor.x * container.size.width;
                    CGFloat ay = anchor.y * container.size.height;
                    CGAffineTransform a = CATransform3DGetAffineTransform(xf);
                    auto mapPt = [&](CGFloat x, CGFloat y) {
                        CGPoint rel = CGPointMake(x - ax, y - ay);
                        CGPoint out = CGPointApplyAffineTransform(rel, a);
                        return CGPointMake(out.x + ax, out.y + ay);
                    };
                    CGFloat expectedY = flipped ? (NSMaxY(container) - NSMaxY(visual)) : (visual.origin.y - container.origin.y);
                    CGPoint p0 = mapPt(0, 0);
                    CGPoint p1 = mapPt(container.size.width, container.size.height);
                    assert(fabs(p0.x - (visual.origin.x - container.origin.x)) < 0.5);
                    assert(fabs(p0.y - expectedY) < 0.5);
                    assert(fabs(p1.x - (visual.origin.x - container.origin.x + visual.size.width)) < 0.5);
                    assert(fabs(p1.y - (expectedY + visual.size.height)) < 0.5);
                }
            }
        }

        // Verify smooth zoom/restore preserves the shadow and original normal frame, including rapid retargeting.
        struct ZoomProbe { int calls; int completed; bool lastMaximized; };
        ZoomProbe zoomProbe = {0, 0, false};
        auto onZoom = [](void *ctx, bool completed, bool maximized) {
            auto *probe = static_cast<ZoomProbe *>(ctx);
            probe->calls++;
            if (completed) {
                probe->completed++;
                probe->lastMaximized = maximized;
            }
        };
        NSRect initialFrame = owner.frame;
        NSRect visibleFrame = owner.screen.visibleFrame;
        assert(me_window_toggle_zoom((__bridge void *)owner.contentView, false, true, onZoom, &zoomProbe));
        NSDate *zoomDeadline = [NSDate dateWithTimeIntervalSinceNow:1.0];
        while (zoomProbe.completed < 1 && [zoomDeadline timeIntervalSinceNow] > 0)
            [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:zoomDeadline];
        assert(zoomProbe.completed == 1 && zoomProbe.lastMaximized);
        assert(MEFramesNearlyEqual(owner.frame, visibleFrame));
        assert(shadow.window.isVisible && MEFramesNearlyEqual(shadow.window.frame, NSInsetRect(owner.frame, -40, -40)));

        // Rapid double-toggle mid-animation must invoke both callbacks (freeing context) and settle at maximized.
        assert(me_window_toggle_zoom((__bridge void *)owner.contentView, true, true, onZoom, &zoomProbe));
        assert(me_window_toggle_zoom((__bridge void *)owner.contentView, true, true, onZoom, &zoomProbe));
        zoomDeadline = [NSDate dateWithTimeIntervalSinceNow:1.0];
        while (zoomProbe.calls < 3 && [zoomDeadline timeIntervalSinceNow] > 0)
            [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:zoomDeadline];
        assert(zoomProbe.calls == 3 && zoomProbe.completed == 2 && zoomProbe.lastMaximized);
        assert(MEFramesNearlyEqual(owner.frame, visibleFrame));

        // Restore cleanly back to the saved normal frame.
        assert(me_window_toggle_zoom((__bridge void *)owner.contentView, true, true, onZoom, &zoomProbe));
        zoomDeadline = [NSDate dateWithTimeIntervalSinceNow:1.0];
        while (zoomProbe.completed < 3 && [zoomDeadline timeIntervalSinceNow] > 0)
            [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:zoomDeadline];
        assert(zoomProbe.completed == 3 && !zoomProbe.lastMaximized);
        assert(MEFramesNearlyEqual(owner.frame, initialFrame));
        assert(shadow.window.isVisible && MEFramesNearlyEqual(shadow.window.frame, NSInsetRect(owner.frame, -40, -40)));

        [owner close];
        assert(owner.childWindows.count == 0 && !shadow.window.isVisible);
        assert(!objc_getAssociatedObject(owner, &MEWindowShadowKey));
        puts("Window shadow native checks passed");
    }
}
