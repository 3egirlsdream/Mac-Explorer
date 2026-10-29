#import <AppKit/AppKit.h>
#import <objc/runtime.h>

typedef void (*DeliveryCallback)(int);
typedef int (*DeliveryDropCallback)(const char*);
static NSMutableDictionary<NSString*, NSURL*>* stagingAccess;

static BOOL DeliveryHasFileURLs(NSPasteboard* pasteboard)
{
    return [pasteboard availableTypeFromArray:@[NSPasteboardTypeFileURL]] != nil;
}


@interface FileDeliveryStatusItem : NSObject
@property(nonatomic, strong) NSStatusItem* item;
@property(nonatomic, weak) NSWindow* panel;
@property(nonatomic, weak) NSWindow* dropPanel;
@property(nonatomic, strong) NSTimer* dragTimer;
@property(nonatomic) NSInteger dragChangeCount;
@property(nonatomic) NSInteger dragPollCount;
@property(nonatomic) NSInteger dragStartCount;
@property(nonatomic) BOOL dragging;
@property(nonatomic) BOOL internalDrag;
@property(nonatomic) BOOL observedMouseDown;
@property(nonatomic) BOOL cancelledUntilRelease;
@property(nonatomic) DeliveryDropCallback dropCallback;
- (void)beginFileDrag;
- (void)endFileDrag;
- (void)pollFileDrag;
- (void)observeMouseEvent:(NSEvent*)event;
- (void)observeDragPressed:(BOOL)pressed changeCount:(NSInteger)changeCount hasFiles:(BOOL)hasFiles cancelled:(BOOL)cancelled;
@property(nonatomic, strong) id globalMonitor;
@property(nonatomic, strong) id localMonitor;
@property(nonatomic) DeliveryCallback callback;
@end

@implementation FileDeliveryStatusItem
- (void)beginFileDrag
{
    if (self.dragging) return;
    self.dragging = YES;
    self.dragStartCount++;
    if (self.callback) self.callback(2);
}
- (void)endFileDrag
{
    self.internalDrag = NO;
    self.observedMouseDown = NO;
    self.dragChangeCount = [NSPasteboard pasteboardWithName:NSPasteboardNameDrag].changeCount;
    if (!self.dragging) return;
    self.dragging = NO;
    if (self.callback) self.callback(3);
}
- (void)observeDragPressed:(BOOL)pressed changeCount:(NSInteger)changeCount hasFiles:(BOOL)hasFiles cancelled:(BOOL)cancelled
{
    if (!pressed) self.cancelledUntilRelease = NO;
    if (cancelled && self.dragging)
    {
        self.cancelledUntilRelease = pressed;
        [self endFileDrag];
    }
    else if (!pressed && !self.internalDrag) [self endFileDrag];
    else if (!self.cancelledUntilRelease && !self.dragging && changeCount != self.dragChangeCount && hasFiles)
        [self beginFileDrag];
    self.dragChangeCount = changeCount;
}
- (void)pollFileDrag
{
    self.dragPollCount++;
    NSPasteboard* pasteboard = [NSPasteboard pasteboardWithName:NSPasteboardNameDrag];
    [self observeDragPressed:self.observedMouseDown || (NSEvent.pressedMouseButtons & 1) != 0 changeCount:pasteboard.changeCount
        hasFiles:DeliveryHasFileURLs(pasteboard) cancelled:CGEventSourceKeyState(kCGEventSourceStateCombinedSessionState, 53)];
}
- (void)observeMouseEvent:(NSEvent*)event
{
    if (event.type == NSEventTypeLeftMouseDown)
    {
        self.observedMouseDown = YES;
    }
    else if (event.type == NSEventTypeLeftMouseDragged)
    {
        self.observedMouseDown = YES;
        [self pollFileDrag];
    }
    else if (event.type == NSEventTypeLeftMouseUp)
    {
        self.observedMouseDown = NO;
        // Let AppKit finish performDragOperation before closing its destination.
        __weak FileDeliveryStatusItem* weakSelf = self;
        dispatch_async(dispatch_get_main_queue(), ^{
            if (!weakSelf.observedMouseDown && !weakSelf.internalDrag) [weakSelf endFileDrag];
        });
    }
    [self checkOutsideClick:event];
}
- (void)toggle:(id)sender { if (self.callback) self.callback(0); }
- (void)checkOutsideClick:(NSEvent*)event
{
    if (event.type != NSEventTypeLeftMouseDown && event.type != NSEventTypeRightMouseDown) return;
    if (!self.panel.visible || !self.callback) return;
    // Local events carry the clicked window's coordinates. Reading the current
    // pointer here can race a queued event (and misclassify accessibility clicks).
    NSPoint point = event.window ? [event.window convertPointToScreen:event.locationInWindow] : NSEvent.mouseLocation;
    NSRect button = [self.item.button.window convertRectToScreen:self.item.button.frame];
    if (!NSPointInRect(point, self.panel.frame) && !NSPointInRect(point, button)) self.callback(1);
}
- (void)dealloc
{
    [self.dragTimer invalidate];
    if (self.globalMonitor) [NSEvent removeMonitor:self.globalMonitor];
    if (self.localMonitor) [NSEvent removeMonitor:self.localMonitor];
    if (self.item) [NSStatusBar.systemStatusBar removeStatusItem:self.item];
}
@end

