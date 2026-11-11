#import <AppKit/AppKit.h>
#import <AVKit/AVKit.h>

typedef void (*VideoKeyCallback)(unsigned short key);
typedef void (*VideoStateCallback)(int state);
static void *VideoItemStatusContext = &VideoItemStatusContext;

// All ownership changes and callbacks happen on the AppKit main thread.
@interface MEVideoPreview : NSView
@property(nonatomic, strong) AVPlayerView *video;
@property(nonatomic, strong) AVURLAsset *asset;
@property(nonatomic, strong) AVPlayerItem *item;
@property(nonatomic, strong) id keyMonitor;
@property(nonatomic) VideoKeyCallback keyCallback;
@property(nonatomic) VideoStateCallback stateCallback;
@property(nonatomic) int state;
- (void)clearVideo;
- (void)reportState:(int)state;
@end

@implementation MEVideoPreview
- (void)setFrameSize:(NSSize)size {
    [super setFrameSize:size];
    self.video.frame = self.bounds;
}
- (void)reportState:(int)state {
    if (self.state == state) return;
    self.state = state;
    if (self.stateCallback) self.stateCallback(state);
}
- (void)observeValueForKeyPath:(NSString *)keyPath ofObject:(id)object change:(NSDictionary *)change context:(void *)context {
    if (context != VideoItemStatusContext) {
        [super observeValueForKeyPath:keyPath ofObject:object change:change context:context];
        return;
    }
    __weak MEVideoPreview *weakSelf = self;
    AVPlayerItem *item = object;
    dispatch_async(dispatch_get_main_queue(), ^{
        MEVideoPreview *current = weakSelf;
        if (!current || current.item != item) return;
        if (item.status == AVPlayerItemStatusReadyToPlay) {
            // Seek while paused so the initial preview displays the first frame.
            [current.video.player seekToTime:kCMTimeZero toleranceBefore:kCMTimeZero toleranceAfter:kCMTimeZero];
            [current reportState:1];
        } else if (item.status == AVPlayerItemStatusFailed) {
            [current.video.player pause];
            [current reportState:2];
        }
    });
}
- (void)setHidden:(BOOL)hidden {
    if (hidden) [self.video.player pause];
    [super setHidden:hidden];
}
- (void)clearVideo {
    NSResponder *responder = self.window.firstResponder;
    if ([responder isKindOfClass:NSView.class] && [(NSView *)responder isDescendantOf:self])
        [self.window makeFirstResponder:self.window.contentView];
    [self.video.player pause];
    if (self.item) [self.item removeObserver:self forKeyPath:@"status" context:VideoItemStatusContext];
    [self.item cancelPendingSeeks];
    [self.asset cancelLoading];
    [self.video.player replaceCurrentItemWithPlayerItem:nil];
    self.item = nil;
    self.asset = nil;
    self.video.player = nil;
    [self.video removeFromSuperview];
    self.video = nil;
    self.state = 0;
}
- (void)dealloc {
    if (self.keyMonitor) [NSEvent removeMonitor:self.keyMonitor];
    [self clearVideo];
}
@end

