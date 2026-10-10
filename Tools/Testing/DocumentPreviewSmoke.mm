// Compile: clang++ -fobjc-arc Tools/Testing/DocumentPreviewSmoke.mm -framework AppKit -framework Quartz -o /tmp/fkfinder-document-smoke
// Run: bash Tools/Testing/run-isolated.sh /tmp/fkfinder-document-smoke <built native dylib>
#import <AppKit/AppKit.h>
#import <Quartz/Quartz.h>
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
int main(int argc, char **argv) {
    @autoreleasepool {
        assert(argc == 2 && getenv("MACEXPLORER_TEST_ROOT"));
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
        auto clear = (void (*)(void *))dlsym(library, "me_document_preview_clear");
        auto destroy = (void (*)(void *))dlsym(library, "me_document_preview_destroy");
        assert(create && load && restoreFocus && appearance && clear && destroy);
        void *handle = create(Key, true);
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
        clear(handle);
        assert(host.subviews.count == 0);
        [window orderOut:nil];
        window.contentView = [NSView new];
        destroy(handle);
        Pump();
        fprintf(stdout, "PASS: PDF continuous pages, page 3 navigation, width resize, theme/radius, native focus restore, locked/corrupt files, Quick Look and cleanup\n");
    }
}
