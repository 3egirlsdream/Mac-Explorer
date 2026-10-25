#import <AppKit/AppKit.h>
#import <objc/runtime.h>
#import <QuartzCore/QuartzCore.h>

static char MEPreparedContentLayerKey;
static void PrepareContentLayer(NSView *view) {
    CALayer *layer = view.layer;
    if (!layer || objc_getAssociatedObject(layer, &MEPreparedContentLayerKey)) return;
    NSMutableDictionary *actions = [layer.actions mutableCopy] ?: [NSMutableDictionary new];
    // AppKit can implicitly animate a new render surface to its final bounds after Show.
    for (NSString *key in @[@"bounds", @"position", @"transform", @"contents", @"contentsRect", @"contentsScale"]) {
        actions[key] = NSNull.null;
        [layer removeAnimationForKey:key];
    }
    layer.actions = actions;
    objc_setAssociatedObject(layer, &MEPreparedContentLayerKey, @YES, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}

extern "C" __attribute__((visibility("default")))
void me_window_chrome_prepare(void *viewPointer) {
    NSView *view = (__bridge NSView *)viewPointer;
    NSWindow *window = view.window;
    if (!window || !NSThread.isMainThread) return;
    window.animationBehavior = NSWindowAnimationBehaviorNone;
    PrepareContentLayer(view);
    if (window.contentView != view) PrepareContentLayer(window.contentView);
}

extern "C" __attribute__((visibility("default")))
void me_window_request_display(void *viewPointer) {
    @autoreleasepool {
        NSView *view = (__bridge NSView *)viewPointer;
        if (!view || !NSThread.isMainThread) return;
        NSWindow *window = view.window;
        if (window) {
            NSView *content = window.contentView;
            if (content) [content setNeedsDisplay:YES];
        }
        [view setNeedsDisplay:YES];
        if (view.layer) [view.layer setNeedsDisplay];
    }
}

static char MEWindowShadowKey;
static const CGFloat MEWindowShadowMargin = 40;

@interface MEWindowShadowView : NSView
@property(nonatomic) CGFloat cornerRadius;
@property(nonatomic) CGFloat shadowAlpha;
@end

@implementation MEWindowShadowView
- (BOOL)isOpaque { return NO; }
- (void)drawRect:(NSRect)dirtyRect {
    [NSGraphicsContext saveGraphicsState];
    [[NSColor clearColor] setFill];
    NSRectFillUsingOperation(self.bounds, NSCompositingOperationCopy);
    NSRect body = NSInsetRect(self.bounds, MEWindowShadowMargin, MEWindowShadowMargin);
    NSShadow *shadow = [NSShadow new];
    shadow.shadowColor = [NSColor colorWithWhite:0 alpha:self.shadowAlpha];
    shadow.shadowBlurRadius = 18;
    shadow.shadowOffset = NSMakeSize(0, -4);
    [shadow set];
    [[NSColor blackColor] setFill];
    [[NSBezierPath bezierPathWithRoundedRect:body xRadius:self.cornerRadius yRadius:self.cornerRadius] fill];
    [NSGraphicsContext restoreGraphicsState];

    // Erase the caster and its antialiased edge so no dark rim or interior tint remains.
    [NSGraphicsContext saveGraphicsState];
    NSGraphicsContext.currentContext.compositingOperation = NSCompositingOperationClear;
    [[NSBezierPath bezierPathWithRoundedRect:NSInsetRect(body, -0.5, -0.5)
        xRadius:self.cornerRadius + 0.5 yRadius:self.cornerRadius + 0.5] fill];
    [NSGraphicsContext restoreGraphicsState];
}
@end

@interface MEWindowShadowWindow : NSWindow
@end
@implementation MEWindowShadowWindow
- (NSRect)constrainFrameRect:(NSRect)frameRect toScreen:(NSScreen *)screen {
    // Decorative margins may cross the menu bar; clamping moves the shadow off its owner.
    return frameRect;
}
- (BOOL)canBecomeKeyWindow { return NO; }
- (BOOL)canBecomeMainWindow { return NO; }
@end

@interface MEWindowShadow : NSObject
@property(nonatomic, weak) NSWindow *owner;
@property(nonatomic, strong) MEWindowShadowWindow *window;
@property(nonatomic, strong) MEWindowShadowView *surface;
@property(nonatomic) BOOL enabled;
@property(nonatomic) BOOL transitioning;
- (instancetype)initWithOwner:(NSWindow *)owner;
- (void)update;
@end

@implementation MEWindowShadow
- (instancetype)initWithOwner:(NSWindow *)owner {
    if (!(self = [super init])) return nil;
    _owner = owner;
    _window = [[MEWindowShadowWindow alloc] initWithContentRect:NSZeroRect
        styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
    _window.releasedWhenClosed = NO;
    _window.backgroundColor = NSColor.clearColor;
    _window.opaque = NO;
    _window.hasShadow = NO;
    _window.ignoresMouseEvents = YES;
    _window.excludedFromWindowsMenu = YES;
    _window.animationBehavior = NSWindowAnimationBehaviorNone;
    // A shadow must track its owner through Spaces and Stage Manager. Marking it
    // "Transient" makes Stage Manager treat it as a short-lived, non-managed window
    // and can leave the owner's child-window group in a broken state after the app
    // is backgrounded and restored. Keep it out of the window cycle, but managed.
    _window.collectionBehavior = NSWindowCollectionBehaviorIgnoresCycle
        | NSWindowCollectionBehaviorFullScreenAuxiliary;
    [_window setAccessibilityElement:NO];
    _surface = [MEWindowShadowView new];
    [_surface setAccessibilityElement:NO];
    _window.contentView = _surface;
    NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
    for (NSNotificationName name in @[NSWindowDidMoveNotification, NSWindowDidResizeNotification,
        NSWindowDidBecomeKeyNotification, NSWindowDidResignKeyNotification,
        NSWindowDidMiniaturizeNotification, NSWindowDidDeminiaturizeNotification,
        NSWindowDidChangeOcclusionStateNotification, NSWindowDidChangeScreenNotification])
        [center addObserver:self selector:@selector(changed:) name:name object:owner];
    for (NSNotificationName name in @[NSWindowWillEnterFullScreenNotification, NSWindowWillExitFullScreenNotification])
        [center addObserver:self selector:@selector(beginTransition:) name:name object:owner];
    for (NSNotificationName name in @[NSWindowDidEnterFullScreenNotification, NSWindowDidExitFullScreenNotification])
        [center addObserver:self selector:@selector(endTransition:) name:name object:owner];
    [center addObserver:self selector:@selector(closing:) name:NSWindowWillCloseNotification object:owner];
    return self;
}
- (void)changed:(NSNotification *)notification { [self update]; }
- (void)beginTransition:(NSNotification *)notification {
    self.transitioning = YES;
    [self update];
}
- (void)endTransition:(NSNotification *)notification {
    self.transitioning = NO;
    [self update];
}
- (void)update {
    NSWindow *owner = self.owner;
    if (!self.enabled || self.transitioning || !owner.isVisible || owner.isMiniaturized
        || (owner.styleMask & NSWindowStyleMaskFullScreen)) {
        [owner removeChildWindow:self.window];
        [self.window orderOut:nil];
        return;
    }
    NSRect frame = NSInsetRect(owner.frame, -MEWindowShadowMargin, -MEWindowShadowMargin);
    if (!NSEqualRects(self.window.frame, frame)) {
        BOOL resized = !NSEqualSizes(self.window.frame.size, frame.size);
        [self.window setFrame:frame display:NO];
        if (resized) self.surface.needsDisplay = YES;
    }
    if (self.window.level != owner.level) self.window.level = owner.level;
    CGFloat alpha = owner.isKeyWindow ? 0.16 : 0.10;
    if (self.surface.shadowAlpha != alpha) {
        self.surface.shadowAlpha = alpha;
        self.surface.needsDisplay = YES;
    }
    if (self.window.parentWindow != owner) {
        [owner addChildWindow:self.window ordered:NSWindowBelow];
    }
}
- (void)closing:(NSNotification *)notification {
    NSWindow *owner = self.owner;
    [owner removeChildWindow:self.window];
    [self.window close];
    objc_setAssociatedObject(owner, &MEWindowShadowKey, nil, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
}
- (void)dealloc { [NSNotificationCenter.defaultCenter removeObserver:self]; }
@end

extern "C" __attribute__((visibility("default")))
void me_window_shadow_update(void *viewPointer, double cornerRadius, bool enabled) {
    @autoreleasepool {
        NSView *view = (__bridge NSView *)viewPointer;
        NSWindow *owner = view.window;
        if (!owner || !NSThread.isMainThread) return;
        MEWindowShadow *shadow = objc_getAssociatedObject(owner, &MEWindowShadowKey);
        if (!shadow) {
            if (!enabled) return;
            shadow = [[MEWindowShadow alloc] initWithOwner:owner];
            objc_setAssociatedObject(owner, &MEWindowShadowKey, shadow, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
        }
        if (shadow.surface.cornerRadius != cornerRadius) {
            shadow.surface.cornerRadius = cornerRadius;
            shadow.surface.needsDisplay = YES;
        }
        shadow.enabled = enabled;
        [shadow update];
    }
}