extern "C" {
void *me_video_preview_create(VideoKeyCallback keyCallback, VideoStateCallback stateCallback) {
    MEVideoPreview *view = [[MEVideoPreview alloc] initWithFrame:NSMakeRect(0, 0, 640, 480)];
    view.wantsLayer = YES;
    view.layer.masksToBounds = YES;
    view.keyCallback = keyCallback;
    view.stateCallback = stateCallback;
    __weak MEVideoPreview *weakView = view;
    view.keyMonitor = [NSEvent addLocalMonitorForEventsMatchingMask:NSEventMaskKeyDown handler:^NSEvent *(NSEvent *event) {
        MEVideoPreview *current = weakView;
        if (!current || current.hiddenOrHasHiddenAncestor || !current.keyCallback || event.window != current.window) return event;
        NSResponder *responder = current.window.firstResponder;
        if (![responder isKindOfClass:NSView.class] || ![(NSView *)responder isDescendantOf:current]) return event;
        if (event.modifierFlags & (NSEventModifierFlagCommand | NSEventModifierFlagControl | NSEventModifierFlagOption | NSEventModifierFlagShift)) return event;
        unsigned short key = event.keyCode;
        if (key == 49 || key == 53 || key == 51 || key == 36 || key == 76) {
            current.keyCallback(key);
            return nil;
        }
        return event;
    }];
    return (__bridge_retained void *)view;
}

// 1: ready (paused), 2: unreadable or unsupported. Loading never blocks the UI.
void me_video_preview_load(void *handle, const char *path) {
    MEVideoPreview *view = (__bridge MEVideoPreview *)handle;
    [view clearVideo];
    NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
    if (![NSFileManager.defaultManager isReadableFileAtPath:url.path]) { [view reportState:2]; return; }
    AVURLAsset *asset = [AVURLAsset URLAssetWithURL:url options:nil];
    view.asset = asset;
    __weak MEVideoPreview *weakView = view;
    [asset loadValuesAsynchronouslyForKeys:@[@"playable", @"tracks"] completionHandler:^{
        dispatch_async(dispatch_get_main_queue(), ^{
            MEVideoPreview *current = weakView;
            if (!current || current.asset != asset) return;
            if ([asset statusOfValueForKey:@"playable" error:nil] != AVKeyValueStatusLoaded
                || [asset statusOfValueForKey:@"tracks" error:nil] != AVKeyValueStatusLoaded
                || !asset.playable || [asset tracksWithMediaType:AVMediaTypeVideo].count == 0) {
                [current reportState:2];
                return;
            }
            current.video = [[AVPlayerView alloc] initWithFrame:current.bounds];
            current.video.controlsStyle = AVPlayerViewControlsStyleInline;
            current.video.videoGravity = AVLayerVideoGravityResizeAspect;
            current.video.showsSharingServiceButton = NO;
            current.video.showsFullScreenToggleButton = NO;
            current.video.allowsPictureInPicturePlayback = NO;
            current.video.updatesNowPlayingInfoCenter = NO;
            current.item = [AVPlayerItem playerItemWithAsset:asset];
            [current.item addObserver:current forKeyPath:@"status" options:NSKeyValueObservingOptionNew context:VideoItemStatusContext];
            current.video.player = [AVPlayer playerWithPlayerItem:current.item];
            [current addSubview:current.video];
        });
    }];
}

void me_video_preview_active(void *handle, bool active) {
    MEVideoPreview *view = (__bridge MEVideoPreview *)handle;
    if (!active) [view.video.player pause];
    // NativeControlHost owns visibility. Showing the view never resumes playback.
}
void me_video_preview_appearance(void *handle, bool dark, double radius) {
    MEVideoPreview *view = (__bridge MEVideoPreview *)handle;
    view.appearance = [NSAppearance appearanceNamed:dark ? NSAppearanceNameDarkAqua : NSAppearanceNameAqua];
    view.layer.cornerRadius = MAX(0, radius);
}
void me_video_preview_restore_focus(void *handle, void *rootHandle) {
    MEVideoPreview *view = (__bridge MEVideoPreview *)handle;
    NSView *root = (__bridge NSView *)rootHandle;
    NSResponder *responder = view.window.firstResponder;
    if (view.window && view.window == root.window && [responder isKindOfClass:NSView.class]
        && [(NSView *)responder isDescendantOf:view]) [view.window makeFirstResponder:root];
}
void me_video_preview_clear(void *handle) {
    MEVideoPreview *view = (__bridge MEVideoPreview *)handle;
    view.keyCallback = nullptr;
    view.stateCallback = nullptr;
    [view clearVideo];
    if (view.keyMonitor) { [NSEvent removeMonitor:view.keyMonitor]; view.keyMonitor = nil; }
}
void me_video_preview_destroy(void *handle) {
    me_video_preview_clear(handle);
    MEVideoPreview *view = (__bridge_transfer MEVideoPreview *)handle;
    [view removeFromSuperview];
}
}
