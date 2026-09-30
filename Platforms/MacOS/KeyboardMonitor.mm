#import <AppKit/AppKit.h>

typedef int (*KeyboardCallback)(int kind, unsigned short keyCode, int modifiers, int repeat);

@interface MacExplorerKeyboardMonitor : NSObject
@property(nonatomic, weak) NSWindow* window;
@property(nonatomic, strong) id monitor;
@property(nonatomic, strong) id windowObserver;
@property(nonatomic, strong) id appObserver;
@property(nonatomic) KeyboardCallback callback;
@end

@implementation MacExplorerKeyboardMonitor
- (void)dealloc
{
    if (self.monitor) [NSEvent removeMonitor:self.monitor];
    if (self.windowObserver) [NSNotificationCenter.defaultCenter removeObserver:self.windowObserver];
    if (self.appObserver) [NSNotificationCenter.defaultCenter removeObserver:self.appObserver];
}
@end

static int Modifiers(NSEventModifierFlags flags)
{
    return ((flags & NSEventModifierFlagCommand) ? 1 : 0)
        | ((flags & NSEventModifierFlagShift) ? 2 : 0)
        | ((flags & NSEventModifierFlagOption) ? 4 : 0)
        | ((flags & NSEventModifierFlagControl) ? 8 : 0);
}

extern "C" __attribute__((visibility("default")))
void* MacExplorerKeyboardCreate(void* viewHandle, KeyboardCallback callback)
{
    NSView* view = (__bridge NSView*)viewHandle;
    if (!view.window) return nullptr;
    MacExplorerKeyboardMonitor* observer = [MacExplorerKeyboardMonitor new];
    observer.window = view.window;
    observer.callback = callback;
    __weak MacExplorerKeyboardMonitor* weakObserver = observer;
    NSEventMask mask = NSEventMaskFlagsChanged | NSEventMaskKeyDown | NSEventMaskKeyUp
        | NSEventMaskLeftMouseDown | NSEventMaskRightMouseDown | NSEventMaskOtherMouseDown;
    observer.monitor = [NSEvent addLocalMonitorForEventsMatchingMask:mask handler:^NSEvent*(NSEvent* event) {
        MacExplorerKeyboardMonitor* current = weakObserver;
        if (!current.callback || !NSApp.active || NSApp.keyWindow != current.window) return event;
        int kind = event.type == NSEventTypeFlagsChanged ? 0 : event.type == NSEventTypeKeyDown ? 1
            : event.type == NSEventTypeKeyUp ? 4 : 2;
        int handled = current.callback(kind, kind == 2 ? 0 : event.keyCode, Modifiers(event.modifierFlags),
            event.type == NSEventTypeKeyDown && event.isARepeat ? 1 : 0);
        // Consume handled recording, tab navigation and repeats to avoid a second dispatch.
        return kind == 1 && handled ? nil : event;
    }];
    observer.windowObserver = [NSNotificationCenter.defaultCenter addObserverForName:NSWindowDidResignKeyNotification
        object:view.window queue:nil usingBlock:^(NSNotification* notification) {
            MacExplorerKeyboardMonitor* current = weakObserver;
            if (current.callback) current.callback(3, 0, 0, 0);
        }];
    observer.appObserver = [NSNotificationCenter.defaultCenter addObserverForName:NSApplicationWillResignActiveNotification
        object:NSApp queue:nil usingBlock:^(NSNotification* notification) {
            MacExplorerKeyboardMonitor* current = weakObserver;
            if (current.callback) current.callback(3, 0, 0, 0);
        }];
    return (__bridge_retained void*)observer;
}

extern "C" __attribute__((visibility("default")))
void MacExplorerKeyboardDestroy(void* handle)
{
    if (!handle) return;
    MacExplorerKeyboardMonitor* observer = (__bridge_transfer MacExplorerKeyboardMonitor*)handle;
    observer.callback = nullptr;
}
