#import "DocumentPreviewScrollbars.h"
#import <WebKit/WebKit.h>

static CGFloat MEScrollerWidth = 8;
static char MEDocumentWebLoadingContext;

@interface MEDocumentScrollStyle ()
@property(nonatomic, weak) NSView *content;
@property(nonatomic, strong) NSHashTable<WKWebView *> *webViews;
@property(nonatomic, strong) NSHashTable<NSScroller *> *scrollers;
@property(nonatomic, strong) NSTimer *discovery;
@property(nonatomic) NSUInteger attempts;
@property(nonatomic) uint32_t thumb, hover, pressed;
- (void)styleWebView:(WKWebView *)web;
- (BOOL)discover:(NSView *)view;
@end

static NSColor *ScrollColor(uint32_t color) {
    return [NSColor colorWithSRGBRed:((color >> 16) & 255) / 255.0 green:((color >> 8) & 255) / 255.0
        blue:(color & 255) / 255.0 alpha:(color >> 24) / 255.0];
}

@interface MEDocumentScroller : NSScroller
@property(nonatomic, weak) MEDocumentScrollStyle *style;
@end
@implementation MEDocumentScroller
+ (BOOL)isCompatibleWithOverlayScrollers { return YES; }
+ (CGFloat)scrollerWidthForControlSize:(NSControlSize)size scrollerStyle:(NSScrollerStyle)style { return MEScrollerWidth; }
- (BOOL)isOpaque { return NO; }
- (void)drawKnobSlotInRect:(NSRect)rect highlight:(BOOL)highlight { }
- (void)drawArrow:(NSScrollerArrow)arrow highlight:(BOOL)highlight { }
- (void)drawRect:(NSRect)dirtyRect {
    [[NSColor clearColor] setFill]; NSRectFillUsingOperation(self.bounds, NSCompositingOperationCopy);
    if (!self.enabled || self.knobProportion >= 1) return;
    NSRect knob = [self rectForPart:NSScrollerKnob];
    NSPoint mouse = [self convertPoint:self.window.mouseLocationOutsideOfEventStream fromView:nil];
    BOOL hover = NSPointInRect(mouse, self.bounds);
    BOOL pressed = hover && self.hitPart == NSScrollerKnob;
    CGFloat width = MEScrollerWidth * (hover ? 1 : 0.75);
    if (self.bounds.size.height >= self.bounds.size.width) {
        knob.origin.x += (knob.size.width - width) / 2; knob.size.width = width;
    } else {
        knob.origin.y += (knob.size.height - width) / 2; knob.size.height = width;
    }
    [ScrollColor(pressed ? self.style.pressed : hover ? self.style.hover : self.style.thumb) setFill];
    [[NSBezierPath bezierPathWithRoundedRect:knob xRadius:width / 2 yRadius:width / 2] fill];
}
@end

