#import "../../Platforms/MacOS/FileDropBridge.mm"
#include <stdio.h>
#include <sys/xattr.h>
#include <sys/stat.h>

#define CHECK(value) do { if (!(value)) { fprintf(stderr, "Failed at line %d: %s\n", __LINE__, #value); return 1; } } while (0)

static NSMutableArray<NSString *> *savedPaths;
static NSMutableArray<NSString *> *failures;
static NSMutableArray<NSString *> *permissionSources;
static NSUInteger cancelledGrants;
static void FolderCancelled(void *context, const char *bookmark, const char *error) {
    if (bookmark || error) abort();
    cancelledGrants++;
}
static void Completed(void *context, const char *directory, const char *path, const char *error, const char *permissionSource) {
    @synchronized(savedPaths) {
        if (path) [savedPaths addObject:[NSString stringWithUTF8String:path]];
        if (error) [failures addObject:[NSString stringWithUTF8String:error]];
        if (permissionSource) [permissionSources addObject:[NSString stringWithUTF8String:permissionSource]];
    }
}
static void WaitForWrites() {
    CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.01, false);
    [MEDropQueue() waitUntilAllOperationsAreFinished];
}

// Model AppKit's reader lifetime, including old promises producing several files
// and providers reporting failure after the destination has accepted the drag.
@interface TestPromise : NSFilePromiseReceiver
@property(nonatomic, strong) NSArray<NSString *> *names;
@property(nonatomic) BOOL fail;
@property(nonatomic) BOOL outsideStaging;
@property(nonatomic, strong) NSURL *receivedDirectory;
@end
@implementation TestPromise
- (void)receivePromisedFilesAtDestination:(NSURL *)destination options:(NSDictionary *)options
    operationQueue:(NSOperationQueue *)queue reader:(void (^)(NSURL *, NSError *))reader {
    self.receivedDirectory = destination;
    for (NSString *name in self.names) {
        BOOL fail = self.fail, outside = self.outsideStaging;
        [queue addOperationWithBlock:^{
            NSURL *file = [destination URLByAppendingPathComponent:name];
            if (outside) file = destination.URLByDeletingLastPathComponent;
            else [@"promised contents" writeToURL:file atomically:YES encoding:NSUTF8StringEncoding error:nil];
            reader(file, fail ? [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadNoPermissionError userInfo:nil] : nil);
        }];
    }
}
@end

@interface TestScopedURL : NSObject
@property(nonatomic) BOOL grantsAccess;
@property(nonatomic) NSInteger starts;
@property(nonatomic) NSInteger stops;
@end
@implementation TestScopedURL
- (BOOL)isFileURL { return YES; }
- (BOOL)startAccessingSecurityScopedResource { self.starts++; return self.grantsAccess; }
- (void)stopAccessingSecurityScopedResource { self.stops++; }
@end

@interface TestDraggingInfo : NSObject
@property(nonatomic, strong) NSPasteboard *draggingPasteboard;
@end
@implementation TestDraggingInfo
@end

@interface AvnView : NSView
@property(nonatomic, copy) NSDragOperation (^handler)(void);
- (NSDragOperation)triggerAvnDragEvent:(int)type info:(id<NSDraggingInfo>)info;
@end
@implementation AvnView
- (NSDragOperation)triggerAvnDragEvent:(int)type info:(id<NSDraggingInfo>)info { return self.handler(); }
@end

