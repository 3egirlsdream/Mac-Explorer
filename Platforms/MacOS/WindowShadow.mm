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

static CGImageRef MECreateNineSliceShadowImage(CGFloat cornerRadius, CGFloat shadowAlpha, CGFloat scale,
    CGFloat *outCap, CGFloat *outSize) {
    CGFloat cap = ceil(MEWindowShadowMargin + cornerRadius + 2.0);
    CGFloat size = cap * 2.0 + 4.0;
    *outCap = cap;
    *outSize = size;
    NSInteger pixels = MAX((NSInteger)llround(size * scale), 1);
    NSBitmapImageRep *rep = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nullptr
        pixelsWide:pixels pixelsHigh:pixels bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO
        colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
    rep.size = NSMakeSize(size, size);
    [NSGraphicsContext saveGraphicsState];
    NSGraphicsContext *ctx = [NSGraphicsContext graphicsContextWithBitmapImageRep:rep];
    [NSGraphicsContext setCurrentContext:ctx];
    NSRect bounds = NSMakeRect(0, 0, size, size);
    [[NSColor clearColor] setFill];
    NSRectFillUsingOperation(bounds, NSCompositingOperationCopy);
    NSRect body = NSInsetRect(bounds, MEWindowShadowMargin, MEWindowShadowMargin);
    NSShadow *shadow = [NSShadow new];
    shadow.shadowColor = [NSColor colorWithWhite:0 alpha:shadowAlpha];
    shadow.shadowBlurRadius = 18;
    shadow.shadowOffset = NSMakeSize(0, -4);
    [shadow set];
    [[NSColor blackColor] setFill];
    [[NSBezierPath bezierPathWithRoundedRect:body xRadius:cornerRadius yRadius:cornerRadius] fill];

    // Erase the caster and its antialiased edge so no dark rim or interior tint remains.
    ctx.compositingOperation = NSCompositingOperationClear;
    [[NSBezierPath bezierPathWithRoundedRect:NSInsetRect(body, -0.5, -0.5)
        xRadius:cornerRadius + 0.5 yRadius:cornerRadius + 0.5] fill];
    [NSGraphicsContext restoreGraphicsState];
    return CGImageRetain(rep.CGImage);
}

@interface MEWindowShadowView : NSView
@property(nonatomic) CGFloat cornerRadius;
@property(nonatomic) CGFloat shadowAlpha;
@property(nonatomic) CGFloat cachedRadius;
@property(nonatomic) CGFloat cachedAlpha;
@property(nonatomic) CGFloat cachedScale;
@property(nonatomic) CGFloat cachedCap;
@property(nonatomic) CGFloat cachedSize;
@property(nonatomic) CGImageRef cachedImage;
- (void)refreshShadowLayer;
@end

@implementation MEWindowShadowView
- (instancetype)initWithFrame:(NSRect)frameRect {
    if (!(self = [super initWithFrame:frameRect])) return nil;
    _cachedRadius = -1;
    _cachedAlpha = -1;
    _cachedScale = -1;
    self.wantsLayer = YES;
    self.layerContentsRedrawPolicy = NSViewLayerContentsRedrawOnSetNeedsDisplay;
    return self;
}
- (BOOL)isOpaque { return NO; }
- (BOOL)wantsUpdateLayer { return YES; }
- (void)refreshShadowLayer {
    CALayer *layer = self.layer;
    if (!layer) return;
    CGFloat scale = self.window.backingScaleFactor ?: 2.0;
    if (!self.cachedImage || self.cachedRadius != self.cornerRadius
        || self.cachedAlpha != self.shadowAlpha || self.cachedScale != scale) {
        if (self.cachedImage) CGImageRelease(self.cachedImage);
        CGFloat cap = 0, size = 0;
        self.cachedImage = MECreateNineSliceShadowImage(self.cornerRadius, self.shadowAlpha, scale, &cap, &size);
        self.cachedRadius = self.cornerRadius;
        self.cachedAlpha = self.shadowAlpha;
        self.cachedScale = scale;
        self.cachedCap = cap;
        self.cachedSize = size;
    }
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    layer.contents = (__bridge id)self.cachedImage;
    layer.contentsScale = self.cachedScale;
    CGFloat norm = self.cachedSize > 0 ? self.cachedCap / self.cachedSize : 0.45;
    layer.contentsCenter = CGRectMake(norm, norm, MAX(1.0 - norm * 2.0, 0.01), MAX(1.0 - norm * 2.0, 0.01));
    [CATransaction commit];
}
- (void)updateLayer { [self refreshShadowLayer]; }
- (void)viewDidChangeBackingProperties {
    [super viewDidChangeBackingProperties];
    self.needsDisplay = YES;
}
- (void)dealloc {
    if (_cachedImage) CGImageRelease(_cachedImage);
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
        [self.window setFrame:frame display:NO];
    }
    if (self.window.level != owner.level) self.window.level = owner.level;
    CGFloat alpha = owner.isKeyWindow ? 0.16 : 0.10;
    if (self.surface.shadowAlpha != alpha) {
        self.surface.shadowAlpha = alpha;
        self.surface.needsDisplay = YES;
    }
    if (!self.surface.cachedImage) [self.surface refreshShadowLayer];
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

