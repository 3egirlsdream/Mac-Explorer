// Compile: clang++ -fobjc-arc Tools/Testing/DocumentPreviewSmoke.mm -framework AppKit -framework Quartz -framework WebKit -o /tmp/fkfinder-document-smoke
// Run: bash Tools/Testing/run-isolated.sh /tmp/fkfinder-document-smoke <built native dylib>
#import <AppKit/AppKit.h>
#import <Quartz/Quartz.h>
#import <WebKit/WebKit.h>
#include <dlfcn.h>
#include <cassert>

@interface MESmokeRoot : NSView
@end
@implementation MESmokeRoot
- (BOOL)acceptsFirstResponder { return YES; }
@end

static void Pump() {
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:0.15];
    while ([deadline timeIntervalSinceNow] > 0)
        [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:deadline];
}
static void Key(unsigned short code) { }
static NSView *FindView(NSView *view, Class type) {
    if ([view isKindOfClass:type]) return view;
    for (NSView *child in view.subviews) { NSView *found = FindView(child, type); if (found) return found; }
    return nil;
}
static id Evaluate(WKWebView *web, NSString *script) {
    __block BOOL done = NO; __block id result; __block NSError *failure;
    [web evaluateJavaScript:script completionHandler:^(id value, NSError *error) { result = value; failure = error; done = YES; }];
    for (int i = 0; i < 40 && !done; i++) Pump();
    assert(done && !failure); return result;
}
int main(int argc, char **argv) {
    @autoreleasepool {
        assert((argc == 2 || argc == 3) && getenv("MACEXPLORER_TEST_ROOT"));
        [NSApplication sharedApplication];
        NSString *root = [NSString stringWithUTF8String:getenv("MACEXPLORER_TEST_ROOT")];
        NSString *path = [root stringByAppendingPathComponent:@"continuous.pdf"];
        NSURL *url = [NSURL fileURLWithPath:path];
        CGRect portrait = CGRectMake(0, 0, 612, 792);
        CGContextRef context = CGPDFContextCreateWithURL((__bridge CFURLRef)url, &portrait, nullptr);
        for (int index = 0; index < 3; index++) {
            CGRect page = index == 1 ? CGRectMake(0, 0, 792, 612) : portrait;
            NSData *box = [NSData dataWithBytes:&page length:sizeof(page)];
            CGPDFContextBeginPage(context, (__bridge CFDictionaryRef)@{(__bridge NSString *)kCGPDFContextMediaBox:box});
            [NSGraphicsContext saveGraphicsState];
            NSGraphicsContext.currentContext = [NSGraphicsContext graphicsContextWithCGContext:context flipped:NO];
            [[NSString stringWithFormat:@"Preview page %d", index + 1] drawAtPoint:NSMakePoint(40, 500)
                withAttributes:@{NSFontAttributeName: [NSFont systemFontOfSize:36]}];
            [NSGraphicsContext restoreGraphicsState];
            CGPDFContextEndPage(context);
        }
        CGPDFContextClose(context);
        CGContextRelease(context);
        void *library = dlopen(argv[1], RTLD_NOW);
        assert(library);
        auto create = (void *(*)(void (*)(unsigned short), bool))dlsym(library, "me_document_preview_create");
        auto load = (int (*)(void *, const char *, bool))dlsym(library, "me_document_preview_load");
        auto restoreFocus = (void (*)(void *, void *))dlsym(library, "me_document_preview_restore_focus");
        auto appearance = (void (*)(void *, bool, double))dlsym(library, "me_document_preview_appearance");
        auto scrollbars = (void (*)(void *, double, uint32_t, uint32_t, uint32_t))dlsym(library, "me_document_preview_scrollbars");
        auto clear = (void (*)(void *))dlsym(library, "me_document_preview_clear");
        auto destroy = (void (*)(void *))dlsym(library, "me_document_preview_destroy");
        assert(create && load && restoreFocus && appearance && scrollbars && clear && destroy);
        void *handle = create(Key, true);
        scrollbars(handle, 8, 0xffd9d9d9, 0xffc9c9c9, 0xffb8b8b8);
        NSView *host = (__bridge NSView *)handle;
        NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(50, 50, 700, 500)
            styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
        MESmokeRoot *rootView = [[MESmokeRoot alloc] initWithFrame:NSMakeRect(0, 0, 700, 500)];
        window.contentView = rootView;
        host.frame = rootView.bounds;
        host.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
        [rootView addSubview:host];
        [window makeKeyAndOrderFront:nil];
        assert(load(handle, path.fileSystemRepresentation, true) == 1);
        Pump();
        PDFView *pdf = (PDFView *)host.subviews.firstObject;
        assert([pdf isKindOfClass:PDFView.class] && pdf.document.pageCount == 3);
        assert(pdf.displayMode == kPDFDisplaySinglePageContinuous && pdf.displayDirection == kPDFDisplayDirectionVertical);
        NSScrollView *pdfScroll = (NSScrollView *)FindView(pdf, NSScrollView.class);
        assert(pdfScroll && pdfScroll.borderType == NSNoBorder);
        assert(pdfScroll.verticalScroller.frame.size.width <= 8);
        appearance(handle, true, 10);
        assert(([[host.effectiveAppearance bestMatchFromAppearancesWithNames:@[NSAppearanceNameAqua, NSAppearanceNameDarkAqua]] isEqualToString:NSAppearanceNameDarkAqua]));
        assert(host.layer.cornerRadius == 10);
        appearance(handle, false, 10);
        assert([window makeFirstResponder:pdf]);
        restoreFocus(handle, (__bridge void *)rootView);
        assert(window.firstResponder == rootView);
        CGFloat originalScale = pdf.scaleFactor;
        [pdf goToPage:[pdf.document pageAtIndex:2]];
        Pump();
        assert([pdf.document indexForPage:pdf.currentPage] == 2);
        [window setContentSize:NSMakeSize(420, 500)];
        Pump();
        assert(pdf.scaleFactor < originalScale);
        assert(fabs(pdf.scaleFactor * 792 - (host.bounds.size.width - 16)) < 2);
        NSString *locked = [root stringByAppendingPathComponent:@"locked.pdf"];
        [pdf.document writeToURL:[NSURL fileURLWithPath:locked] withOptions:@{PDFDocumentUserPasswordOption:@"secret", PDFDocumentOwnerPasswordOption:@"owner"}];
        assert(load(handle, locked.fileSystemRepresentation, true) == 2);
        NSString *bad = [root stringByAppendingPathComponent:@"corrupt.pdf"];
        [@"not a pdf" writeToFile:bad atomically:YES encoding:NSUTF8StringEncoding error:nil];
        assert(load(handle, bad.fileSystemRepresentation, true) == 0);
        assert(load(handle, path.fileSystemRepresentation, true) == 1);
        NSString *rtf = [root stringByAppendingPathComponent:@"multi-page.rtf"];
        [@"{\\rtf1\\ansi Preview page one\\page Preview page two\\page Preview page three}" writeToFile:rtf atomically:YES encoding:NSUTF8StringEncoding error:nil];
        assert(load(handle, rtf.fileSystemRepresentation, false) == 1);
        Pump();
        assert([host.subviews.firstObject isKindOfClass:QLPreviewView.class]);
        if (argc == 3) {
            NSString *office = [root stringByAppendingPathComponent:@"office.docx"];
            assert([NSFileManager.defaultManager copyItemAtPath:[NSString stringWithUTF8String:argv[2]] toPath:office error:nil]);
            [window setContentSize:NSMakeSize(1008, 510)];
            assert(load(handle, office.fileSystemRepresentation, false) == 1);
            WKWebView *web = nil;
            for (int i = 0; i < 40 && !(web = (WKWebView *)FindView(host, WKWebView.class)); i++) Pump();
            assert(web);
            for (int i = 0; i < 40 && web.isLoading; i++) Pump();
            Pump();
            assert(NSEqualRects(web.frame, web.superview.bounds));
            NSDictionary *state = Evaluate(web, @"(()=>{const v=document.getElementById('macexplorer-document-scroll'),p=v.firstElementChild.getBoundingClientRect();return {width:innerWidth,viewport:v.clientWidth,paperLeft:p.left,paperWidth:p.width,scrollbar:getComputedStyle(v,'::-webkit-scrollbar').width,thumb:getComputedStyle(v,'::-webkit-scrollbar-thumb').backgroundColor,root:document.documentElement.scrollHeight};})()");
            assert([state[@"width"] intValue] == 1008 && [state[@"viewport"] intValue] == 1000);
            assert([state[@"scrollbar"] isEqual:@"8px"] && [state[@"thumb"] isEqual:@"rgb(217, 217, 217)"]);
            assert([state[@"root"] intValue] == 510);
            assert(fabs([state[@"paperLeft"] doubleValue] * 2 + [state[@"paperWidth"] doubleValue] - [state[@"viewport"] doubleValue]) < 1);
            assert([Evaluate(web, @"document.getElementById('macexplorer-document-scroll').tabIndex===0") boolValue]);
            assert([Evaluate(web, @"(()=>{const v=document.getElementById('macexplorer-document-scroll');v.scrollTop=100;return v.scrollTop>0;})()") boolValue]);
            scrollbars(handle, 8, 0xff73767c, 0xff8b8e94, 0xffa2a5ab);
            state = Evaluate(web, @"getComputedStyle(document.getElementById('macexplorer-document-scroll'),'::-webkit-scrollbar-thumb').backgroundColor");
            assert([(NSString *)state isEqual:@"rgb(115, 118, 124)"]);
            assert([Evaluate(web, @"document.getElementById('macexplorer-document-scroll').scrollTop===100") boolValue]);
            [window setContentSize:NSMakeSize(420, 500)]; Pump();
            assert(NSEqualRects(web.frame, web.superview.bounds));
            [window setContentSize:NSMakeSize(1008, 510)]; Pump();
            assert(NSEqualRects(web.frame, web.superview.bounds));
        }
        clear(handle);
        assert(host.subviews.count == 0);
        [window orderOut:nil];
        window.contentView = [NSView new];
        destroy(handle);
        Pump();
        fprintf(stdout, "PASS: PDF continuous pages, page 3 navigation, width resize, theme/radius, native focus restore, locked/corrupt files, Quick Look, shared scrollbars and cleanup\n");
    }
}
