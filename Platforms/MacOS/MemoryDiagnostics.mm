#include <mach/mach.h>
#include <stdint.h>
#include <string.h>
#import <AppKit/AppKit.h>
#import <QuartzCore/QuartzCore.h>

extern "C" __attribute__((visibility("default")))
int64_t me_memory_footprint() {
    task_vm_info_data_t info = {};
    mach_msg_type_number_t count = TASK_VM_INFO_COUNT;
    if (task_info(mach_task_self(), TASK_VM_INFO, (task_info_t)&info, &count) != KERN_SUCCESS
        || count < TASK_VM_INFO_REV1_COUNT) return -1;
    return (int64_t)info.phys_footprint;
}

static NSArray *DiagnosticSize(NSSize size) { return @[@(size.width), @(size.height)]; }
static NSArray *DiagnosticRect(NSRect rect) {
    return @[@(rect.origin.x), @(rect.origin.y), @(rect.size.width), @(rect.size.height)];
}
static NSArray *DiagnosticTransform(CATransform3D t) {
    return @[@(t.m11), @(t.m12), @(t.m13), @(t.m14), @(t.m21), @(t.m22), @(t.m23), @(t.m24),
        @(t.m31), @(t.m32), @(t.m33), @(t.m34), @(t.m41), @(t.m42), @(t.m43), @(t.m44)];
}

// Read-only: never request a drawable, render, flush a transaction, or repair geometry here.
// A diagnostic repaint could hide the intermittent resize race we are trying to observe.
extern "C" __attribute__((visibility("default")))
int me_window_render_snapshot(void *viewPointer, uint8_t *buffer, int capacity) {
    if (!viewPointer || !buffer || capacity <= 0 || !NSThread.isMainThread) return 0;
    @autoreleasepool {
        @try {
            NSView *view = (__bridge NSView *)viewPointer;
            NSWindow *window = view.window;
            if (!window) return 0;
            CALayer *layer = view.layer;
            CALayer *presentation = layer.presentationLayer;
            CAMetalLayer *metal = [layer isKindOfClass:CAMetalLayer.class] ? (CAMetalLayer *)layer : nil;
            NSDictionary *snapshot = @{
                @"windowNumber": @(window.windowNumber),
                @"windowFrame": DiagnosticRect(window.frame),
                @"windowScale": @(window.backingScaleFactor),
                @"screenScale": @(window.screen.backingScaleFactor),
                @"screenNumber": window.screen.deviceDescription[@"NSScreenNumber"] ?: NSNull.null,
                @"screenFrame": DiagnosticRect(window.screen.frame),
                @"contentSize": DiagnosticSize(window.contentView.bounds.size),
                @"viewSize": DiagnosticSize(view.frame.size),
                @"viewBoundsSize": DiagnosticSize(view.bounds.size),
                @"backingSize": DiagnosticSize([view convertSizeToBacking:view.bounds.size]),
                @"visible": @(window.isVisible), @"miniaturized": @(window.isMiniaturized),
                @"occluded": @(!(window.occlusionState & NSWindowOcclusionStateVisible)),
                @"zoomed": @(window.isZoomed), @"liveResize": @(view.inLiveResize),
                @"styleMask": @(window.styleMask),
                @"layerKind": metal ? @"Metal" : layer ? @"CALayer" : @"none",
                @"layerSize": DiagnosticSize(layer.bounds.size),
                @"contentsScale": @(layer.contentsScale),
                @"contentsGravity": layer.contentsGravity ?: NSNull.null,
                @"contentsRect": DiagnosticRect(layer.contentsRect),
                @"layerTransform": layer ? DiagnosticTransform(layer.transform) : NSNull.null,
                @"sublayerTransform": layer ? DiagnosticTransform(layer.sublayerTransform) : NSNull.null,
                @"animationCount": @(layer.animationKeys.count),
                @"presentationBounds": presentation ? DiagnosticRect(presentation.bounds) : NSNull.null,
                @"presentationTransform": presentation ? DiagnosticTransform(presentation.transform) : NSNull.null,
                @"drawableSize": metal ? DiagnosticSize(metal.drawableSize) : NSNull.null,
                @"presentsWithTransaction": metal ? @(metal.presentsWithTransaction) : NSNull.null
            };
            NSData *json = [NSJSONSerialization dataWithJSONObject:snapshot options:0 error:nil];
            if (!json || json.length > (NSUInteger)capacity) return -1;
            memcpy(buffer, json.bytes, json.length);
            return (int)json.length;
        } @catch (NSException *exception) { return -1; }
    }
}
