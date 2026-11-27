#import <AppKit/AppKit.h>

// Establish the AppKit sandbox identity before entering the NativeAOT runtime.
extern "C" __attribute__((constructor, used))
void InitializeStoreApplication() {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        [NSApp finishLaunching];
    }
}