static FileDeliveryStatusItem* delivery;

void MacExplorerDeliveryDragStarted()
{
    delivery.internalDrag = YES;
    [delivery beginFileDrag];
}
void MacExplorerDeliveryDragEnded() { [delivery endFileDrag]; }

static NSDragOperation DeliveryDropOperation(id<NSDraggingInfo> info)
{
    return DeliveryHasFileURLs(info.draggingPasteboard) && (info.draggingSourceOperationMask & NSDragOperationCopy)
        ? NSDragOperationCopy : NSDragOperationNone;
}

static NSDragOperation DeliveryDropEntered(id view, SEL selector, id<NSDraggingInfo> info)
{
    NSDragOperation operation = DeliveryDropOperation(info);
    if (delivery.callback) delivery.callback(operation == NSDragOperationNone ? 5 : 4);
    return operation;
}
static void DeliveryDropExited(id view, SEL selector, id<NSDraggingInfo> info)
{
    if (delivery.callback) delivery.callback(5);
}
static void DeliveryDropEnded(id view, SEL selector, id<NSDraggingInfo> info)
{
    [delivery endFileDrag];
}
static void DeliveryDropConclude(id view, SEL selector, id<NSDraggingInfo> info) { }
static BOOL DeliveryDropPrepare(id view, SEL selector, id<NSDraggingInfo> info)
{
    return DeliveryDropOperation(info) != NSDragOperationNone;
}
static BOOL DeliveryDropPerform(id view, SEL selector, id<NSDraggingInfo> info)
{
    if (!delivery.dropCallback || DeliveryDropOperation(info) == NSDragOperationNone) return NO;
    NSArray<NSURL*>* urls = [info.draggingPasteboard readObjectsForClasses:@[NSURL.class]
        options:@{ NSPasteboardURLReadingFileURLsOnlyKey: @YES }];
    NSMutableArray<NSString*>* paths = [NSMutableArray new];
    NSMutableArray<NSURL*>* retainedURLs = [NSMutableArray new];
    for (NSURL* url in urls)
    {
        if (!url.isFileURL || (url.host.length && ![url.host isEqualToString:@"localhost"])) continue;
        NSString* path = url.filePathURL.path.stringByStandardizingPath;
        if (!path.isAbsolutePath || [path rangeOfString:[NSString stringWithFormat:@"%C", (unichar)0]].location != NSNotFound) continue;
        [paths addObject:path];
        [retainedURLs addObject:url];
    }
    if (!paths.count) return NO;
    if (!stagingAccess) stagingAccess = [NSMutableDictionary new];
    // Keep the original URL's temporary sandbox access for the in-process shelf.
    NSMutableArray<NSString*>* opened = [NSMutableArray new];
    for (NSUInteger index = 0; index < paths.count; index++)
        if (!stagingAccess[paths[index]] && [retainedURLs[index] startAccessingSecurityScopedResource])
        {
            stagingAccess[paths[index]] = retainedURLs[index];
            [opened addObject:paths[index]];
        }
    NSData* json = [NSJSONSerialization dataWithJSONObject:paths options:0 error:nil];
    NSString* payload = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
    BOOL accepted = delivery.dropCallback(payload.UTF8String) != 0;
    if (!accepted)
        for (NSString* path in opened)
        {
            [stagingAccess[path] stopAccessingSecurityScopedResource];
            [stagingAccess removeObjectForKey:path];
        }
    [delivery endFileDrag];
    return accepted;
}

