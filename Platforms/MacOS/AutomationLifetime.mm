#import <AppKit/AppKit.h>
#import <objc/message.h>
#import <objc/runtime.h>

// Avalonia.Native 12.0.4 returns owned COM references from its AX getters.
// Balance the references omitted by its native AX traversal. Keep the AX tree
// intact. ABI slots are from the pinned IAvnAutomationPeer / IAvnTopLevel
// and IAvnAutomationPeerArray interfaces (IUnknown occupies slots 0–2).
static void *MEPeer(id object, const char *selector) {
    return ((void *(*)(id, SEL))objc_msgSend)(object, sel_registerName(selector));
}

static void MERelease(void *object) {
    if (object) ((uint32_t (*)(void *))(*(void ***)object)[2])(object);
}

static IMP MEOriginalAcquire;

static NSView *MERootView(void *peer) {
    void *window = ((void *(*)(void *))(*(void ***)peer)[27])(peer);
    if (!window) return nil;
    @try {
        void *view = nullptr;
        int32_t result = ((int32_t (*)(void *, void **))(*(void ***)window)[12])(window, &view);
        return result == 0 ? (__bridge NSView *)view : nil;
    } @finally { MERelease(window); }
}

static id MEAcquire(id type, SEL selector, void *peer) {
    if (!peer) return nil;
    void *node = ((void *(*)(void *))(*(void ***)peer)[3])(peer);
    if (node) {
        @try {
            return ((id (*)(id, SEL, void *))MEOriginalAcquire)(type, selector, peer);
        } @finally {
            // Our GetNode and the original acquire:'s GetNode each return an
            // owned reference to the same existing node.
            MERelease(node);
            MERelease(node);
        }
    }
    if (((int32_t (*)(void *))(*(void ***)peer)[26])(peer))
        return [MERootView(peer) window];
    return ((id (*)(id, SEL, void *))MEOriginalAcquire)(type, selector, peer);
}

static id MERelatedElement(id object, SEL selector) {
    void *peer = MEPeer(object, "peer");
    if (!peer) return nil;
    int slot = selector == sel_registerName("accessibilityParent") ? 14
        : selector == sel_registerName("accessibilityWindow") ? 15 : 23;
    void *related = ((void *(*)(void *))(*(void ***)peer)[slot])(peer);
    if (!related)
        return slot == 14 ? [NSApplication sharedApplication] : nil;
    @try {
        if (slot == 14 && ((int32_t (*)(void *))(*(void ***)related)[26])(related))
            return MERootView(related);
        return MEAcquire(objc_getClass("AvnAccessibilityElement"), sel_registerName("acquire:"), related);
    } @finally { MERelease(related); }
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
    Method acquireMethod = class_getClassMethod(element, sel_registerName("acquire:"));
    Method parentMethod = class_getInstanceMethod(element, sel_registerName("accessibilityParent"));
    Method windowMethod = class_getInstanceMethod(element, sel_registerName("accessibilityWindow"));
    Method rootMethod = class_getInstanceMethod(element, sel_registerName("accessibilityTopLevelUIElement"));
    MEElementChildren = class_getInstanceVariable(element, "_children");
    MEViewChildren = class_getInstanceVariable(view, "_accessibilityChildren");
    if (!elementMethod || !viewMethod || !acquireMethod || !parentMethod || !windowMethod || !rootMethod
        || !MEElementChildren || !MEViewChildren) return false;
    MEOriginalAcquire = method_setImplementation(acquireMethod, (IMP)MEAcquire);
    method_setImplementation(parentMethod, (IMP)MERelatedElement);
    method_setImplementation(windowMethod, (IMP)MERelatedElement);
    method_setImplementation(rootMethod, (IMP)MERelatedElement);
    method_setImplementation(elementMethod, (IMP)MERecalculateChildren);
    method_setImplementation(viewMethod, (IMP)MERecalculateViewChildren);
    installed = true;
    return true;
}
