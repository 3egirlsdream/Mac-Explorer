#import <AppKit/AppKit.h>
#import <objc/runtime.h>

typedef void (*DeliveryCallback)(int);

@interface FileDeliveryStatusItem : NSObject
@property(nonatomic, strong) NSStatusItem* item;
@property(nonatomic, weak) NSWindow* panel;
@property(nonatomic, strong) id globalMonitor;
@property(nonatomic, strong) id localMonitor;
@property(nonatomic) DeliveryCallback callback;
@end

@implementation FileDeliveryStatusItem
- (void)toggle:(id)sender { if (self.callback) self.callback(0); }
- (void)checkOutsideClick:(NSEvent*)event
{
    if (!self.panel.visible || !self.callback) return;
    // Local events carry the clicked window's coordinates. Reading the current
    // pointer here can race a queued event (and misclassify accessibility clicks).
    NSPoint point = event.window ? [event.window convertPointToScreen:event.locationInWindow] : NSEvent.mouseLocation;
    NSRect button = [self.item.button.window convertRectToScreen:self.item.button.frame];
    if (!NSPointInRect(point, self.panel.frame) && !NSPointInRect(point, button)) self.callback(1);
}
- (void)dealloc
{
    if (self.globalMonitor) [NSEvent removeMonitor:self.globalMonitor];
    if (self.localMonitor) [NSEvent removeMonitor:self.localMonitor];
    if (self.item) [NSStatusBar.systemStatusBar removeStatusItem:self.item];
}
@end

static FileDeliveryStatusItem* delivery;

static void ConfigureDeliveryDragActivation(NSView* view)
{
    // Only the delivery view delays activation; ordinary browser windows keep
    // Avalonia's input behavior. A click still activates on mouse-up.
    static Class deliveryViewClass;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        deliveryViewClass = objc_allocateClassPair(object_getClass(view), "MacExplorerDeliveryView", 0);
        Method method = class_getInstanceMethod(NSView.class, @selector(shouldDelayWindowOrderingForEvent:));
        class_addMethod(deliveryViewClass, @selector(shouldDelayWindowOrderingForEvent:),
            imp_implementationWithBlock(^BOOL(id self, NSEvent* event) { return YES; }),
            method_getTypeEncoding(method));
        objc_registerClassPair(deliveryViewClass);
    });
    object_setClass(view, deliveryViewClass);
}

void MacExplorerDeliveryPrepareDrag(NSView* view)
{
    if (view.window == delivery.panel)
        [NSApp preventWindowOrdering];
}