static void ConfigureDeliveryDrop(NSView* view)
{
    static Class dropClass;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        dropClass = objc_allocateClassPair(object_getClass(view), "MacExplorerDeliveryDropView", 0);
        class_addMethod(dropClass, @selector(draggingEntered:), (IMP)DeliveryDropEntered, "Q@:@");
        class_addMethod(dropClass, @selector(draggingUpdated:), (IMP)DeliveryDropEntered, "Q@:@");
        class_addMethod(dropClass, @selector(draggingEnded:), (IMP)DeliveryDropEnded, "v@:@");
        class_addMethod(dropClass, @selector(concludeDragOperation:), (IMP)DeliveryDropConclude, "v@:@");
        class_addMethod(dropClass, @selector(draggingExited:), (IMP)DeliveryDropExited, "v@:@");
        class_addMethod(dropClass, @selector(prepareForDragOperation:), (IMP)DeliveryDropPrepare, "B@:@");
        class_addMethod(dropClass, @selector(performDragOperation:), (IMP)DeliveryDropPerform, "B@:@");
        objc_registerClassPair(dropClass);
    });
    object_setClass(view, dropClass);
    [view registerForDraggedTypes:@[NSPasteboardTypeFileURL]];
}


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
        Method dragged = class_getInstanceMethod(object_getClass(view), @selector(mouseDragged:));
        IMP forwardDragged = method_getImplementation(dragged);
        class_addMethod(deliveryViewClass, @selector(mouseDragged:),
            imp_implementationWithBlock(^(NSView* self, NSEvent* event) {
                // Cold preview/render initialization may pump a mouse-up before the
                // managed handler reaches beginDraggingSession. Suppress ordering first.
                [NSApp preventWindowOrdering];
                ((void (*)(id, SEL, NSEvent*))forwardDragged)(self, @selector(mouseDragged:), event);
            }), method_getTypeEncoding(dragged));
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
void MacExplorerDeliveryCreate(DeliveryCallback callback, DeliveryDropCallback dropCallback)
{
    delivery = [FileDeliveryStatusItem new];
    delivery.callback = callback;
    delivery.dropCallback = dropCallback;
    delivery.dragChangeCount = [NSPasteboard pasteboardWithName:NSPasteboardNameDrag].changeCount;
    __weak FileDeliveryStatusItem* weakTimerOwner = delivery;
    delivery.dragTimer = [NSTimer timerWithTimeInterval:0.1 repeats:YES block:^(NSTimer* timer) {
        [weakTimerOwner pollFileDrag];
    }];
    [NSRunLoop.mainRunLoop addTimer:delivery.dragTimer forMode:NSRunLoopCommonModes];
    delivery.item = [NSStatusBar.systemStatusBar statusItemWithLength:NSSquareStatusItemLength];
    delivery.item.button.image = DeliveryMenuBarImage();
    NSString *appName = [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleDisplayName"]
        ?: [NSBundle.mainBundle objectForInfoDictionaryKey:@"CFBundleName"]
        ?: NSProcessInfo.processInfo.processName;
    delivery.item.button.toolTip = [@"文件速递 · " stringByAppendingString:appName];
    [delivery.item.button setAccessibilityLabel:@"文件速递"];
    delivery.item.button.target = delivery;
    delivery.item.button.action = @selector(toggle:);
    __weak FileDeliveryStatusItem* weakDelivery = delivery;
    NSEventMask mask = NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown | NSEventMaskLeftMouseUp | NSEventMaskLeftMouseDragged;
    delivery.globalMonitor = [NSEvent addGlobalMonitorForEventsMatchingMask:mask handler:^(NSEvent* event) {
        [weakDelivery observeMouseEvent:event];
    }];
    delivery.localMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:mask handler:^NSEvent*(NSEvent* event) {
        [weakDelivery observeMouseEvent:event];
        return event;
    }];
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryDestroy()
{
    [delivery.dragTimer invalidate];
    delivery.callback = NULL;
    delivery.dropCallback = NULL;
    delivery = nil;
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryClearStagingAccess()
{
    for (NSURL* url in stagingAccess.allValues) [url stopAccessingSecurityScopedResource];
    [stagingAccess removeAllObjects];
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryPrepare(void* viewHandle)
{
    NSView* view = (__bridge NSView*)viewHandle;
    if (!view.window) return;
    delivery.panel = view.window;
    ConfigureDeliveryDragActivation(view);
}

static void PlaceDeliveryWindow(NSWindow* window)
{
    if (!window || !delivery.item.button.window) return;
    window.level = NSFloatingWindowLevel;
    window.collectionBehavior = NSWindowCollectionBehaviorCanJoinAllSpaces
        | NSWindowCollectionBehaviorFullScreenAuxiliary | NSWindowCollectionBehaviorIgnoresCycle;
    window.hidesOnDeactivate = NO;
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
    [window orderFrontRegardless];
    [window invalidateShadow];
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryPlace(void* viewHandle)
{
    NSView* view = (__bridge NSView*)viewHandle;
    MacExplorerDeliveryPrepare(viewHandle);
    PlaceDeliveryWindow(view.window);
}

extern "C" __attribute__((visibility("default")))
void MacExplorerDeliveryPlaceDrop(void* viewHandle)
{
    NSView* view = (__bridge NSView*)viewHandle;
    if (!view.window) return;
    delivery.dropPanel = view.window;
    ConfigureDeliveryDrop(view);
    PlaceDeliveryWindow(view.window);
}

extern "C" __attribute__((visibility("default")))
int MacExplorerDeliveryContainsPoint(double x, double y)
{
    return delivery.panel && NSPointInRect(NSMakePoint(x, y), delivery.panel.frame);
}

extern "C" __attribute__((visibility("default")))
int MacExplorerDeliveryInspect(double* x, double* y, int* polls, int* starts, int* changeCount)
{
    NSRect button = delivery.item.button.window
        ? [delivery.item.button.window convertRectToScreen:delivery.item.button.frame] : NSZeroRect;
    *x = NSMidX(button);
    *y = NSMaxY(NSScreen.screens.firstObject.frame) - NSMidY(button);
    *polls = (int)delivery.dragPollCount;
    *starts = (int)delivery.dragStartCount;
    *changeCount = (int)delivery.dragChangeCount;
    return (delivery != nil ? 1 : 0) | (delivery.item.button.window != nil ? 2 : 0)
        | (delivery.dragTimer.valid ? 4 : 0) | (delivery.dragging ? 8 : 0);
}
