#import "../../Platforms/MacOS/VideoPreview.mm"
#import <QuartzCore/QuartzCore.h>
#include <cassert>

@interface MEVideoSmokeRoot : NSView
@end
@implementation MEVideoSmokeRoot
- (BOOL)acceptsFirstResponder { return YES; }
@end

static void Pump(double seconds = 0.05) {
    NSDate *until = [NSDate dateWithTimeIntervalSinceNow:seconds];
    while (until.timeIntervalSinceNow > 0) @autoreleasepool {
        NSEvent *event = [NSApp nextEventMatchingMask:NSEventMaskAny untilDate:NSDate.date inMode:NSDefaultRunLoopMode dequeue:YES];
        if (event) [NSApp sendEvent:event];
        [NSApp updateWindows];
        [CATransaction flush];
        [NSRunLoop.currentRunLoop runMode:NSDefaultRunLoopMode beforeDate:until];
    }
}
static void WaitAt(BOOL (^ready)(void), int line) {
    NSDate *until = [NSDate dateWithTimeIntervalSinceNow:12];
    while (until.timeIntervalSinceNow > 0) {
        @autoreleasepool { if (ready()) return; }
        Pump();
    }
    fprintf(stderr, "Video preview timed out at line %d\n", line); abort();
}
#define Wait(...) WaitAt(__VA_ARGS__, __LINE__)
static void WriteFixture(NSString *path) {
    NSError *error = nil;
    AVAssetWriter *writer = [[AVAssetWriter alloc] initWithURL:[NSURL fileURLWithPath:path]
        fileType:[path.pathExtension.lowercaseString isEqualToString:@"mov"] ? AVFileTypeQuickTimeMovie : AVFileTypeMPEG4 error:&error];
    assert(writer && !error);
    AVAssetWriterInput *input = [AVAssetWriterInput assetWriterInputWithMediaType:AVMediaTypeVideo
        outputSettings:@{AVVideoCodecKey:AVVideoCodecTypeH264, AVVideoWidthKey:@320, AVVideoHeightKey:@180}];
    AVAssetWriterInputPixelBufferAdaptor *adaptor = [AVAssetWriterInputPixelBufferAdaptor
        assetWriterInputPixelBufferAdaptorWithAssetWriterInput:input sourcePixelBufferAttributes:@{
            (NSString *)kCVPixelBufferPixelFormatTypeKey:@(kCVPixelFormatType_32BGRA),
            (NSString *)kCVPixelBufferWidthKey:@320, (NSString *)kCVPixelBufferHeightKey:@180}];
    [writer addInput:input];
    assert([writer startWriting]);
    [writer startSessionAtSourceTime:kCMTimeZero];
    for (int frame = 0; frame < 96; frame++) {
        Wait(^{ return input.readyForMoreMediaData || writer.status == AVAssetWriterStatusFailed; });
        assert(writer.status != AVAssetWriterStatusFailed);
        CVPixelBufferRef buffer = nullptr;
        assert(CVPixelBufferPoolCreatePixelBuffer(nullptr, adaptor.pixelBufferPool, &buffer) == kCVReturnSuccess);
        CVPixelBufferLockBaseAddress(buffer, 0);
        auto *bytes = (unsigned char *)CVPixelBufferGetBaseAddress(buffer);
        size_t stride = CVPixelBufferGetBytesPerRow(buffer);
        for (int y = 0; y < 180; y++) for (int x = 0; x < 320; x++) {
            auto *pixel = bytes + y * stride + x * 4;
            pixel[0] = 90 + y / 2; pixel[1] = 45 + x / 3; pixel[2] = 25 + frame;
            if (abs(x - (frame * 3) % 320) < 15 && abs(y - 90) < 15) {
                pixel[0] = 30; pixel[1] = 210; pixel[2] = 240;
            }
            pixel[3] = 255;
        }
        CVPixelBufferUnlockBaseAddress(buffer, 0);
        assert([adaptor appendPixelBuffer:buffer withPresentationTime:CMTimeMake(frame, 24)]);
        CVPixelBufferRelease(buffer);
    }
    [input markAsFinished];
    __block BOOL finished = NO;
    [writer finishWritingWithCompletionHandler:^{ finished = YES; }];
    Wait(^{ return finished; });
    assert(writer.status == AVAssetWriterStatusCompleted);
}
static int State, LastKey, Callbacks;
static void Loaded(int state) { State = state; Callbacks++; }
static void Key(unsigned short code) { LastKey = code; }

