#import <AppKit/AppKit.h>
#import <objc/runtime.h>
#import <QuartzCore/QuartzCore.h>

static char MEPreparedContentLayerKey;
static void PrepareContentLayer(NSView *view) {
    CALayer *layer = view.layer;
    if (!layer) return;
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    // Keep an outstanding frame at its original pixel size while the renderer catches up.
    layer.contentsGravity = layer.contentsAreFlipped ? kCAGravityBottomLeft : kCAGravityTopLeft;
    layer.contentsScale = view.window.backingScaleFactor;
    if (!objc_getAssociatedObject(layer, &MEPreparedContentLayerKey)) {
        NSMutableDictionary *actions = [layer.actions mutableCopy] ?: [NSMutableDictionary new];
        for (NSString *key in @[@"bounds", @"position", @"transform", @"contents", @"contentsRect", @"contentsScale"]) {
            actions[key] = NSNull.null;
            [layer removeAnimationForKey:key];
        }
        layer.actions = actions;
        objc_setAssociatedObject(layer, &MEPreparedContentLayerKey, @YES, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    }
    [CATransaction commit];
}

static char MEPreparedContentViewKey;
@interface MEContentLayerPreparation : NSObject
@property(nonatomic, weak) NSView *view;
@property(nonatomic, strong) NSColor *backgroundColor;
@property(nonatomic) CGFloat cornerRadius;
- (instancetype)initWithView:(NSView *)view;
- (void)prepareLayer;
@end
@implementation MEContentLayerPreparation
- (instancetype)initWithView:(NSView *)view {
    if (!(self = [super init])) return nil;
    _view = view;
    // Avalonia replaces the placeholder layer with its render target after Show.
    [view addObserver:self forKeyPath:@"layer" options:0 context:&MEPreparedContentViewKey];
    [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(backingChanged:)
        name:NSWindowDidChangeBackingPropertiesNotification object:view.window];
    return self;
}
- (void)observeValueForKeyPath:(NSString *)keyPath ofObject:(id)object change:(NSDictionary *)change context:(void *)context {
    if (context == &MEPreparedContentViewKey) [self prepareLayer];
    else [super observeValueForKeyPath:keyPath ofObject:object change:change context:context];
}
- (void)prepareLayer {
    PrepareContentLayer(self.view);
    if (!self.backgroundColor) return;
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    // The previous drawable stays pixel-aligned; fill the newly exposed area independently.
    self.view.layer.backgroundColor = self.backgroundColor.CGColor;
    self.view.layer.cornerRadius = self.cornerRadius;
    self.view.layer.masksToBounds = YES;
    [CATransaction commit];
}
- (void)backingChanged:(NSNotification *)notification { [self prepareLayer]; }
- (void)dealloc {
    [_view removeObserver:self forKeyPath:@"layer" context:&MEPreparedContentViewKey];
    [NSNotificationCenter.defaultCenter removeObserver:self];
}
@end

static MEContentLayerPreparation *PrepareContentView(NSView *view) {
    if (!objc_getAssociatedObject(view, &MEPreparedContentViewKey))
        objc_setAssociatedObject(view, &MEPreparedContentViewKey,
            [[MEContentLayerPreparation alloc] initWithView:view], OBJC_ASSOCIATION_RETAIN_NONATOMIC);
    MEContentLayerPreparation *preparation = objc_getAssociatedObject(view, &MEPreparedContentViewKey);
    [preparation prepareLayer];
    return preparation;
}

extern "C" __attribute__((visibility("default")))
void me_window_chrome_prepare(void *viewPointer) {
    NSView *view = (__bridge NSView *)viewPointer;
    NSWindow *window = view.window;
    if (!window || !NSThread.isMainThread) return;
    window.animationBehavior = NSWindowAnimationBehaviorNone;
    PrepareContentView(view);
    if (window.contentView != view) PrepareContentView(window.contentView);
}

extern "C" __attribute__((visibility("default")))
void me_window_background_update(void *viewPointer, double red, double green, double blue, double alpha,
    double cornerRadius) {
    NSView *view = (__bridge NSView *)viewPointer;
    if (!view.window || !NSThread.isMainThread) return;
    NSColor *color = [NSColor colorWithSRGBRed:red green:green blue:blue alpha:alpha];
    for (NSView *surface in view.window.contentView == view ? @[view] : @[view, view.window.contentView]) {
        MEContentLayerPreparation *preparation = PrepareContentView(surface);
        preparation.backgroundColor = color;
        preparation.cornerRadius = cornerRadius;
        [preparation prepareLayer];
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
    _window.collectionBehavior = NSWindowCollectionBehaviorTransient
        | NSWindowCollectionBehaviorIgnoresCycle | NSWindowCollectionBehaviorFullScreenAuxiliary;
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
