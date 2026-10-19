#import <AppKit/AppKit.h>

typedef void (*MEAirDropCompletion)(void *, int, const char *);

@interface MEAirDropSession : NSObject <NSSharingServiceDelegate>
@property(nonatomic, strong) NSSharingService *service;
@property(nonatomic, weak) NSWindow *window;
@property(nonatomic) void *context;
@property(nonatomic) MEAirDropCompletion completion;
- (void)finish:(int)result error:(NSError *)error;
@end

// A sharing service has a weak delegate; keep the session alive until its terminal callback.
static MEAirDropSession *MEActiveAirDropSession;

@implementation MEAirDropSession
- (NSWindow *)sharingService:(NSSharingService *)service sourceWindowForShareItems:(NSArray *)items
        sharingContentScope:(NSSharingContentScope *)scope {
    return self.window;
}
- (void)sharingService:(NSSharingService *)service didShareItems:(NSArray *)items {
    [self finish:1 error:nil];
}
- (void)sharingService:(NSSharingService *)service didFailToShareItems:(NSArray *)items error:(NSError *)error {
    BOOL cancelled = [error.domain isEqualToString:NSCocoaErrorDomain] && error.code == NSUserCancelledError;
    [self finish:cancelled ? 0 : -1 error:error];
}
- (void)finish:(int)result error:(NSError *)error {
    if (!self.completion) return;
    MEAirDropCompletion callback = self.completion;
    self.completion = nullptr;
    self.service.delegate = nil;
    if (MEActiveAirDropSession == self) MEActiveAirDropSession = nil;
    // Always complete after me_airdrop_send returns, including immediate AppKit failures.
    dispatch_async(dispatch_get_main_queue(), ^{
        callback(self.context, result, error.localizedDescription.UTF8String);
    });
}
@end

static NSArray<NSURL *> *MEAirDropFileURLs(const char *pathsJson) {
    if (!pathsJson) return nil;
    NSString *json = [NSString stringWithUTF8String:pathsJson];
    if (!json) return nil;
    id paths = [NSJSONSerialization JSONObjectWithData:[json dataUsingEncoding:NSUTF8StringEncoding]
        options:0 error:nil];
    if (![paths isKindOfClass:NSArray.class] || [paths count] == 0) return nil;
    NSMutableArray<NSURL *> *urls = [NSMutableArray array];
    for (id path in paths) {
        if (![path isKindOfClass:NSString.class] || ![path isAbsolutePath]
            || [path rangeOfString:[NSString stringWithFormat:@"%C", (unichar)0]].location != NSNotFound
            || ![NSFileManager.defaultManager isReadableFileAtPath:path]) return nil;
        [urls addObject:[NSURL fileURLWithPath:path]];
    }
    return urls;
}

extern "C" __attribute__((visibility("default")))
int me_airdrop_send(void *viewPointer, const char *pathsJson, void *context, MEAirDropCompletion completion) {
    @autoreleasepool {
        if (!NSThread.isMainThread || !completion) return 0;
        NSArray<NSURL *> *urls = MEAirDropFileURLs(pathsJson);
        if (!urls) return -1;
        if (MEActiveAirDropSession) return -2;
        NSView *view = (__bridge NSView *)viewPointer;
        if (!view.window) return 0;
        NSSharingService *service = [NSSharingService sharingServiceNamed:NSSharingServiceNameSendViaAirDrop];
        if (!service || ![service canPerformWithItems:urls]) return 0;
        MEAirDropSession *session = [MEAirDropSession new];
        session.service = service;
        session.window = view.window;
        session.context = context;
        session.completion = completion;
        service.delegate = session;
        MEActiveAirDropSession = session;
        @try {
            [service performWithItems:urls];
        } @catch (NSException *exception) {
            [session finish:-1 error:[NSError errorWithDomain:@"MacExplorer.AirDrop" code:1
                userInfo:@{NSLocalizedDescriptionKey: exception.reason ?: @"AirDrop"}]];
        }
        return 1;
    }
}
