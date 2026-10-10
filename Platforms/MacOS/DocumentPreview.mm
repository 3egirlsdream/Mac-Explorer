#import <AppKit/AppKit.h>
#import <Quartz/Quartz.h>

// Owned by NativeControlHost; all calls and callbacks run on the AppKit main thread.
typedef void (*DocumentKeyCallback)(unsigned short key);
@interface MEDocumentPreview : NSView
@property(nonatomic, strong) PDFView *pdf;
@property(nonatomic, strong) QLPreviewView *quickLook;
@property(nonatomic, strong) id keyMonitor;
@property(nonatomic) DocumentKeyCallback callback;
@property(nonatomic) BOOL navigationKeys;
@property(nonatomic) CGFloat documentWidth;
- (void)clearDocument;
- (void)fitWidth;
@end
@implementation MEDocumentPreview
- (void)fitWidth {
    if (!self.pdf.document || self.bounds.size.width <= 16) return;
    if (self.documentWidth > 0)
        self.pdf.scaleFactor = MAX(0.01, (self.bounds.size.width - 16) / self.documentWidth);
}
- (void)setFrameSize:(NSSize)size {
    BOOL changedWidth = fabs(size.width - self.frame.size.width) > 0.5;
    PDFDestination *position = self.pdf.currentDestination;
    [super setFrameSize:size];
    self.pdf.frame = self.bounds;
    self.quickLook.frame = self.bounds;
    if (changedWidth) {
        [self fitWidth];
        if (position) [self.pdf goToDestination:position];
    }
}
- (void)clearDocument {
    NSResponder *responder = self.window.firstResponder;
    if ([responder isKindOfClass:NSView.class] && [(NSView *)responder isDescendantOf:self])
        [self.window makeFirstResponder:self.window.contentView];
    self.documentWidth = 0;
    self.pdf.document = nil;
    [self.pdf removeFromSuperview]; self.pdf = nil;
    self.quickLook.previewItem = nil;
    [self.quickLook close];
    [self.quickLook removeFromSuperview]; self.quickLook = nil;
}
- (void)dealloc {
    if (self.keyMonitor) [NSEvent removeMonitor:self.keyMonitor];
    [self clearDocument];
}
@end

extern "C" {
void *me_document_preview_create(DocumentKeyCallback callback, bool navigationKeys) {
    MEDocumentPreview *view = [[MEDocumentPreview alloc] initWithFrame:NSMakeRect(0, 0, 640, 480)];
    view.wantsLayer = YES;
    view.layer.masksToBounds = YES;
    view.callback = callback;
    view.navigationKeys = navigationKeys;
    __weak MEDocumentPreview *weakView = view;
    view.keyMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskKeyDown handler:^NSEvent *(NSEvent *event) {
        MEDocumentPreview *current = weakView;
        if (!current || current.hiddenOrHasHiddenAncestor || !current.callback || event.window != current.window) return event;
        NSResponder *responder = current.window.firstResponder;
        if (![responder isKindOfClass:NSView.class] || ![(NSView *)responder isDescendantOf:current]) return event;
        NSEventModifierFlags modifiers = event.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagOption | NSEventModifierFlagShift);
        if (modifiers != 0) return event;
        unsigned short key = event.keyCode;
        if (key == 53 || key == 36 || key == 76 || (current.navigationKeys && (key == 49 || key == 51))) {
            current.callback(key);
            return nil;
        }
        return event;
    }];
    return (__bridge_retained void *)view;
}
// 0: unreadable, 1: loaded (Quick Look completes asynchronously), 2: locked PDF.
int me_document_preview_load(void *handle, const char *path, bool pdf) {
    @autoreleasepool {
        MEDocumentPreview *view = (__bridge MEDocumentPreview *)handle;
        [view clearDocument];
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        if (![NSFileManager.defaultManager isReadableFileAtPath:url.path]) return 0;
        if (pdf) {
            PDFDocument *document = [[PDFDocument alloc] initWithURL:url];
            if (!document) return 0;
            if (document.isLocked) return 2;
            if (document.pageCount == 0) return 0;
            view.pdf = [[PDFView alloc] initWithFrame:view.bounds];
            view.pdf.displayMode = kPDFDisplaySinglePageContinuous;
            view.pdf.displayDirection = kPDFDisplayDirectionVertical;
            view.pdf.displaysPageBreaks = YES;
            view.pdf.pageBreakMargins = NSEdgeInsetsMake(4, 0, 4, 0);
            view.pdf.autoScales = NO;
            view.pdf.backgroundColor = NSColor.windowBackgroundColor;
            // Cache the widest page once; resizing must not enumerate a large document.
            for (NSUInteger i = 0; i < document.pageCount; i++) {
                PDFPage *page = [document pageAtIndex:i];
                NSRect box = [page boundsForBox:view.pdf.displayBox];
                CGFloat width = page.rotation % 180 == 0 ? box.size.width : box.size.height;
                view.documentWidth = MAX(view.documentWidth, width);
            }
            view.pdf.document = document;
            [view addSubview:view.pdf];
            [view fitWidth];
            PDFPage *firstPage = [document pageAtIndex:0];
            NSRect firstBounds = [firstPage boundsForBox:view.pdf.displayBox];
            [view.pdf goToDestination:[[PDFDestination alloc] initWithPage:firstPage
                atPoint:NSMakePoint(NSMinX(firstBounds), NSMaxY(firstBounds))]];
        } else {
            view.quickLook = [[QLPreviewView alloc] initWithFrame:view.bounds style:QLPreviewViewStyleNormal];
            if (!view.quickLook) return 0;
            view.quickLook.shouldCloseWithWindow = NO;
            view.quickLook.autostarts = NO;
            [view addSubview:view.quickLook];
            view.quickLook.previewItem = url;
        }
        return 1;
    }
}
void me_document_preview_restore_focus(void *handle, void *rootHandle) {
    MEDocumentPreview *view = (__bridge MEDocumentPreview *)handle;
    NSView *root = (__bridge NSView *)rootHandle;
    NSResponder *responder = view.window.firstResponder;
    if (view.window && view.window == root.window && [responder isKindOfClass:NSView.class]
        && [(NSView *)responder isDescendantOf:view])
        [view.window makeFirstResponder:root];
}
void me_document_preview_appearance(void *handle, bool dark, double radius) {
    MEDocumentPreview *view = (__bridge MEDocumentPreview *)handle;
    view.appearance = [NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua];
    view.layer.cornerRadius = MAX(0, radius);
    [view.appearance performAsCurrentDrawingAppearance:^{ view.pdf.backgroundColor = NSColor.windowBackgroundColor; }];
}
void me_document_preview_clear(void *handle) { [(__bridge MEDocumentPreview *)handle clearDocument]; }
void me_document_preview_destroy(void *handle) {
    MEDocumentPreview *view = (__bridge_transfer MEDocumentPreview *)handle;
    view.callback = nullptr;
    [view clearDocument];
    [view removeFromSuperview];
}
}
