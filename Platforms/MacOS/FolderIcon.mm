#import <AppKit/AppKit.h>
#include <cstdlib>
#include <cstring>

// Load installed Finder artwork at runtime instead of redistributing a system asset.
extern "C" char *me_finder_folder_icon(int size) {
    @autoreleasepool {
        if (size <= 0) return nullptr;
        NSImage *icon = [[NSImage alloc] initWithContentsOfFile:
            @"/System/Library/CoreServices/CoreTypes.bundle/Contents/Resources/GenericFolderIcon.icns"];
        if (!icon) icon = [NSImage imageNamed:NSImageNameFolder];
        if (!icon) return nullptr;
        NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nil pixelsWide:size pixelsHigh:size
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
        [NSGraphicsContext saveGraphicsState];
        NSGraphicsContext.currentContext = [NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap];
        [icon drawInRect:NSMakeRect(0, 0, size, size) fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1.0];
        [NSGraphicsContext restoreGraphicsState];
        NSString *png = [[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] base64EncodedStringWithOptions:0];
        return png ? strdup(png.UTF8String) : nullptr;
    }
}

extern "C" void me_free_folder_icon(char *value) { free(value); }