typedef void (*MEWindowZoomCompletion)(void *context, bool completed, bool maximized);

static char MEWindowZoomStateKey;
static NSString *const MEWindowZoomAnimationKey = @"meWindowZoom";

static BOOL MEFramesNearlyEqual(NSRect a, NSRect b) {
    return fabs(a.origin.x - b.origin.x) <= 2.0
        && fabs(a.origin.y - b.origin.y) <= 2.0
        && fabs(a.size.width - b.size.width) <= 2.0
        && fabs(a.size.height - b.size.height) <= 2.0;
}

static NSRect MEClampFrameToVisible(NSRect frame, NSRect visible) {
    CGFloat width = MIN(MAX(frame.size.width, 320.0), visible.size.width);
    CGFloat height = MIN(MAX(frame.size.height, 240.0), visible.size.height);
    CGFloat x = MIN(MAX(frame.origin.x, visible.origin.x), NSMaxX(visible) - width);
    CGFloat y = MIN(MAX(frame.origin.y, visible.origin.y), NSMaxY(visible) - height);
    return NSMakeRect(round(x), round(y), round(width), round(height));
}

static NSRect MEDefaultRestoreFrame(NSRect visible) {
    CGFloat width = MIN(1280.0, visible.size.width * 0.85);
    CGFloat height = MIN(800.0, visible.size.height * 0.85);
    CGFloat x = visible.origin.x + (visible.size.width - width) * 0.5;
    CGFloat y = visible.origin.y + (visible.size.height - height) * 0.5;
    return NSMakeRect(round(x), round(y), round(width), round(height));
}

static CATransform3D METransformContainerToVisual(NSRect container, NSRect visual, CALayer *layer) {
    if (container.size.width <= 0 || container.size.height <= 0)
        return CATransform3DIdentity;
    CGFloat sx = visual.size.width / container.size.width;
    CGFloat sy = visual.size.height / container.size.height;
    CGFloat localX = visual.origin.x - container.origin.x;
    CGFloat localY = layer.isGeometryFlipped
        ? (NSMaxY(container) - NSMaxY(visual))
        : (visual.origin.y - container.origin.y);
    CGPoint anchor = layer.anchorPoint;
    CGFloat tx = localX - anchor.x * (container.size.width - visual.size.width);
    CGFloat ty = localY - anchor.y * (container.size.height - visual.size.height);
    CATransform3D xf = CATransform3DMakeTranslation(tx, ty, 0);
    return CATransform3DScale(xf, sx, sy, 1);
}

static CATransform3D METransformPinOldDrawable(NSRect container, NSRect visual, CALayer *layer) {
    CGFloat dx = visual.origin.x - container.origin.x;
    CGFloat dy = layer.isGeometryFlipped
        ? (NSMaxY(container) - NSMaxY(visual))
        : (NSMaxY(visual) - NSMaxY(container));
    return CATransform3DMakeTranslation(dx, dy, 0);
}

static CATransform3D MECurrentLayerTransform(CALayer *layer) {
    if (!layer) return CATransform3DIdentity;
    CALayer *presentation = layer.presentationLayer;
    return presentation ? presentation.transform : layer.transform;
}

static void MEAddTransformAnimation(CALayer *layer, CATransform3D fromXf, CATransform3D toXf,
    CFTimeInterval duration, CAMediaTimingFunction *timing) {
    if (!layer) return;
    CABasicAnimation *anim = [CABasicAnimation animationWithKeyPath:@"transform"];
    anim.fromValue = [NSValue valueWithCATransform3D:fromXf];
    anim.toValue = [NSValue valueWithCATransform3D:toXf];
    anim.duration = duration;
    anim.timingFunction = timing;
    anim.fillMode = kCAFillModeBackwards;
    [layer removeAnimationForKey:MEWindowZoomAnimationKey];
    layer.transform = toXf;
    [layer addAnimation:anim forKey:MEWindowZoomAnimationKey];
}

