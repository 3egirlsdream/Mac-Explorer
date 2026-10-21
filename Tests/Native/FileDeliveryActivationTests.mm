#import "../../Platforms/MacOS/FileDeliveryStatusItem.mm"
#include <cassert>
#include <cstdio>

@interface MEDeliveryTestApplication : NSApplication
@property(nonatomic) NSUInteger orderingPreventions;
@end
@implementation MEDeliveryTestApplication
- (void)preventWindowOrdering { self.orderingPreventions++; }
@end

@interface MEDeliveryTestView : NSView
@property(nonatomic) NSUInteger downs;
@property(nonatomic) NSUInteger drags;
@property(nonatomic) NSUInteger ups;
@property(nonatomic) BOOL protectedBeforeDispatch;
@property(nonatomic) NSUInteger previousPreventions;
@end
@implementation MEDeliveryTestView
- (void)mouseDown:(NSEvent *)event { self.downs++; }
- (void)mouseDragged:(NSEvent *)event {
    // This is the boundary before Avalonia enters managed selection/preview code.
    self.protectedBeforeDispatch = ((MEDeliveryTestApplication *)NSApp).orderingPreventions > self.previousPreventions;
    self.drags++;
    // Model a cold renderer pumping the release while the original handler is running.
    [self mouseUp:event];
}
- (void)mouseUp:(NSEvent *)event { self.ups++; }
@end

int main() {
    @autoreleasepool {
        assert(getenv("MACEXPLORER_TEST_ROOT"));
        MEDeliveryTestApplication *app = [MEDeliveryTestApplication sharedApplication];
        [app setActivationPolicy:NSApplicationActivationPolicyProhibited];
        NSWindow *panel = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 560, 396)
            styleMask:NSWindowStyleMaskBorderless backing:NSBackingStoreBuffered defer:NO];
        panel.releasedWhenClosed = NO;
        MEDeliveryTestView *view = [MEDeliveryTestView new];
        panel.contentView = view;
        // Install before the first Show, then exercise the first and subsequent gestures.
        ConfigureDeliveryDragActivation(view);
        Class prepared = object_getClass(view);
        NSEvent *down = [NSEvent mouseEventWithType:NSEventTypeLeftMouseDown location:NSMakePoint(30, 30)
            modifierFlags:0 timestamp:1 windowNumber:panel.windowNumber context:nil eventNumber:1 clickCount:1 pressure:1];
        NSEvent *drag = [NSEvent mouseEventWithType:NSEventTypeLeftMouseDragged location:NSMakePoint(45, 45)
            modifierFlags:0 timestamp:2 windowNumber:panel.windowNumber context:nil eventNumber:2 clickCount:1 pressure:1];
        for (NSUInteger iteration = 0; iteration < 3; iteration++) {
            ConfigureDeliveryDragActivation(view);
            assert(object_getClass(view) == prepared);
            [view mouseDown:down];
            view.previousPreventions = app.orderingPreventions;
            [view mouseDragged:drag];
            assert(view.protectedBeforeDispatch);
            assert(app.orderingPreventions == iteration + 1);
            assert(view.downs == iteration + 1 && view.drags == iteration + 1 && view.ups == iteration + 1);
        }
        // A plain click keeps normal mouse-down/up behavior and does not suppress ordering.
        [view mouseDown:down];
        [view mouseUp:down];
        assert(app.orderingPreventions == 3);
        MEDeliveryTestView *ordinary = [MEDeliveryTestView new];
        ordinary.previousPreventions = app.orderingPreventions;
        [ordinary mouseDragged:drag];
        assert(!ordinary.protectedBeforeDispatch && app.orderingPreventions == 3);
        [panel close];
        puts("File delivery activation native checks passed");
    }
}
