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


@interface MEDeliveryTestDraggingInfo : NSObject
@property(nonatomic, strong) NSPasteboard* draggingPasteboard;
@property(nonatomic) NSDragOperation draggingSourceOperationMask;
@end
@implementation MEDeliveryTestDraggingInfo
@end
static NSMutableArray<NSNumber*>* actions;
static NSArray<NSString*>* receivedPaths;
static void OnDeliveryAction(int action) { [actions addObject:@(action)]; }
static int OnDeliveryDrop(const char* payload)
{
    NSData* data = [[NSString stringWithUTF8String:payload] dataUsingEncoding:NSUTF8StringEncoding];
    receivedPaths = [NSJSONSerialization JSONObjectWithData:data options:0 error:nil];
    return receivedPaths.count > 0;
}

static void CheckStagingDrag()
{
    delivery = [FileDeliveryStatusItem new];
    delivery.callback = OnDeliveryAction;
    delivery.dropCallback = OnDeliveryDrop;
    actions = [NSMutableArray new];
    delivery.dragChangeCount = 10;
    // Old pasteboard data and non-file drags do not open the shelf.
    [delivery observeDragPressed:YES changeCount:10 hasFiles:YES cancelled:NO];
    [delivery observeDragPressed:YES changeCount:11 hasFiles:NO cancelled:NO];
    [delivery observeDragPressed:NO changeCount:12 hasFiles:YES cancelled:NO];
    assert(!delivery.dragging && actions.count == 0);
    [delivery observeDragPressed:YES changeCount:13 hasFiles:YES cancelled:NO];
    [delivery observeDragPressed:YES changeCount:13 hasFiles:YES cancelled:NO];
    assert(delivery.dragging && [actions isEqualToArray:@[@2]]);
    [delivery observeDragPressed:YES changeCount:13 hasFiles:YES cancelled:YES];
    [delivery observeDragPressed:YES changeCount:14 hasFiles:YES cancelled:NO];
    assert((!delivery.dragging && [actions isEqualToArray:@[@2, @3]]));
    [delivery observeDragPressed:NO changeCount:14 hasFiles:YES cancelled:NO];
    MacExplorerDeliveryDragStarted();
    [delivery observeDragPressed:NO changeCount:14 hasFiles:YES cancelled:NO];
    assert(delivery.dragging);
    MacExplorerDeliveryDragEnded();
    assert(!delivery.dragging);
    // A late button-state sample after completion must not reopen the old drag.
    NSUInteger completedActions = actions.count;
    [delivery observeDragPressed:YES changeCount:delivery.dragChangeCount hasFiles:YES cancelled:NO];
    assert(!delivery.dragging && actions.count == completedActions);

    NSPasteboard* pasteboard = [NSPasteboard pasteboardWithUniqueName];
    MEDeliveryTestDraggingInfo* info = [MEDeliveryTestDraggingInfo new];
    info.draggingPasteboard = pasteboard;
    info.draggingSourceOperationMask = NSDragOperationCopy | NSDragOperationMove;
    NSString* root = [NSString stringWithUTF8String:getenv("MACEXPLORER_TEST_ROOT")];
    NSString* file = [root stringByAppendingPathComponent:@"原文件.txt"];
    NSString* folder = [root stringByAppendingPathComponent:@"folder"];
    [@"unchanged" writeToFile:file atomically:YES encoding:NSUTF8StringEncoding error:nil];
    [NSFileManager.defaultManager createDirectoryAtPath:folder withIntermediateDirectories:YES attributes:nil error:nil];
    [pasteboard writeObjects:@[[NSURL fileURLWithPath:file], [NSURL fileURLWithPath:folder]]];
    NSView* dropView = [NSView new];
    ConfigureDeliveryDrop(dropView);
    assert([dropView draggingEntered:(id<NSDraggingInfo>)info] == NSDragOperationCopy);
    assert([dropView prepareForDragOperation:(id<NSDraggingInfo>)info]);
    assert([dropView performDragOperation:(id<NSDraggingInfo>)info]);
    assert(([receivedPaths isEqualToArray:@[file.stringByStandardizingPath, folder.stringByStandardizingPath]]));
    assert([[NSString stringWithContentsOfFile:file encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"unchanged"]);
    info.draggingSourceOperationMask = NSDragOperationMove;
    assert(DeliveryDropOperation((id<NSDraggingInfo>)info) == NSDragOperationNone);
    info.draggingSourceOperationMask = NSDragOperationCopy;
    [pasteboard clearContents];
    [pasteboard setString:@"text" forType:NSPasteboardTypeString];
    assert(!DeliveryDropPrepare(dropView, NULL, (id<NSDraggingInfo>)info));
    assert(!DeliveryDropPerform(dropView, NULL, (id<NSDraggingInfo>)info));
    [pasteboard releaseGlobally];
    MacExplorerDeliveryClearStagingAccess();
    MacExplorerDeliveryDestroy();
}

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
        CheckStagingDrag();
        puts("File delivery activation native checks passed");
    }
}
