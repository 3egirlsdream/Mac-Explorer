#import <AppKit/AppKit.h>
#import <Foundation/Foundation.h>

typedef void (*MacExplorerDragCallback)(void*, int, double, double, int);

@interface MacExplorerDragSource : NSObject <NSDraggingSource>
@property(nonatomic) NSDragOperation operationMask;
@property(nonatomic) void* callbackContext;
@property(nonatomic) MacExplorerDragCallback callback;
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

- (void)draggingSession:(NSDraggingSession*)session willBeginAtPoint:(NSPoint)point
{
    if (self.callback) self.callback(self.callbackContext, 0, point.x, point.y, 0);
}

- (void)draggingSession:(NSDraggingSession*)session movedToPoint:(NSPoint)point
{
    if (self.callback) self.callback(self.callbackContext, 1, point.x, point.y, 0);
}

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
    if (self.callback) self.callback(self.callbackContext, 2, screenPoint.x, screenPoint.y, (int)operation);
    [MacExplorerActiveDragSources() removeObject:self];
}

@end

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

static NSImage* MacExplorerCreateDragImage(const unsigned char* previewPixels,
    int previewWidth, int previewHeight, int previewStride)
{
    if (previewPixels == NULL || previewWidth <= 0 || previewHeight <= 0
        || previewStride < previewWidth * 4)
        return nil;
    NSBitmapImageRep* representation = [[NSBitmapImageRep alloc]
        initWithBitmapDataPlanes:NULL pixelsWide:previewWidth pixelsHigh:previewHeight
        bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO
        colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:previewWidth * 4 bitsPerPixel:32];
    if (representation == nil) return nil;
    for (int row = 0; row < previewHeight; ++row)
        memcpy(representation.bitmapData + row * representation.bytesPerRow,
               previewPixels + row * previewStride, previewWidth * 4);
    NSImage* previewImage = [[NSImage alloc] initWithSize:NSMakeSize(64, 64)];
    [previewImage addRepresentation:representation];

    return previewImage;
}

extern "C" __attribute__((visibility("default")))
void MacExplorerPrepareFileDragPixels(const unsigned char* pixels, int width, int height, int stride)
{
    @autoreleasepool
    {
        NSImage* image = MacExplorerCreateDragImage(pixels, width, height, stride);
        NSDraggingItem* item = [[NSDraggingItem alloc]
            initWithPasteboardWriter:[NSURL fileURLWithPath:@"/" isDirectory:YES]];
        [item setDraggingFrame:NSMakeRect(0, 0, 64, 64) contents:image];
        (void)[MacExplorerDragSource new];
        (void)MacExplorerActiveDragSources();
        // Construct resources only: do not start a session or change a pasteboard.
    }
}

extern "C" __attribute__((visibility("default")))
int MacExplorerBeginFileDragPixels(
    void* nsViewHandle,
    double x,
    double y,
    const char* pathsUtf8,
    int pathsByteLength,
    const unsigned char* previewPixels,
    int previewWidth,
    int previewHeight,
    int previewStride,
    int operationMask, void* callbackContext, MacExplorerDragCallback callback)
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

        NSImage* previewImage = MacExplorerCreateDragImage(previewPixels, previewWidth, previewHeight, previewStride);
        if (previewImage == nil) return 0;

        for (NSUInteger index = 0; index < paths.count; ++index)
        {
            NSString* path = paths[index];
            NSURL* fileUrl = [NSURL fileURLWithPath:path isDirectory:[path hasSuffix:@"/"]];
            NSDraggingItem* item = [[NSDraggingItem alloc] initWithPasteboardWriter:fileUrl];

            NSImage* image = previewImage;

            CGFloat offset = MIN(index, 3) * 4.0;
            NSRect frame = NSMakeRect(x + offset, y - offset, image.size.width, image.size.height);
            [item setDraggingFrame:frame contents:image];
            [draggingItems addObject:item];
        }

        MacExplorerDragSource* source = [MacExplorerDragSource new];
        source.operationMask = (NSDragOperation)operationMask;
        source.callbackContext = callbackContext;
        source.callback = callback;
        [MacExplorerActiveDragSources() addObject:source];

        [view beginDraggingSessionWithItems:draggingItems event:event source:source];
        return 1;
    }
}