@implementation MEDocumentScrollStyle
- (instancetype)init {
    if (!(self = [super init])) return nil;
    _webViews = [NSHashTable weakObjectsHashTable];
    _scrollers = [NSHashTable weakObjectsHashTable];
    return self;
}
- (void)prepareContent:(NSView *)content {
    [self clear]; self.content = content;
    if ([self discover:content]) return;
    // Quick Look creates its renderer asynchronously. Stop discovery after it appears,
    // or after two seconds; the timer never owns the preview or runs while it is closed.
    self.attempts = 0;
    __weak MEDocumentScrollStyle *weakSelf = self;
    self.discovery = [NSTimer scheduledTimerWithTimeInterval:0.05 repeats:YES block:^(NSTimer *timer) {
        MEDocumentScrollStyle *current = weakSelf;
        if (!current || !current.content || [current discover:current.content] || ++current.attempts >= 40)
            [timer invalidate];
    }];
}
- (void)setWidth:(CGFloat)width thumb:(uint32_t)thumb hover:(uint32_t)hover pressed:(uint32_t)pressed {
    MEScrollerWidth = width;
    self.thumb = thumb; self.hover = hover; self.pressed = pressed;
    for (WKWebView *web in self.webViews) [self styleWebView:web];
    for (NSScroller *scroller in self.scrollers) scroller.needsDisplay = YES;
}
- (void)fillWebView:(WKWebView *)web {
    // Quick Look's centering container otherwise constrains Office's scroll viewport
    // to a paper-sized column, leaving its scrollbar in the middle of the host.
    if (web.superview && !NSEqualRects(web.frame, web.superview.bounds)) web.frame = web.superview.bounds;
}
- (void)webFrameChanged:(NSNotification *)notification { [self fillWebView:notification.object]; }
- (void)observeValueForKeyPath:(NSString *)keyPath ofObject:(id)object change:(NSDictionary *)change context:(void *)context {
    if (context == &MEDocumentWebLoadingContext) {
        if (![(WKWebView *)object isLoading]) [self styleWebView:object];
    } else [super observeValueForKeyPath:keyPath ofObject:object change:change context:context];
}
- (void)styleWebView:(WKWebView *)web {
    [self fillWebView:web];
    NSString *(^cssColor)(uint32_t) = ^NSString *(uint32_t color) {
        return [NSString stringWithFormat:@"rgba(%u,%u,%u,%.4f)", (color >> 16) & 255, (color >> 8) & 255, color & 255, (color >> 24) / 255.0];
    };
    NSString *css = [NSString stringWithFormat:
        @"html,body{height:100%%;overflow:hidden!important}"
        "#macexplorer-document-scroll{position:absolute;inset:0;overflow:auto}"
        "#macexplorer-document-scroll>div,#macexplorer-document-scroll>table{margin-left:auto;margin-right:auto}"
        "::-webkit-scrollbar{width:%gpx;height:%gpx;background:transparent}"
        "::-webkit-scrollbar-track,::-webkit-scrollbar-corner{background:transparent;border:0;box-shadow:none}"
        "::-webkit-scrollbar-button{display:none;width:0;height:0}"
        "::-webkit-scrollbar-thumb{background:%@;border:1px solid transparent;background-clip:padding-box;border-radius:999px}"
        "::-webkit-scrollbar-thumb:hover{background:%@;border:0}::-webkit-scrollbar-thumb:active{background:%@;border:0}",
        MEScrollerWidth, MEScrollerWidth, cssColor(self.thumb), cssColor(self.hover), cssColor(self.pressed)];
    NSData *json = [NSJSONSerialization dataWithJSONObject:@[css] options:0 error:nil];
    NSString *script = [NSString stringWithFormat:
        @"(()=>{if(!document.body||!document.head)return;let v=document.getElementById('macexplorer-document-scroll');"
        "if(!v){v=document.createElement('div');v.id='macexplorer-document-scroll';v.tabIndex=0;"
        "while(document.body.firstChild)v.appendChild(document.body.firstChild);document.body.appendChild(v);}"
        "let s=document.getElementById('macexplorer-scrollbars');if(!s){s=document.createElement('style');s.id='macexplorer-scrollbars';document.head.appendChild(s);}s.textContent=(%@)[0];})()",
        [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]];
    [web evaluateJavaScript:script completionHandler:nil];
}
- (NSScroller *)prepareScroller:(NSScroller *)existing {
    if (!existing) return nil;
    MEDocumentScroller *scroller;
    if ([existing isKindOfClass:MEDocumentScroller.class]) scroller = (MEDocumentScroller *)existing;
    else {
        scroller = [[MEDocumentScroller alloc] initWithFrame:existing.frame];
        scroller.controlSize = NSControlSizeSmall;
    }
    scroller.scrollerStyle = NSScrollerStyleOverlay;
    scroller.style = self; [self.scrollers addObject:scroller]; return scroller;
}
- (BOOL)discover:(NSView *)view {
    if ([view isKindOfClass:WKWebView.class]) {
        WKWebView *web = (WKWebView *)view;
        if (![self.webViews containsObject:web]) {
            [self.webViews addObject:web];
            web.autoresizingMask = NSViewWidthSizable | NSViewHeightSizable;
            web.postsFrameChangedNotifications = YES;
            [NSNotificationCenter.defaultCenter addObserver:self selector:@selector(webFrameChanged:)
                name:NSViewFrameDidChangeNotification object:web];
            [web addObserver:self forKeyPath:@"loading" options:0 context:&MEDocumentWebLoadingContext];
            [self styleWebView:web];
        }
        return YES;
    }
    BOOL found = NO;
    if ([view isKindOfClass:NSScrollView.class]) {
        NSScrollView *scroll = (NSScrollView *)view;
        scroll.scrollerStyle = NSScrollerStyleOverlay; scroll.autohidesScrollers = YES;
        scroll.borderType = NSNoBorder;
        if (scroll.verticalScroller) scroll.verticalScroller = [self prepareScroller:scroll.verticalScroller];
        if (scroll.horizontalScroller) scroll.horizontalScroller = [self prepareScroller:scroll.horizontalScroller];
        [scroll tile]; found = YES;
    }
    for (NSView *child in view.subviews) found = [self discover:child] || found;
    return found;
}
- (void)clear {
    [self.discovery invalidate]; self.discovery = nil;
    [NSNotificationCenter.defaultCenter removeObserver:self];
    for (WKWebView *web in self.webViews)
        [web removeObserver:self forKeyPath:@"loading" context:&MEDocumentWebLoadingContext];
    [self.webViews removeAllObjects]; [self.scrollers removeAllObjects]; self.content = nil;
}
- (void)dealloc { [self clear]; }
@end
