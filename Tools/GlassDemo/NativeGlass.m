#import <AppKit/AppKit.h>

// Experimental native underlay. No production window or global appearance is modified.
@interface DemoBackdrop : NSView
@property NSInteger mode;
@property BOOL dark;
@property CGFloat phase;
@property NSImage *artwork;
@end
@implementation DemoBackdrop
- (BOOL)isFlipped { return YES; }
- (NSView *)hitTest:(NSPoint)point { return nil; }
- (void)drawRect:(NSRect)dirty {
    if (self.mode == 2) return;
    if (self.mode >= 4 && self.artwork) {
        [self.artwork drawInRect:self.bounds fromRect:NSZeroRect operation:NSCompositingOperationCopy
            fraction:1 respectFlipped:YES hints:nil];
        return;
    }
    [(self.dark ? [NSColor colorWithSRGBRed:0.10 green:0.12 blue:0.15 alpha:1]
               : [NSColor colorWithSRGBRed:0.94 green:0.95 blue:0.97 alpha:1]) setFill];
    NSRectFill(self.bounds);
    if (self.mode == 0) return;
    NSArray<NSColor *> *colors = @[
        [NSColor colorWithSRGBRed:0.37 green:0.66 blue:0.73 alpha:1],
        [NSColor colorWithSRGBRed:0.93 green:0.67 blue:0.44 alpha:1],
        [NSColor colorWithSRGBRed:0.56 green:0.57 blue:0.77 alpha:1]];
    for (NSInteger i = -2; i < 12; i++) {
        [colors[(i + 12) % 3] setFill];
        NSRectFill(NSMakeRect(i * 90 + self.phase, 0, 42, self.bounds.size.height));
    }
    [[NSColor colorWithWhite:self.dark ? 1 : 0 alpha:0.12] setStroke];
    NSBezierPath *lines = [NSBezierPath bezierPath];
    for (CGFloat y = 24; y < self.bounds.size.height; y += 36) {
        [lines moveToPoint:NSMakePoint(0, y)];
        [lines lineToPoint:NSMakePoint(self.bounds.size.width, y)];
    }
    [lines stroke];
}
@end

@interface DemoNativeState : NSObject
@property DemoBackdrop *backdrop;
@property NSView *glass;
@property NSVisualEffectView *material;
@property NSView *darkPanel;
@property NSWindow *testWindow;
@end
@implementation DemoNativeState
@end

@interface DemoTestWindow : NSWindow
@end
@implementation DemoTestWindow
- (BOOL)canBecomeKeyWindow { return NO; }
- (BOOL)canBecomeMainWindow { return NO; }
@end

void *glass_demo_create(void *handle) {
    NSView *view = (__bridge NSView *)handle;
    NSView *host = view.window.contentView;
    if (!host) return NULL;
    view.window.opaque = NO;
    view.window.backgroundColor = NSColor.clearColor;
    DemoNativeState *state = [DemoNativeState new];
    state.backdrop = [DemoBackdrop new];
    if (@available(macOS 26.0, *)) {
        NSGlassEffectView *glass = [NSGlassEffectView new];
        glass.style = NSGlassEffectViewStyleClear;
        glass.cornerRadius = 22;
        glass.tintColor = nil;
        glass.contentView = [NSView new];
        state.glass = glass;
    } else {
        // A visibly labelled fallback: this does not claim to be Liquid Glass.
        NSVisualEffectView *glass = [NSVisualEffectView new];
        glass.material = NSVisualEffectMaterialSidebar;
        glass.blendingMode = NSVisualEffectBlendingModeBehindWindow;
        glass.state = NSVisualEffectStateActive;
        glass.wantsLayer = YES;
        glass.layer.cornerRadius = 22;
        glass.layer.masksToBounds = YES;
        state.glass = glass;
    }
    state.material = [NSVisualEffectView new];
    state.material.material = NSVisualEffectMaterialHUDWindow;
    state.material.blendingMode = NSVisualEffectBlendingModeBehindWindow;
    state.material.state = NSVisualEffectStateActive;
    state.material.wantsLayer = YES;
    state.material.layer.cornerRadius = 22;
    state.material.layer.masksToBounds = YES;
    state.material.hidden = YES;
    [state.backdrop addSubview:state.material];
    [state.backdrop addSubview:state.glass];
    state.darkPanel = [NSView new];
    state.darkPanel.wantsLayer = YES;
    state.darkPanel.layer.cornerRadius = 22;
    state.darkPanel.layer.backgroundColor = [NSColor colorWithSRGBRed:48.0/255 green:48.0/255 blue:47.0/255 alpha:0.85].CGColor;
    state.darkPanel.hidden = YES;
    [state.backdrop addSubview:state.darkPanel];
    [host addSubview:state.backdrop positioned:NSWindowBelow relativeTo:nil];
    return (__bridge_retained void *)state;
}

void glass_demo_update(void *token, void *handle, double x, double y, double w, double h,
                       double cx, double cy, double cw, double ch,
                       int mode, int dark, int regular, int enabled, int material, double phase) {
    DemoNativeState *state = (__bridge DemoNativeState *)token;
    NSView *view = (__bridge NSView *)handle;
    NSView *host = view.window.contentView;
    // Avalonia positions are DIPs from the top of its NSView; convert to the host's coordinates.
    NSRect rect = NSMakeRect(x, view.isFlipped ? y : view.bounds.size.height - y - h, w, h);
    state.backdrop.frame = [view convertRect:rect toView:host];
    state.backdrop.mode = (mode == 2 || mode == 3) ? 2 : mode;
    state.backdrop.dark = dark;
    state.backdrop.phase = phase;
    state.backdrop.appearance = [NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua];
    state.glass.frame = NSMakeRect(cx, cy, cw, ch);
    state.glass.hidden = !enabled || dark;
    state.darkPanel.frame = state.glass.frame;
    state.darkPanel.hidden = !enabled || !dark;
    state.material.frame = state.glass.frame;
    state.material.hidden = !(material && enabled && (mode == 2 || mode == 3));
    if (@available(macOS 26.0, *)) {
        ((NSGlassEffectView *)state.glass).style = regular ? NSGlassEffectViewStyleRegular : NSGlassEffectViewStyleClear;
    }
    [state.backdrop setNeedsDisplay:YES];
    if (mode == 3) {
        if (!state.testWindow) {
            state.testWindow = [[DemoTestWindow alloc] initWithContentRect:view.window.frame
                styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
            state.testWindow.releasedWhenClosed = NO;
            state.testWindow.title = @"Glass Demo controlled backdrop";
            state.testWindow.ignoresMouseEvents = YES;
            state.testWindow.contentView = [DemoBackdrop new];
        }
        DemoBackdrop *test = (DemoBackdrop *)state.testWindow.contentView;
        test.mode = 1; test.dark = dark; test.phase = phase;
        [test setNeedsDisplay:YES];
        [state.testWindow orderWindow:NSWindowBelow relativeTo:view.window.windowNumber];
    } else {
        [state.testWindow orderOut:nil];
    }
}

void glass_demo_set_artwork(void *token, const void *bytes, int length) {
    DemoNativeState *state = (__bridge DemoNativeState *)token;
    state.backdrop.artwork = [[NSImage alloc] initWithData:[NSData dataWithBytes:bytes length:length]];
    [state.backdrop setNeedsDisplay:YES];
}

void glass_demo_destroy(void *token) {
    DemoNativeState *state = (__bridge_transfer DemoNativeState *)token;
    [state.testWindow close];
    [state.backdrop removeFromSuperview];
}