@interface MEWindowZoomState : NSObject
@property(nonatomic, weak) NSWindow *window;
@property(nonatomic, weak) NSView *targetView;
@property(nonatomic) BOOL hasRestoreFrame;
@property(nonatomic) NSRect restoreFrame;
@property(nonatomic) BOOL animating;
@property(nonatomic) BOOL waitingForExpandFrame;
@property(nonatomic) BOOL targetMaximized;
@property(nonatomic) NSRect containerFrame;
@property(nonatomic) NSRect visualStartFrame;
@property(nonatomic) NSRect finalWindowFrame;
@property(nonatomic) NSUInteger generation;
@property(nonatomic) MEWindowZoomCompletion completion;
@property(nonatomic) void *completionContext;
- (instancetype)initWithWindow:(NSWindow *)window view:(NSView *)view;
- (void)finishPendingWithCompleted:(BOOL)completed resetLayers:(BOOL)resetLayers;
- (void)beginGpuExpandForGeneration:(NSUInteger)token;
@end

@implementation MEWindowZoomState
- (instancetype)initWithWindow:(NSWindow *)window view:(NSView *)view {
    if (!(self = [super init])) return nil;
    _window = window;
    _targetView = view;
    NSNotificationCenter *center = NSNotificationCenter.defaultCenter;
    [center addObserver:self selector:@selector(interrupt:) name:NSWindowWillCloseNotification object:window];
    [center addObserver:self selector:@selector(interrupt:) name:NSWindowWillEnterFullScreenNotification object:window];
    return self;
}
- (CALayer *)animatableLayer {
    NSView *view = self.targetView ?: self.window.contentView;
    if (!view) return nil;
    if (!view.wantsLayer) view.wantsLayer = YES;
    return view.layer;
}
- (MEWindowShadow *)windowShadow {
    NSWindow *window = self.window;
    return window ? objc_getAssociatedObject(window, &MEWindowShadowKey) : nil;
}
- (void)resetLayerTransforms {
    CALayer *layer = [self animatableLayer];
    MEWindowShadow *shadow = [self windowShadow];
    [CATransaction begin];
    [CATransaction setDisableActions:YES];
    if (layer) {
        [layer removeAnimationForKey:MEWindowZoomAnimationKey];
        layer.transform = CATransform3DIdentity;
    }
    if (shadow.surface.layer) {
        [shadow.surface.layer removeAnimationForKey:MEWindowZoomAnimationKey];
        shadow.surface.layer.transform = CATransform3DIdentity;
    }
    [CATransaction commit];
}
- (void)interrupt:(NSNotification *)notification {
    self.generation++;
    [self finishPendingWithCompleted:NO resetLayers:YES];
}
- (void)finishPendingWithCompleted:(BOOL)completed resetLayers:(BOOL)resetLayers {
    self.animating = NO;
    self.waitingForExpandFrame = NO;
    if (resetLayers) {
        [self resetLayerTransforms];
        MEWindowShadow *shadow = [self windowShadow];
        if (shadow && shadow.transitioning) {
            shadow.transitioning = NO;
            [shadow update];
        }
    }
    MEWindowZoomCompletion cb = self.completion;
    void *ctx = self.completionContext;
    BOOL maximized = self.targetMaximized;
    self.completion = nullptr;
    self.completionContext = nullptr;
    if (cb) cb(ctx, completed, maximized);
}
- (void)beginGpuExpandForGeneration:(NSUInteger)token {
    if (self.generation != token || !self.waitingForExpandFrame) return;
    self.waitingForExpandFrame = NO;
    NSWindow *window = self.window;
    CALayer *layer = [self animatableLayer];
    if (!window || !layer) {
        [self finishPendingWithCompleted:YES resetLayers:YES];
        return;
    }

    NSRect container = self.containerFrame;
    NSRect startVisual = self.visualStartFrame;
    CATransform3D fromXf = METransformContainerToVisual(container, startVisual, layer);
    MEWindowShadow *shadow = [self windowShadow];
    CALayer *shadowLayer = shadow.surface.layer;

    if (shadow) {
        shadow.transitioning = NO;
        [shadow update];
        shadowLayer = shadow.surface.layer;
    }

    CAMediaTimingFunction *timing = [CAMediaTimingFunction functionWithControlPoints:0.22f :1.0f :0.36f :1.0f];
    CFTimeInterval duration = 0.22;

    __weak MEWindowZoomState *weakSelf = self;
    [CATransaction begin];
    [CATransaction setAnimationDuration:duration];
    [CATransaction setAnimationTimingFunction:timing];
    [CATransaction setCompletionBlock:^{
        MEWindowZoomState *strong = weakSelf;
        if (!strong || strong.generation != token) return;
        [strong finishPendingWithCompleted:YES resetLayers:YES];
    }];
    MEAddTransformAnimation(layer, fromXf, CATransform3DIdentity, duration, timing);
    if (shadowLayer) {
        NSRect shadowContainer = NSInsetRect(container, -MEWindowShadowMargin, -MEWindowShadowMargin);
        NSRect shadowStart = NSInsetRect(startVisual, -MEWindowShadowMargin, -MEWindowShadowMargin);
        CATransform3D shadowFrom = METransformContainerToVisual(shadowContainer, shadowStart, shadowLayer);
        MEAddTransformAnimation(shadowLayer, shadowFrom, CATransform3DIdentity, duration, timing);
    }
    [CATransaction commit];
}
- (void)dealloc {
    [NSNotificationCenter.defaultCenter removeObserver:self];
    self.generation++;
    [self finishPendingWithCompleted:NO resetLayers:YES];
}
@end