int main(int argc, char **argv) {
    @autoreleasepool {
        CHECK(argc == 2);
        [NSApplication sharedApplication];
        NSString *root = [NSString stringWithUTF8String:argv[1]];
        savedPaths = [NSMutableArray new]; failures = [NSMutableArray new]; permissionSources = [NSMutableArray new];
        TestScopedURL *scoped = [TestScopedURL new]; scoped.grantsAccess = YES;
        TestScopedURL *ordinary = [TestScopedURL new];
        @autoreleasepool {
            void *access = (__bridge_retained void *)MEAcquireDropURLs((id)@[scoped, ordinary]);
            CHECK(scoped.starts == 1 && ordinary.starts == 1 && scoped.stops == 0);
            me_file_drop_end_access(access);
        }
        CHECK(scoped.stops == 1 && ordinary.stops == 0);
        CHECK(me_file_drop_begin_access() == nullptr);
        NSPasteboard *board = [NSPasteboard pasteboardWithUniqueName];
        CHECK(MEDropKind(board) == 0);
        CHECK(!MEReceiveDrop(board, root, nullptr, Completed));
        CHECK(!MEReceiveDrop(board, @"relative", nullptr, Completed));
        NSBitmapImageRep *pixels = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nullptr pixelsWide:2 pixelsHigh:2
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:8 bitsPerPixel:32];
        memset(pixels.bitmapData, 255, 16);
        NSData *png = [pixels representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
        [board setData:png forType:NSPasteboardTypePNG];
        CHECK(MEDropKind(board) == 2);
        CHECK(me_file_drop_kind() == 0); // No access to a stale drag/general clipboard.
        AvnView *view = [AvnView new];
        [view registerForDraggedTypes:@[@"test.original-type"]];
        CHECK(me_file_drop_enable((__bridge void *)view));
        CHECK(me_file_drop_enable((__bridge void *)view)); // Does not hook itself twice.
        CHECK([view.registeredDraggedTypes containsObject:@"test.original-type"]);
        for (NSString *type in NSFilePromiseReceiver.readableDraggedTypes)
            CHECK([view.registeredDraggedTypes containsObject:type]);
        TestDraggingInfo *info = [TestDraggingInfo new]; info.draggingPasteboard = board;
        view.handler = ^NSDragOperation {
            if (me_file_drop_kind() != 2 || !me_file_drop_receive(root.UTF8String, nullptr, Completed)) abort();
            return NSDragOperationCopy;
        };
        CHECK([view triggerAvnDragEvent:3 info:(id)info] == NSDragOperationCopy);
        CHECK(me_file_drop_kind() == 0);
        WaitForWrites();
        CHECK(savedPaths.count == 1 && failures.count == 0);
        CHECK([[NSData dataWithContentsOfFile:savedPaths[0]] isEqual:png]);
        CHECK(MEReceiveDrop(board, root, nullptr, Completed)); WaitForWrites();
        CHECK(savedPaths.count == 2 && ![savedPaths[0] isEqual:savedPaths[1]]);
        CHECK([[NSData dataWithContentsOfFile:savedPaths[0]] isEqual:png]);
        // A file URL plus thumbnail remains a regular file drop.
        [board setString:[[NSURL fileURLWithPath:savedPaths[0]] absoluteString] forType:NSPasteboardTypeFileURL];
        CHECK(MEDropKind(board) == 0);
        CHECK(MEReadDroppedPaths(board).count == 1);
        [board clearContents];
        [board setString:@"" forType:NSPasteboardTypeFileURL];
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [board setPropertyList:@[[root stringByAppendingPathComponent:@"微信 附件.png"]] forType:NSFilenamesPboardType];
#pragma clang diagnostic pop
        CHECK([MEReadDroppedPaths(board) isEqualToArray:@[[root stringByAppendingPathComponent:@"微信 附件.png"]]]);
        [board clearContents];
        [board setString:@"# Qt URI list\r\nfile:///tmp/%E5%9B%BE%E7%89%87%20test.png\r\nhttps://example.com/a\r\nfile:///tmp/second.txt"
            forType:@"com.trolltech.anymime.text--uri-list"];
        CHECK(([MEReadDroppedPaths(board) isEqualToArray:@[@"/tmp/图片 test.png", @"/tmp/second.txt"]]));
        CHECK(me_file_drop_copy_paths() == nullptr); // No stale pasteboard outside native event routing.
        // Legacy promise format takes precedence over any thumbnail.
        [board setPropertyList:@[@"png"] forType:NSFilesPromisePboardType];
        CHECK(MEDropKind(board) == 1);
        [board clearContents]; [board setData:pixels.TIFFRepresentation forType:NSPasteboardTypeTIFF];
        CHECK(MEReceiveDrop(board, root, nullptr, Completed)); WaitForWrites();
        CHECK(savedPaths.count == 3);
        NSBitmapImageRep *converted = [NSBitmapImageRep imageRepWithData:[NSData dataWithContentsOfFile:savedPaths.lastObject]];
        CHECK(converted.pixelsWide == 2 && converted.pixelsHigh == 2);
        NSColor *color = [[converted colorAtX:0 y:0] colorUsingColorSpace:NSColorSpace.sRGBColorSpace];
        CHECK(color.redComponent > 0.99 && color.greenComponent > 0.99 && color.blueComponent > 0.99 && color.alphaComponent > 0.99);

        __weak MEDropStaging *weakStaging;
        NSURL *stagingURL;
        @autoreleasepool {
            MEDropStaging *staging = [MEDropStaging new]; weakStaging = staging;
            staging.url = [NSURL fileURLWithPath:[root stringByAppendingPathComponent:@"staging"] isDirectory:YES]; stagingURL = staging.url;
            CHECK([NSFileManager.defaultManager createDirectoryAtURL:staging.url withIntermediateDirectories:NO attributes:nil error:nil]);
            TestPromise *first = [TestPromise new]; first.names = @[@"含 空格.txt", @"another.txt"];
            TestPromise *second = [TestPromise new]; second.names = @[@"failed.txt"]; second.fail = YES;
            TestPromise *outside = [TestPromise new]; outside.names = @[@"bad"]; outside.outsideStaging = YES;
            [@"existing" writeToFile:[root stringByAppendingPathComponent:@"含 空格.txt"] atomically:YES encoding:NSUTF8StringEncoding error:nil];
            MEReceivePromises(@[first, second, outside], staging, [NSURL fileURLWithPath:root isDirectory:YES], nullptr, Completed);
            CHECK([first.receivedDirectory isEqual:second.receivedDirectory]);
        }
        WaitForWrites();
        CHECK(savedPaths.count == 5 && failures.count == 2);
        CHECK([[NSString stringWithContentsOfFile:[root stringByAppendingPathComponent:@"含 空格.txt"] encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"existing"]);
        CHECK([[NSString stringWithContentsOfFile:[root stringByAppendingPathComponent:@"含 空格 2.txt"] encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"promised contents"]);
        // Operations may be autoreleased until their worker pool drains.
        NSDate *deadline = [NSDate dateWithTimeIntervalSinceNow:2];
        while (weakStaging && deadline.timeIntervalSinceNow > 0) [NSThread sleepForTimeInterval:0.01];
        CHECK(!weakStaging && ![NSFileManager.defaultManager fileExistsAtPath:stagingURL.path]);
        [board clearContents];
        NSString *qtSource = [root stringByAppendingPathComponent:@"微信 附件.txt"];
        NSString *qtTarget = [root stringByAppendingPathComponent:@"qt-target"];
        CHECK([@"Qt drag contents" writeToFile:qtSource atomically:YES encoding:NSUTF8StringEncoding error:nil]);
        CHECK([NSFileManager.defaultManager createDirectoryAtPath:qtTarget withIntermediateDirectories:NO attributes:nil error:nil]);
        [board setString:@"" forType:NSPasteboardTypeFileURL];
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
        [board setPropertyList:@[qtSource] forType:NSFilenamesPboardType];
#pragma clang diagnostic pop
        [board setString:[NSURL fileURLWithPath:qtSource].absoluteString forType:@"com.trolltech.anymime.text--uri-list"];
        CHECK(MEDropKind(board) == 3);
        NSUInteger beforeQt = savedPaths.count;
        // Model a source that becomes readable only after Drop has returned.
        view.handler = ^NSDragOperation { return MEReceiveDrop(board, qtTarget, nullptr, Completed) ? NSDragOperationCopy : NSDragOperationNone; };
        CHECK([view triggerAvnDragEvent:3 info:(id)info] == NSDragOperationCopy);
        WaitForWrites();
        CHECK(MECurrentDropPasteboard == nil);
        CHECK(savedPaths.count == beforeQt + 1 && failures.count == 2);
        CHECK([[NSString stringWithContentsOfFile:qtSource encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"Qt drag contents"]);
        CHECK([[NSString stringWithContentsOfFile:[qtTarget stringByAppendingPathComponent:@"微信 附件.txt"] encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"Qt drag contents"]);
        // Multi-chunk binary exports preserve bytes without importing source metadata.
        NSString *binaryPath = [root stringByAppendingPathComponent:@"附件.zip"];
        NSMutableData *binary = [NSMutableData dataWithLength:196613];
        for (NSUInteger i = 0; i < binary.length; i++) ((uint8_t *)binary.mutableBytes)[i] = (uint8_t)(i * 31);
        CHECK([binary writeToFile:binaryPath atomically:YES]);
        CHECK(setxattr(binaryPath.fileSystemRepresentation, "com.macexplorer.drop-test", "metadata", 8, 0, 0) == 0);
        NSURL *binaryTarget = [NSURL fileURLWithPath:[qtTarget stringByAppendingPathComponent:@"附件.zip"]];
        NSError *contentError = nil;
        CHECK(MECopyDroppedContents([NSURL fileURLWithPath:binaryPath], binaryTarget, &contentError));
        CHECK([[NSData dataWithContentsOfURL:binaryTarget] isEqual:binary]);
        CHECK(getxattr(binaryTarget.fileSystemRepresentation, "com.macexplorer.drop-test", nullptr, 0, 0, 0) == -1);
        CHECK([[NSData dataWithContentsOfFile:binaryPath] isEqual:binary]);
        NSURL *empty = [NSURL fileURLWithPath:[root stringByAppendingPathComponent:@"empty"]];
        CHECK([[NSData data] writeToURL:empty atomically:YES]);
        NSURL *emptyTarget = [NSURL fileURLWithPath:[qtTarget stringByAppendingPathComponent:@"empty"]];
        CHECK(MECopyDroppedContents(empty, emptyTarget, &contentError));
        CHECK([NSData dataWithContentsOfURL:emptyTarget].length == 0);
        NSURL *missing = [NSURL fileURLWithPath:[root stringByAppendingPathComponent:@"missing"]];
        CHECK(!MECopyDroppedContents(missing, emptyTarget, &contentError) && contentError != nil);
        CHECK(MEReadPermissionError([NSError errorWithDomain:NSPOSIXErrorDomain code:EPERM userInfo:nil]));
        CHECK(MEReadPermissionError([NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadNoPermissionError userInfo:nil]));
        CHECK(!MEReadPermissionError([NSError errorWithDomain:NSCocoaErrorDomain code:NSFileWriteNoPermissionError userInfo:nil]));
        CHECK(chmod(qtSource.fileSystemRepresentation, 0000) == 0);
        CHECK(me_file_drop_copy_file(qtSource.UTF8String, qtTarget.UTF8String, nullptr, Completed));
        WaitForWrites();
        CHECK(chmod(qtSource.fileSystemRepresentation, 0644) == 0);
        CHECK(permissionSources.count == 1 && [permissionSources[0] isEqualToString:qtSource]);
        CHECK(me_file_drop_copy_file(qtSource.UTF8String, qtTarget.UTF8String, nullptr, Completed));
        WaitForWrites();
        CHECK([[NSString stringWithContentsOfFile:savedPaths.lastObject encoding:NSUTF8StringEncoding error:nil] isEqualToString:@"Qt drag contents"]);
        CHECK(chmod(qtTarget.fileSystemRepresentation, 0000) == 0);
        CHECK(me_file_drop_copy_file(qtSource.UTF8String, qtTarget.UTF8String, nullptr, Completed));
        WaitForWrites();
        CHECK(chmod(qtTarget.fileSystemRepresentation, 0755) == 0);
        CHECK(permissionSources.count == 1); // A destination error must not request source-folder access.
        me_file_drop_authorize_folder(qtSource.UTF8String, root.UTF8String, nullptr,
            "title", "message", "allow", "wrong folder", nullptr, FolderCancelled);
        CHECK(cancelledGrants == 1); // Closing the owner does not leave a pending authorization.
        [board releaseGlobally];
        printf("File drop native checks passed\n");
    }
    return 0;
}