// The white folder/search artwork from Assets/appicon.svg, without its gradient tile.
// A template image lets the menu bar choose the correct monochrome contrast.
static NSImage* DeliveryMenuBarImage()
{
    NSImage* image = [NSImage imageWithSize:NSMakeSize(18, 18) flipped:NO drawingHandler:^BOOL(NSRect rect) {
        CGContextRef context = NSGraphicsContext.currentContext.CGContext;
        CGContextSaveGState(context);
        CGContextTranslateCTM(context, 0, 18);
        CGContextScaleCTM(context, 18.0 / 300, -18.0 / 300);
        CGContextTranslateCTM(context, -98, -106);
        CGContextSetRGBFillColor(context, 1, 1, 1, 1);
        CGContextSetRGBStrokeColor(context, 1, 1, 1, 1);
        CGContextMoveToPoint(context, 104, 175);
        CGContextAddLineToPoint(context, 104, 148);
        CGContextAddCurveToPoint(context, 104, 136.95, 112.95, 128, 124, 128);
        CGContextAddLineToPoint(context, 184, 128);
        CGContextAddCurveToPoint(context, 192.2, 128, 199.8, 132.6, 203.6, 139.6);
        CGContextAddLineToPoint(context, 212.4, 155.4);
        CGContextAddCurveToPoint(context, 216.2, 162.4, 223.8, 167, 232, 167);
        CGContextAddLineToPoint(context, 332, 167);
        CGContextAddCurveToPoint(context, 343.05, 167, 352, 175.95, 352, 187);
        CGContextAddLineToPoint(context, 352, 322);
        CGContextAddCurveToPoint(context, 352, 333.05, 343.05, 342, 332, 342);
        CGContextAddLineToPoint(context, 124, 342);
        CGContextAddCurveToPoint(context, 112.95, 342, 104, 333.05, 104, 322);
        CGContextClosePath(context);
        CGContextFillPath(context);
        CGContextSetBlendMode(context, kCGBlendModeClear);
        CGContextFillEllipseInRect(context, CGRectMake(267, 259, 106, 106));
        CGContextSetLineCap(context, kCGLineCapRound);
        CGContextSetLineWidth(context, 22);
        CGContextMoveToPoint(context, 350, 342);
        CGContextAddLineToPoint(context, 384, 376);
        CGContextStrokePath(context);
        CGContextSetBlendMode(context, kCGBlendModeNormal);
        CGContextSetLineWidth(context, 14);
        CGContextStrokeEllipseInRect(context, CGRectMake(278, 270, 84, 84));
        CGContextMoveToPoint(context, 350, 342);
        CGContextAddLineToPoint(context, 384, 376);
        CGContextStrokePath(context);
        CGContextRestoreGState(context);
        return YES;
    }];
    [image setTemplate:YES];
    return image;
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryCreate(DeliveryCallback callback)
{
    delivery = [FileDeliveryStatusItem new];
    delivery.callback = callback;
    delivery.item = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
    delivery.item.button.image = DeliveryMenuBarImage();
    delivery.item.button.toolTip = @"文件速递 · Mac Explorer";
    [delivery.item.button setAccessibilityLabel:@"文件速递"];
    delivery.item.button.target = delivery;
    delivery.item.button.action = @selector(toggle:);
    __weak FileDeliveryStatusItem* weakDelivery = delivery;
    NSEventMask mask = NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown;
    delivery.globalMonitor = [NSEvent addGlobalMonitorForEventsMatchingMask:mask handler:^(NSEvent* event) {
        [weakDelivery checkOutsideClick:event];
    }];
    delivery.localMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:mask handler:^NSEvent*(NSEvent* event) {
        [weakDelivery checkOutsideClick:event];
        return event;
    }];
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryDestroy() { delivery.callback = NULL; delivery = nil; }

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryPlace(void* viewHandle)
{
    NSView* view = (__bridge NSView*)viewHandle;
    NSWindow* window = view.window;
    if (!window || !delivery.item.button.window) return;
    delivery.panel = window;
    ConfigureDeliveryDragActivation(view);
    window.level = NSFloatingWindowLevel;
    window.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces
        | NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorIgnoresCycle;
    window.hidesOnDeactivate = NO;
    window.hasShadow = YES;
    NSRect button = [delivery.item.button.window convertRectToScreen:delivery.item.button.frame];
    NSScreen* screen = delivery.item.button.window.screen ?: NSScreen.mainScreen;
    NSRect bounds = screen.visibleFrame;
    NSSize size = window.frame.size;
    CGFloat scale = MIN(1, MIN(bounds.size.width / size.width, (bounds.size.height - 6) / size.height));
    size.width *= scale;
    size.height *= scale;
    CGFloat x = MAX(NSMinX(bounds), MIN(NSMidX(button) - size.width / 2, NSMaxX(bounds) - size.width));
    CGFloat y = MAX(NSMinY(bounds), MIN(NSMinY(button) - size.height - 6, NSMaxY(bounds) - size.height));
    [window setFrame:NSMakeRect(x, y, size.width, size.height) display:YES];
    // Do not activate NSApp: doing so would also raise the main browser window.
    [window orderFrontRegardless];
    [window makeKeyWindow];
    [window invalidateShadow];
}

extern "C" __attribute__((visibility("default")))
int MacExplorerDeliveryContainsPoint(double x, double y)
{
    return delivery.panel && NSPointInRect(NSMakePoint(x, y), delivery.panel.frame);
}
