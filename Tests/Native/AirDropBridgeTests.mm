#import "../../Platforms/MacOS/AirDropBridge.mm"
#include <stdio.h>

#define CHECK(value) do { if (!(value)) { fprintf(stderr, "Failed at line %d: %s\n", __LINE__, #value); return 1; } } while (0)

static int callbackCount;
static int callbackResult;
static NSString *callbackError;
static void Completed(void *context, int result, const char *error) {
    callbackCount++;
    callbackResult = result;
    callbackError = error ? [NSString stringWithUTF8String:error] : nil;
    fprintf(stderr, "AirDrop completion: %d\n", result);
}

static void DrainMainQueue(void) {
    NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:2];
    while (!callbackCount && [deadline timeIntervalSinceNow] > 0)
        [NSRunLoop.currentRunLoop runUntilDate:[NSDate dateWithTimeIntervalSinceNow:0.01]];
}

@interface MEAirDropUITest : NSObject
@property(nonatomic, strong) NSWindow *window;
@property(nonatomic, strong) NSArray<NSString *> *paths;
- (void)send:(NSButton *)sender;
@end
@implementation MEAirDropUITest
- (void)send:(NSButton *)sender {
    NSArray *paths = sender.tag == 1 ? @[self.paths.firstObject] : self.paths;
    NSData *json = [NSJSONSerialization dataWithJSONObject:paths options:0 error:nil];
    NSString *text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
    int result = me_airdrop_send((__bridge void *)self.window.contentView, text.UTF8String, nullptr, Completed);
    fprintf(stderr, "AirDrop UI start: %d, files: %lu\n", result, (unsigned long)paths.count);
    fflush(stderr);
}
@end

int main(int argc, char **argv) {
    @autoreleasepool {
        CHECK(argc == 2 || (argc == 3 && strcmp(argv[2], "--ui") == 0));
        NSString *root = [NSString stringWithUTF8String:argv[1]];
        NSString *first = [root stringByAppendingPathComponent:@"含 空格\".txt"];
        NSString *second = [root stringByAppendingPathComponent:@"second.txt"];
        CHECK([@"one" writeToFile:first atomically:YES encoding:NSUTF8StringEncoding error:nil]);
        CHECK([@"two" writeToFile:second atomically:YES encoding:NSUTF8StringEncoding error:nil]);
        auto json = ^NSString *(NSArray *paths) {
            return [[NSString alloc] initWithData:[NSJSONSerialization dataWithJSONObject:paths options:0 error:nil]
                encoding:NSUTF8StringEncoding];
        };
        CHECK(MEAirDropFileURLs(json(@[first]).UTF8String).count == 1);
        NSArray<NSURL *> *urls = MEAirDropFileURLs(json(@[first, second]).UTF8String);
        CHECK(urls.count == 2);
        CHECK([urls[0].path isEqualToString:first]);
        CHECK([urls[1].path isEqualToString:second]);
        CHECK(urls[0].isFileURL && urls[1].isFileURL);
        for (NSString *bad in @[@"[]", @"{}", @"null", @"bad json", @"[null]", @"[1]",
                @"[\"relative.txt\"]", @"[\"sftp://host/a.txt\"]", @"[\"/tmp/a\\u0000b\"]",
                json(@[first, [root stringByAppendingPathComponent:@"missing.txt"]])])
            CHECK(MEAirDropFileURLs(bad.UTF8String) == nil);
        CHECK(me_airdrop_send(nullptr, "[]", nullptr, Completed) == -1);
        CHECK(me_airdrop_send(nullptr, json(@[first]).UTF8String, nullptr, Completed) == 0);
        CHECK(callbackCount == 0);

        [NSApplication sharedApplication];
        NSSharingService *system = [NSSharingService sharingServiceNamed:NSSharingServiceNameSendViaAirDrop];
        CHECK(system != nil);
        CHECK([system canPerformWithItems:urls]);

        for (int outcome = 0; outcome < 3; outcome++) {
            MEAirDropSession *session = [MEAirDropSession new];
            session.service = [[NSSharingService alloc] initWithTitle:@"Test" image:[NSImage new]
                alternateImage:nil handler:^{}];
            session.service.delegate = session;
            session.completion = Completed;
            MEActiveAirDropSession = session;
            callbackCount = 0;
            if (outcome == 0) [session sharingService:session.service didShareItems:urls];
            else [session sharingService:session.service didFailToShareItems:urls error:
                [NSError errorWithDomain:NSCocoaErrorDomain
                    code:outcome == 1 ? NSUserCancelledError : NSFileReadNoPermissionError userInfo:nil]];
            CHECK(MEActiveAirDropSession == nil);
            CHECK(session.service.delegate == nil);
            CHECK(callbackCount == 0);
            [session finish:1 error:nil]; // A late terminal event must not release the managed handle twice.
            DrainMainQueue();
            CHECK(callbackCount == 1);
            CHECK(callbackResult == (outcome == 0 ? 1 : outcome == 1 ? 0 : -1));
            if (outcome == 2) CHECK(callbackError.length > 0);
        }
        CHECK([[NSString stringWithContentsOfFile:first encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"one"]);
        CHECK([[NSString stringWithContentsOfFile:second encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"two"]);
        puts("AirDrop native checks passed: file URLs, validation, system availability, success, cancellation, failure, callback lifetime.");
        if (argc == 3) {
            MEAirDropUITest *test = [MEAirDropUITest new];
            test.paths = @[first, second];
            test.window = [[NSWindow alloc] initWithContentRect:NSMakeRect(100, 100, 460, 160)
                styleMask:NSWindowStyleMaskTitled | NSWindowStyleMaskClosable backing:NSBackingStoreBuffered defer:NO];
            test.window.title = @"FKFinder AirDrop Native Test";
            for (int count = 1; count <= 2; count++) {
                NSButton *button = [NSButton buttonWithTitle:count == 1 ? @"单文件隔空投送" : @"多文件隔空投送"
                    target:test action:@selector(send:)];
                button.tag = count;
                button.frame = NSMakeRect(30 + (count - 1) * 220, 60, 180, 32);
                [test.window.contentView addSubview:button];
            }
            [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
            [test.window makeKeyAndOrderFront:nil];
            [NSApp activateIgnoringOtherApps:YES];
            [NSApp run];
        }
    }
    return 0;
}
