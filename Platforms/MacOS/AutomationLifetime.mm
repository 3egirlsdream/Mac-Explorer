#import <AppKit/AppKit.h>
#import <objc/message.h>
#import <objc/runtime.h>

// Avalonia.Native 12.0.4 returns owned COM references from GetChildren/Get.
// Its two native children builders omit Release. Keep the AX tree intact while
// balancing those references. ABI slots are from the pinned IAvnAutomationPeer
// and IAvnAutomationPeerArray interfaces (IUnknown occupies slots 0–2).
static void *MEPeer(id object, const char *selector) {
    return ((void *(*)(id, SEL))objc_msgSend)(object, sel_registerName(selector));
}

static void MERelease(void *object) {
    if (object) ((uint32_t (*)(void *))(*(void ***)object)[2])(object);
}

static NSMutableArray *MEChildren(void *peer) {
    NSMutableArray *children = [NSMutableArray new];
    if (!peer) return children;
    void *array = ((void *(*)(void *))(*(void ***)peer)[10])(peer);
    if (!array) return children;
    @try {
        uint32_t count = ((uint32_t (*)(void *))(*(void ***)array)[3])(array);
        for (uint32_t index = 0; index < count; ++index) {
            void *child = nullptr;
            int32_t result = ((int32_t (*)(void *, uint32_t, void **))(*(void ***)array)[4])(array, index, &child);
            @try {
                if (result == 0 && child) {
                    id element = ((id (*)(id, SEL, void *))objc_msgSend)(
                        objc_getClass("AvnAccessibilityElement"), sel_registerName("acquire:"), child);
                    if (element) [children addObject:element];
                }
            } @finally { MERelease(child); }
        }
    } @finally { MERelease(array); }
    return children;
}

static Ivar MEElementChildren;
static Ivar MEViewChildren;

static void MERecalculateChildren(id object, SEL selector) {
    object_setIvarWithStrongDefault(object, MEElementChildren, MEChildren(MEPeer(object, "peer")));
}

static void MERecalculateViewChildren(id object, SEL selector) {
    NSWindow *window = [(NSView *)object window];
    void *peer = [window isKindOfClass:objc_getClass("AvnWindow")] ? MEPeer(window, "automationPeer") : nullptr;
    object_setIvarWithStrongDefault(object, MEViewChildren, MEChildren(peer));
}

extern "C" __attribute__((visibility("default")))
bool me_automation_fix_children() {
    static bool installed = false;
    if (installed) return true;
    if (!NSThread.isMainThread) return false;
    Class element = objc_getClass("AvnAccessibilityElement");
    Class view = objc_getClass("AvnView");
    Method elementMethod = class_getInstanceMethod(element, sel_registerName("recalculateChildren"));
    Method viewMethod = class_getInstanceMethod(view, sel_registerName("recalculateAccessibiltyChildren"));
    MEElementChildren = class_getInstanceVariable(element, "_children");
    MEViewChildren = class_getInstanceVariable(view, "_accessibilityChildren");
    if (!elementMethod || !viewMethod || !MEElementChildren || !MEViewChildren) return false;
    method_setImplementation(elementMethod, (IMP)MERecalculateChildren);
    method_setImplementation(viewMethod, (IMP)MERecalculateViewChildren);
    installed = true;
    return true;
}
