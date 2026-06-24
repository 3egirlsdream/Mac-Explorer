#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>

@interface MacExplorerDragSource : NSObject <NSDraggingSource>
@property(nonatomic) NSDragOperation operationMask;
@end

static NSMutableSet<MacExplorerDragSource*>* MacExplorerActiveDragSources()
{
    static NSMutableSet<MacExplorerDragSource*>* sources = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        sources = [NSMutableSet set];
    });
    return sources;
}

@implementation MacExplorerDragSource

- (NSDragOperation)draggingSession:(NSDraggingSession*)session
    sourceOperationMaskForDraggingContext:(NSDraggingContext)context
{
    return self.operationMask;
}

- (BOOL)ignoreModifierKeysForDraggingSession:(NSDraggingSession*)session
{
    return NO;
}

- (void)draggingSession:(NSDraggingSession*)session
           endedAtPoint:(NSPoint)screenPoint
              operation:(NSDragOperation)operation
{
    [MacExplorerActiveDragSources() removeObject:self];
}

@end

extern "C" __attribute__((visibility("default")))
int MacExplorerSetWindowFrame(
    void* nsViewHandle,
    double width,
    double height,
    int keepRightEdge,
    int keepTopEdge)
{
    if (nsViewHandle == NULL || width <= 0.0 || height <= 0.0)
        return 0;

    NSView* view = (__bridge NSView*)nsViewHandle;
    NSWindow* window = view.window;
    if (window == nil)
        return 0;

    NSRect frame = window.frame;
    if (keepRightEdge != 0)
        frame.origin.x = NSMaxX(frame) - width;
    if (keepTopEdge != 0)
        frame.origin.y = NSMaxY(frame) - height;

    frame.size = NSMakeSize(width, height);
    [window setFrame:frame display:YES];
    return 1;
}

static NSArray<NSString*>* MacExplorerParseNullSeparatedPaths(const char* bytes, int byteLength)
{
    if (bytes == NULL || byteLength <= 0)
        return @[];

    NSMutableArray<NSString*>* paths = [NSMutableArray array];
    const char* segmentStart = bytes;
    int segmentLength = 0;

    for (int i = 0; i < byteLength; ++i)
    {
        if (bytes[i] == '\0')
        {
            if (segmentLength > 0)
            {
                NSString* path = [[NSString alloc] initWithBytes:segmentStart
                                                          length:(NSUInteger)segmentLength
                                                        encoding:NSUTF8StringEncoding];
                if (path.length > 0)
                    [paths addObject:path];
            }

            segmentStart = bytes + i + 1;
            segmentLength = 0;
        }
        else
        {
            ++segmentLength;
        }
    }

    return paths;
}

static NSImage* MacExplorerIconForPath(NSString* path)
{
    NSImage* icon = [[NSWorkspace sharedWorkspace] iconForFile:path];
    if (icon == nil)
        icon = [NSImage imageNamed:NSImageNameMultipleDocuments];
    [icon setSize:NSMakeSize(64, 64)];
    return icon;
}

extern "C" __attribute__((visibility("default")))
int MacExplorerBeginFileDrag(
    void* nsViewHandle,
    double x,
    double y,
    const char* pathsUtf8,
    int pathsByteLength,
    const char* previewPathUtf8,
    int operationMask)
{
    @autoreleasepool
    {
        if (nsViewHandle == NULL)
            return 0;

        NSView* view = (__bridge NSView*)nsViewHandle;
        NSArray<NSString*>* paths = MacExplorerParseNullSeparatedPaths(pathsUtf8, pathsByteLength);
        if (paths.count == 0)
            return 0;

        NSEvent* event = [NSApp currentEvent];
        NSEventType eventType = event.type;
        if (!((eventType >= NSEventTypeLeftMouseDown && eventType <= NSEventTypeMouseExited)
              || (eventType >= NSEventTypeOtherMouseDown && eventType <= NSEventTypeOtherMouseDragged)))
        {
            NSWindow* window = view.window;
            if (window != nil)
            {
                NSRect screenRect = [window convertRectToScreen:NSMakeRect(x, y, 0.0, 0.0)];
                CGPoint point = NSPointToCGPoint(screenRect.origin);
                CGEventRef cgEvent = CGEventCreateMouseEvent(NULL, kCGEventLeftMouseDown, point, kCGMouseButtonLeft);
                event = [NSEvent eventWithCGEvent:cgEvent];
                CFRelease(cgEvent);
            }
        }

        if (event == nil)
            return 0;

        NSMutableArray<NSDraggingItem*>* draggingItems =
            [NSMutableArray arrayWithCapacity:paths.count];

        NSString* previewPath = nil;
        if (previewPathUtf8 != NULL && previewPathUtf8[0] != '\0')
            previewPath = [NSString stringWithUTF8String:previewPathUtf8];
        NSImage* previewImage = nil;
        if (previewPath.length > 0)
        {
            previewImage = [[NSImage alloc] initWithContentsOfFile:previewPath];
            [previewImage setSize:NSMakeSize(64, 64)];
        }

        for (NSUInteger index = 0; index < paths.count; ++index)
        {
            NSString* path = paths[index];
            NSURL* fileUrl = [NSURL fileURLWithPath:path];
            NSDraggingItem* item = [[NSDraggingItem alloc] initWithPasteboardWriter:fileUrl];

            NSImage* image = previewImage != nil ? previewImage : MacExplorerIconForPath(path);

            CGFloat offset = MIN(index, 3) * 4.0;
            NSRect frame = NSMakeRect(x + offset, y - offset, image.size.width, image.size.height);
            [item setDraggingFrame:frame contents:image];
            [draggingItems addObject:item];
        }

        MacExplorerDragSource* source = [MacExplorerDragSource new];
        source.operationMask = (NSDragOperation)operationMask;
        [MacExplorerActiveDragSources() addObject:source];

        [view beginDraggingSessionWithItems:draggingItems event:event source:source];
        return 1;
    }
}