static __attribute__((noinline)) void ExercisePlayback(void *handle, NSWindow *window, NSString *path,
    AVPlayer *__weak *weakPlayer, AVPlayerItem *__weak *weakItem, AVURLAsset *__weak *weakAsset) {
    MEVideoPreview *host = (__bridge MEVideoPreview *)handle;
        @autoreleasepool {
        me_video_preview_load(handle, path.fileSystemRepresentation);
        Wait(^{ return State != 0; });
        assert(State == 1 && host.video.player.rate == 0);
        Wait(^{ return host.video.readyForDisplay; });
        assert(host.video.videoGravity == AVLayerVideoGravityResizeAspect);
        assert(!host.video.showsFullScreenToggleButton && !host.video.allowsPictureInPicturePlayback);
        AVPlayer *player = host.video.player;
        double before = CMTimeGetSeconds(player.currentTime);
        Pump(0.2);
        assert(CMTimeGetSeconds(player.currentTime) == before);
        [player play];
        Wait(^{ return CMTimeGetSeconds(player.currentTime) > before + 0.2; });
        me_video_preview_active(handle, false);
        assert(player.rate == 0);
        me_video_preview_active(handle, true);
        assert(player.rate == 0);
        [player seekToTime:CMTimeMake(2, 1) toleranceBefore:kCMTimeZero toleranceAfter:kCMTimeZero];
        Wait(^{ return fabs(CMTimeGetSeconds(player.currentTime) - 2) < 0.1; });
        player.volume = 0.25;
        assert(player.volume == 0.25);
        host.frame = NSMakeRect(0, 0, 300, 240);
        assert(NSEqualRects(host.video.frame, host.bounds));
        me_video_preview_appearance(handle, true, 10);
        assert(host.layer.cornerRadius == 10);
        assert(([[host.effectiveAppearance bestMatchFromAppearancesWithNames:@[NSAppearanceNameAqua, NSAppearanceNameDarkAqua]] isEqualToString:NSAppearanceNameDarkAqua]));
        assert([window makeFirstResponder:host.video]);
        for (int key : {49, 53}) {
            LastKey = 0;
            NSEvent *event = [NSEvent keyEventWithType:NSEventTypeKeyDown location:NSZeroPoint modifierFlags:0
                timestamp:NSProcessInfo.processInfo.systemUptime windowNumber:window.windowNumber context:nil
                characters:key == 49 ? @" " : @"\e" charactersIgnoringModifiers:key == 49 ? @" " : @"\e"
                isARepeat:NO keyCode:key];
            [NSApp sendEvent:event];
            assert(LastKey == key && player.rate == 0);
        }
        *weakPlayer = player;
        *weakItem = host.item;
        *weakAsset = host.asset;
        player = nil;
        me_video_preview_clear(handle);
        assert(!host.item && !host.asset && !host.video && !host.keyMonitor);
        }
}

int main(int argc, char **argv) {
    @autoreleasepool {
        assert(getenv("MACEXPLORER_TEST_ROOT"));
        [NSApplication sharedApplication];
        if (argc == 3 && strcmp(argv[1], "--fixture") == 0) {
            WriteFixture([NSString stringWithUTF8String:argv[2]]);
            return 0;
        }
        NSString *root = [NSString stringWithUTF8String:getenv("MACEXPLORER_TEST_ROOT")];
        NSString *path = [root stringByAppendingPathComponent:@"中文 video fixture.MP4"];
        NSString *mov = [root stringByAppendingPathComponent:@"video.mov"];
        WriteFixture(path); WriteFixture(mov);
        NSWindow *window = [[NSWindow alloc] initWithContentRect:NSMakeRect(60, 60, 640, 480)
            styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskResizable backing:NSBackingStoreBuffered defer:NO];
        window.contentView = [[MEVideoSmokeRoot alloc] initWithFrame:NSMakeRect(0, 0, 640, 480)];
        [window makeKeyAndOrderFront:nil];
        void *handle;
        MEVideoPreview *host;
        __weak AVPlayer *weakPlayer;
        __weak AVPlayerItem *weakItem;
        __weak AVURLAsset *weakAsset;
        __weak MEVideoPreview *weakFirstHost;
        @autoreleasepool {
        handle = me_video_preview_create(Key, Loaded);
        host = (__bridge MEVideoPreview *)handle;
        [window.contentView addSubview:host];
        ExercisePlayback(handle, window, path, &weakPlayer, &weakItem, &weakAsset);
        weakFirstHost = host;
        me_video_preview_destroy(handle);
        host = nil;
        }
        Wait(^{ return weakFirstHost == nil; });
        Wait(^{ return weakItem == nil && weakAsset == nil; });
        assert(!weakPlayer || (!weakPlayer.currentItem && weakPlayer.rate == 0));
        // Reloading must reject stale async work; destroying during load must not call freed callbacks.
        for (int index = 0; index < 12; index++) {
            __weak MEVideoPreview *weakHost;
            @autoreleasepool {
                State = 0;
                handle = me_video_preview_create(Key, Loaded);
                host = (__bridge MEVideoPreview *)handle;
                weakHost = host;
                me_video_preview_load(handle, path.fileSystemRepresentation);
                me_video_preview_load(handle, mov.fileSystemRepresentation);
                Wait(^{ return State != 0; });
                assert(State == 1 && [host.asset.URL.path isEqualToString:mov]);
                me_video_preview_load(handle, path.fileSystemRepresentation);
                me_video_preview_destroy(handle);
                host = nil;
            }
            int previousCallbacks = Callbacks;
            Wait(^{ return weakHost == nil; });
            assert(previousCallbacks == Callbacks);
        }
        NSString *bad = [root stringByAppendingPathComponent:@"broken.mkv"];
        [@"unsupported video content" writeToFile:bad atomically:YES encoding:NSUTF8StringEncoding error:nil];
        for (NSString *invalid in @[bad, [root stringByAppendingPathComponent:@"missing.mp4"]]) {
            State = 0;
            handle = me_video_preview_create(Key, Loaded);
            me_video_preview_load(handle, invalid.fileSystemRepresentation);
            Wait(^{ return State != 0; });
            assert(State == 2);
            me_video_preview_destroy(handle);
        }
        [window orderOut:nil];
        puts("Video preview native checks passed: MP4/MOV, paused first frame, play/pause/seek/volume, resize/theme, native keys, async replacement, failure and release");
    }
}