extern "C" __attribute__((visibility("default")))
void me_window_zoom_commit_expand(void *viewPointer) {
    @autoreleasepool {
        NSView *view = (__bridge NSView *)viewPointer;
        NSWindow *window = view.window;
        if (!window || !NSThread.isMainThread) return;
        MEWindowZoomState *state = objc_getAssociatedObject(window, &MEWindowZoomStateKey);
        if (state && state.waitingForExpandFrame)
            [state beginGpuExpandForGeneration:state.generation];
    }
}

extern "C" __attribute__((visibility("default")))
bool me_window_toggle_zoom(void *viewPointer, bool currentlyMaximized, bool animate,
    MEWindowZoomCompletion completion, void *context) {
    @autoreleasepool {
        NSView *view = (__bridge NSView *)viewPointer;
        NSWindow *window = view.window;
        if (!window || !NSThread.isMainThread || (window.styleMask & NSWindowStyleMaskFullScreen))
            return false;

        NSScreen *screen = window.screen ?: NSScreen.mainScreen;
        if (!screen) return false;
        NSRect visible = screen.visibleFrame;
        if (visible.size.width <= 0 || visible.size.height <= 0) return false;

        MEWindowZoomState *state = objc_getAssociatedObject(window, &MEWindowZoomStateKey);
        if (!state) {
            state = [[MEWindowZoomState alloc] initWithWindow:window view:view];
            objc_setAssociatedObject(window, &MEWindowZoomStateKey, state, OBJC_ASSOCIATION_RETAIN_NONATOMIC);
        } else {
            state.targetView = view;
        }

        BOOL wasAnimating = state.animating;
        BOOL isMaximized = wasAnimating
            ? state.targetMaximized
            : (currentlyMaximized || MEFramesNearlyEqual(window.frame, visible));
        BOOL targetMaximized = !isMaximized;

        NSRect targetFrame;
        if (targetMaximized) {
            if (!wasAnimating && !MEFramesNearlyEqual(window.frame, visible)) {
                state.restoreFrame = window.frame;
                state.hasRestoreFrame = YES;
            }
            targetFrame = visible;
        } else {
            NSRect fallback = state.hasRestoreFrame ? state.restoreFrame : MEDefaultRestoreFrame(visible);
            if (MEFramesNearlyEqual(fallback, visible)) fallback = MEDefaultRestoreFrame(visible);
            targetFrame = MEClampFrameToVisible(fallback, visible);
        }

        CALayer *layer = [state animatableLayer];
        CATransform3D liveTransform = wasAnimating ? MECurrentLayerTransform(layer) : CATransform3DIdentity;
        MEWindowShadow *shadow = [state windowShadow];
        CATransform3D liveShadowTransform = (wasAnimating && shadow.surface.layer)
            ? MECurrentLayerTransform(shadow.surface.layer) : CATransform3DIdentity;

        // Supersede any in-flight zoom and free its previous completion context immediately.
        state.generation++;
        [state finishPendingWithCompleted:NO resetLayers:!wasAnimating];
        NSUInteger token = state.generation;

        if (!animate || !layer || MEFramesNearlyEqual(window.frame, targetFrame) && !wasAnimating) {
            [state resetLayerTransforms];
            state.targetMaximized = targetMaximized;
            [window setFrame:targetFrame display:YES animate:NO];
            if (shadow) {
                shadow.transitioning = NO;
                [shadow update];
            }
            if (completion) completion(context, true, targetMaximized);
            return true;
        }

        state.targetMaximized = targetMaximized;
        state.completion = completion;
        state.completionContext = context;
        state.animating = YES;
        state.finalWindowFrame = targetFrame;

        CAMediaTimingFunction *timing = [CAMediaTimingFunction functionWithControlPoints:0.22f :1.0f :0.36f :1.0f];

        if (targetMaximized) {
            // If reversing mid-shrink, window is already at `visible` with live GPU transform: animate straight back!
            if (wasAnimating && MEFramesNearlyEqual(window.frame, visible)) {
                CFTimeInterval duration = 0.22;
                __weak MEWindowZoomState *weakState = state;
                [CATransaction begin];
                [CATransaction setAnimationDuration:duration];
                [CATransaction setAnimationTimingFunction:timing];
                [CATransaction setCompletionBlock:^{
                    MEWindowZoomState *strong = weakState;
                    if (!strong || strong.generation != token) return;
                    [strong finishPendingWithCompleted:YES resetLayers:YES];
                }];
                MEAddTransformAnimation(layer, liveTransform, CATransform3DIdentity, duration, timing);
                if (shadow.surface.layer)
                    MEAddTransformAnimation(shadow.surface.layer, liveShadowTransform, CATransform3DIdentity, duration, timing);
                [CATransaction commit];
                return true;
            }

            // Single resize to `visible`, pin the old drawable at its exact screen spot for 1 frame while
            // Avalonia renders once at `visible`, then expand on the GPU at 120fps.
            NSRect startVisual = window.frame;
            state.containerFrame = visible;
            state.visualStartFrame = startVisual;
            state.waitingForExpandFrame = YES;

            if (shadow) {
                shadow.transitioning = YES;
                [shadow update];
            }
            [CATransaction begin];
            [CATransaction setDisableActions:YES];
            [layer removeAnimationForKey:MEWindowZoomAnimationKey];
            [window setFrame:visible display:NO animate:NO];
            layer.transform = METransformPinOldDrawable(visible, startVisual, layer);
            [CATransaction commit];

            // Fallback in case the host does not invoke me_window_zoom_commit_expand explicitly.
            __weak MEWindowZoomState *weakState = state;
            dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)(30 * NSEC_PER_MSEC)), dispatch_get_main_queue(), ^{
                MEWindowZoomState *strong = weakState;
                if (strong && strong.generation == token && strong.waitingForExpandFrame)
                    [strong beginGpuExpandForGeneration:token];
            });
            return true;
        } else {
            // Restore: window is already rendered at `container` (visible); shrink on GPU at 120fps,
            // then commit the single resize to `targetFrame` when the animation lands.
            NSRect container = window.frame;
            CATransform3D toXf = METransformContainerToVisual(container, targetFrame, layer);
            CATransform3D fromXf = wasAnimating ? liveTransform : CATransform3DIdentity;
            CFTimeInterval duration = 0.20;

            __weak MEWindowZoomState *weakState = state;
            [CATransaction begin];
            [CATransaction setAnimationDuration:duration];
            [CATransaction setAnimationTimingFunction:timing];
            [CATransaction setCompletionBlock:^{
                MEWindowZoomState *strong = weakState;
                if (!strong || strong.generation != token) return;
                NSWindow *win = strong.window;
                [CATransaction begin];
                [CATransaction setDisableActions:YES];
                [strong resetLayerTransforms];
                if (win) [win setFrame:targetFrame display:YES animate:NO];
                [CATransaction commit];
                MEWindowShadow *sh = [strong windowShadow];
                if (sh) {
                    sh.transitioning = NO;
                    [sh update];
                }
                [strong finishPendingWithCompleted:YES resetLayers:NO];
            }];
            MEAddTransformAnimation(layer, fromXf, toXf, duration, timing);
            if (shadow.surface.layer) {
                NSRect shadowContainer = NSInsetRect(container, -MEWindowShadowMargin, -MEWindowShadowMargin);
                NSRect shadowTarget = NSInsetRect(targetFrame, -MEWindowShadowMargin, -MEWindowShadowMargin);
                CATransform3D shadowTo = METransformContainerToVisual(shadowContainer, shadowTarget, shadow.surface.layer);
                CATransform3D shadowFrom = wasAnimating ? liveShadowTransform : CATransform3DIdentity;
                MEAddTransformAnimation(shadow.surface.layer, shadowFrom, shadowTo, duration, timing);
            }
            [CATransaction commit];
            return true;
        }
    }
}
