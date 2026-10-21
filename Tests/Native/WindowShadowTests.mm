#import "../../Platforms/MacOS/WindowShadow.mm"
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

int main() {
    @autoreleasepool {
        assert(getenv("MACEXPLORER_TEST_ROOT"));
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyProhibited];
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
        [owner close];
        assert(owner.childWindows.count == 0 && !shadow.window.isVisible);
        assert(!objc_getAssociatedObject(owner, &MEWindowShadowKey));
        puts("Window shadow native checks passed");
    }
}
