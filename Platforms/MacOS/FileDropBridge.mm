#import <AppKit/AppKit.h>
#import <objc/runtime.h>
#include <errno.h>

// Keep Avalonia.Native 12.0.4 routing/hit testing and expose the current
// AppKit pasteboard for Qt file URLs, file promises, and image-only drags.
static NSPasteboard *MECurrentDropPasteboard;
static IMP MEOriginalDragEvent;
typedef void (*MEDropCallback)(void *, const char *, const char *, const char *, const char *);
typedef void (*MEFolderGrantCallback)(void *, const char *, const char *);

static NSDragOperation MEDragEvent(id view, SEL selector, int type, id<NSDraggingInfo> info) {
    NSPasteboard *previous = MECurrentDropPasteboard;
    MECurrentDropPasteboard = info.draggingPasteboard;
    @try {
        return ((NSDragOperation (*)(id, SEL, int, id<NSDraggingInfo>))MEOriginalDragEvent)(view, selector, type, info);
    } @finally {
        MECurrentDropPasteboard = previous;
    }
}

extern "C" __attribute__((visibility("default")))
bool me_file_drop_enable(void *handle) {
    if (!handle || !NSThread.isMainThread) return false;
    NSView *view = (__bridge NSView *)handle;
    Class avnView = NSClassFromString(@"AvnView");
    SEL selector = NSSelectorFromString(@"triggerAvnDragEvent:info:");
    Method method = class_getInstanceMethod(avnView, selector);
    if (!method || ![view isKindOfClass:avnView]) return false;
    if (!MEOriginalDragEvent) MEOriginalDragEvent = method_setImplementation(method, (IMP)MEDragEvent);
    NSMutableArray *types = [view.registeredDraggedTypes mutableCopy];
    [types addObjectsFromArray:NSFilePromiseReceiver.readableDraggedTypes];
    [types addObjectsFromArray:@[NSPasteboardTypePNG, NSPasteboardTypeTIFF, @"public.jpeg", @"com.compuserve.gif"]];
    [view registerForDraggedTypes:types];
    return true;
}

static int MEDropKind(NSPasteboard *pasteboard) {
    if (!pasteboard) return 0;
    if ([pasteboard availableTypeFromArray:NSFilePromiseReceiver.readableDraggedTypes]) return 1;
    // An ordinary file may advertise its thumbnail as well. Keep the file path.
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    bool hasFileURLs = [pasteboard availableTypeFromArray:@[NSPasteboardTypeFileURL, NSFilenamesPboardType]] != nil;
#pragma clang diagnostic pop
    if (hasFileURLs && [pasteboard availableTypeFromArray:@[@"com.trolltech.anymime.text--uri-list"]]) return 3;
    if (hasFileURLs) return 0;
    return [pasteboard availableTypeFromArray:@[NSPasteboardTypePNG, @"public.jpeg", @"com.compuserve.gif", NSPasteboardTypeTIFF]] ? 2 : 0;
}

extern "C" __attribute__((visibility("default")))
int me_file_drop_kind() { return MEDropKind(MECurrentDropPasteboard); }

@interface MEDropAccess : NSObject
@property(nonatomic, strong) NSArray<NSURL *> *urls;
@property(nonatomic, strong) NSArray<NSURL *> *startedURLs;
@end
@implementation MEDropAccess
- (void)dealloc { for (NSURL *url in _startedURLs) [url stopAccessingSecurityScopedResource]; }
@end

static MEDropAccess *MEAcquireDropURLs(NSArray<NSURL *> *urls) {
    MEDropAccess *access = [MEDropAccess new];
    access.urls = urls;
    NSMutableArray *started = [NSMutableArray new];
    for (NSURL *url in urls)
        if (url.isFileURL && [url startAccessingSecurityScopedResource]) [started addObject:url];
    access.startedURLs = started;
    return access;
}

static NSArray<NSURL *> *MEReadOriginalDropURLs(NSPasteboard *pasteboard) {
    NSMutableArray *urls = [NSMutableArray new];
    for (NSURL *url in [pasteboard readObjectsForClasses:@[NSURL.class] options:nil])
        if (url.isFileURL && ![urls containsObject:url]) [urls addObject:url];
    if (urls.count) return urls;
    NSURL *singleURL = [NSURL URLFromPasteboard:pasteboard];
    if (singleURL.isFileURL && ![urls containsObject:singleURL]) [urls addObject:singleURL];
    return urls;
}

extern "C" __attribute__((visibility("default")))
void *me_file_drop_begin_access() {
    if (!MECurrentDropPasteboard) return nullptr;
    // Retain the original pasteboard URLs, not reconstructed path URLs: AppKit
    // can attach temporary access to the object. Keep it through async copies.
    return (__bridge_retained void *)MEAcquireDropURLs(MEReadOriginalDropURLs(MECurrentDropPasteboard));
}

extern "C" __attribute__((visibility("default")))
void me_file_drop_end_access(void *handle) { if (handle) CFBridgingRelease(handle); }

static NSArray<NSString *> *MEReadDroppedPaths(NSPasteboard *pasteboard) {
    if (!pasteboard) return @[];
    NSMutableOrderedSet<NSString *> *paths = [NSMutableOrderedSet new];
    void (^addURL)(NSURL *) = ^(NSURL *url) {
        if (!url.isFileURL || (url.host.length && ![url.host isEqualToString:@"localhost"])) return;
        NSString *path = url.filePathURL.path;
        if (path.isAbsolutePath && [path rangeOfString:[NSString stringWithFormat:@"%C", (unichar)0]].location == NSNotFound)
            [paths addObject:path.stringByStandardizingPath];
    };
    for (NSURL *url in [pasteboard readObjectsForClasses:@[NSURL.class]
        options:@{NSPasteboardURLReadingFileURLsOnlyKey:@YES}]) addURL(url);
    if (paths.count) return paths.array;
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    id legacy = [pasteboard propertyListForType:NSFilenamesPboardType];
#pragma clang diagnostic pop
    if ([legacy isKindOfClass:NSArray.class])
        for (id path in legacy)
            if ([path isKindOfClass:NSString.class] && [path isAbsolutePath]) addURL([NSURL fileURLWithPath:path]);
    if (paths.count) return paths.array;
    // Qt apps may publish an empty/unreadable public.file-url while supplying
    // the actual URLs only through their URI-list pasteboard flavor.
    for (NSString *type in @[NSPasteboardTypeFileURL, @"com.trolltech.anymime.text--uri-list", @"Apple URL pasteboard type"]) {
        for (NSString *line in [[pasteboard stringForType:type] componentsSeparatedByCharactersInSet:NSCharacterSet.newlineCharacterSet]) {
            NSString *value = [line stringByTrimmingCharactersInSet:NSCharacterSet.whitespaceAndNewlineCharacterSet];
            if (value.length && ![value hasPrefix:@"#"]) addURL([NSURL URLWithString:value]);
        }
        if (paths.count) break;
    }
    return paths.array;
}

extern "C" __attribute__((visibility("default")))
char *me_file_drop_copy_paths() {
    @try {
        NSArray *paths = MEReadDroppedPaths(MECurrentDropPasteboard);
        if (!paths.count) return nullptr;
        NSData *json = [NSJSONSerialization dataWithJSONObject:paths options:0 error:nil];
        NSString *text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
        return text ? strdup(text.UTF8String) : nullptr;
    } @catch (NSException *exception) { return nullptr; }
}

extern "C" __attribute__((visibility("default")))
void me_file_drop_free_paths(void *paths) { free(paths); }

@interface MEDropStaging : NSObject
@property(nonatomic, strong) NSURL *url;
@end
@implementation MEDropStaging
- (void)dealloc { if (_url) [NSFileManager.defaultManager removeItemAtURL:_url error:nil]; }
@end

static MEDropStaging *MECreateDropStaging(NSError **error) {
    MEDropStaging *staging = [MEDropStaging new];
    staging.url = [NSURL fileURLWithPath:[NSTemporaryDirectory() stringByAppendingPathComponent:
        [@"MacExplorer-drop-" stringByAppendingString:NSUUID.UUID.UUIDString]] isDirectory:YES];
    return [NSFileManager.defaultManager createDirectoryAtURL:staging.url withIntermediateDirectories:NO attributes:nil error:error]
        ? staging : nil;
}

static NSOperationQueue *MEDropQueue() {
    static NSOperationQueue *queue;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ queue = [NSOperationQueue new]; queue.maxConcurrentOperationCount = 2; });
    return queue;
}

static NSURL *MECommitDroppedFile(NSURL *source, NSURL *directory, NSError **error) {
    NSString *name = source.lastPathComponent;
    NSString *extension = name.pathExtension;
    NSString *stem = extension.length ? name.stringByDeletingPathExtension : name;
    for (NSUInteger suffix = 1; ; suffix++) {
        NSString *candidate = suffix == 1 ? name : [NSString stringWithFormat:@"%@ %lu%@%@", stem,
            (unsigned long)suffix, extension.length ? @"." : @"", extension];
        NSURL *destination = [directory URLByAppendingPathComponent:candidate];
        NSError *moveError = nil;
        // moveItem does not replace an existing destination, including a race.
        if ([NSFileManager.defaultManager moveItemAtURL:source toURL:destination error:&moveError]) return destination;
        if (moveError.code != NSFileWriteFileExistsError || ![moveError.domain isEqualToString:NSCocoaErrorDomain]) {
            if (error) *error = moveError;
            return nil;
        }
    }
}

static void MEReceivePromises(NSArray<NSFilePromiseReceiver *> *receivers, MEDropStaging *staging,
    NSURL *target, void *context, MEDropCallback callback) {
    for (NSFilePromiseReceiver *receiver in receivers) {
        [receiver receivePromisedFilesAtDestination:staging.url options:@{} operationQueue:MEDropQueue()
            reader:^(NSURL *url, NSError *error) {
                NSURL *saved = nil;
                if (!error) {
                    if (![url.URLByStandardizingPath.URLByDeletingLastPathComponent.path isEqualToString:staging.url.path])
                        error = [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadInvalidFileNameError userInfo:nil];
                    else saved = MECommitDroppedFile(url, target, &error);
                }
                callback(context, target.path.UTF8String, saved.path.UTF8String, error.localizedDescription.UTF8String, nullptr);
            }];
    }
}

static bool MEReadPermissionError(NSError *error) {
    if (!error) return false;
    if ([error.domain isEqualToString:NSCocoaErrorDomain] && error.code == NSFileReadNoPermissionError) return true;
    if ([error.domain isEqualToString:NSPOSIXErrorDomain] && (error.code == EPERM || error.code == EACCES)) return true;
    return MEReadPermissionError(error.userInfo[NSUnderlyingErrorKey]);
}

static bool MECopyDroppedContents(NSURL *source, NSURL *destination, NSError **error, bool *sourceDenied = nullptr) {
    NSNumber *directory = nil;
    if (![source getResourceValue:&directory forKey:NSURLIsDirectoryKey error:error]) {
        if (sourceDenied) *sourceDenied = MEReadPermissionError(*error);
        return false;
    }
    if (directory.boolValue) return [NSFileManager.defaultManager copyItemAtURL:source toURL:destination error:error];
    // Export bytes without inheriting the source application's protected metadata.
    NSInputStream *input = [NSInputStream inputStreamWithURL:source];
    [input open];
    NSOutputStream *output = [NSOutputStream outputStreamWithURL:destination append:NO];
    [output open];
    NSError *failure = input.streamError ?: output.streamError;
    uint8_t buffer[65536];
    while (!failure) {
        NSInteger count = [input read:buffer maxLength:sizeof(buffer)];
        if (count == 0) break;
        if (count < 0) {
            failure = input.streamError ?: [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadUnknownError userInfo:nil];
            break;
        }
        NSInteger offset = 0;
        while (offset < count) {
            NSInteger written = [output write:buffer + offset maxLength:(NSUInteger)(count - offset)];
            if (written <= 0) {
                failure = output.streamError ?: [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileWriteUnknownError userInfo:nil];
                break;
            }
            offset += written;
        }
    }
    failure = failure ?: input.streamError ?: output.streamError;
    if (sourceDenied) *sourceDenied = MEReadPermissionError(input.streamError);
    [input close]; [output close];
    if (failure) {
        [NSFileManager.defaultManager removeItemAtURL:destination error:nil];
        if (error) *error = failure;
    }
    return failure == nil;
}

static bool MEReceiveFileURLs(NSArray<NSURL *> *urls, MEDropStaging *staging,
    NSURL *target, void *context, MEDropCallback callback) {
    if (!urls.count) {
        NSError *error = [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadUnknownError userInfo:nil];
        callback(context, target.path.UTF8String, nullptr, error.localizedDescription.UTF8String, nullptr);
        return false;
    }
    MEDropAccess *access = MEAcquireDropURLs(urls);
    for (NSUInteger index = 0; index < access.urls.count; index++) {
        [MEDropQueue() addOperationWithBlock:^{
            // Keep the original pasteboard URLs and their scopes alive through the copy.
            NSURL *source = access.urls[index];
            NSString *path = source.filePathURL.path.stringByStandardizingPath;
            NSError *error = nil;
            NSURL *saved = nil;
            bool sourceDenied = false;
            if (!source.isFileURL || !path.isAbsolutePath
                || (source.host.length && [source.host caseInsensitiveCompare:@"localhost"] != NSOrderedSame)
                || [target.path isEqualToString:path]
                || [target.path hasPrefix:[path stringByAppendingString:@"/"]]) {
                error = [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileWriteInvalidFileNameError userInfo:nil];
            } else {
                NSURL *folder = [staging.url URLByAppendingPathComponent:NSUUID.UUID.UUIDString isDirectory:YES];
                if ([NSFileManager.defaultManager createDirectoryAtURL:folder withIntermediateDirectories:NO attributes:nil error:&error]) {
                    NSURL *copy = [folder URLByAppendingPathComponent:source.lastPathComponent];
                    if (MECopyDroppedContents(source, copy, &error, &sourceDenied))
                        saved = MECommitDroppedFile(copy, target, &error);
                }
            }
            if (getenv("MACEXPLORER_DROP_DIAGNOSTICS"))
                NSLog(@"[FileDrop] source=%@ saved=%@ error=%@", source, saved, error);
            callback(context, target.path.UTF8String, saved.path.UTF8String, error.localizedDescription.UTF8String,
                error && sourceDenied ? source.path.UTF8String : nullptr);
        }];
    }
    return true;
}

static bool MEReceiveQtFiles(NSPasteboard *pasteboard, MEDropStaging *staging,
    NSURL *target, void *context, MEDropCallback callback) {
    NSArray<NSURL *> *originalURLs = MEReadOriginalDropURLs(pasteboard);
    if (originalURLs.count) return MEReceiveFileURLs(originalURLs, staging, target, context, callback);
    NSMutableArray<NSURL *> *urls = [NSMutableArray new];
    for (NSString *path in MEReadDroppedPaths(pasteboard)) [urls addObject:[NSURL fileURLWithPath:path]];
    return MEReceiveFileURLs(urls, staging, target, context, callback);
}

static bool MEReceiveDrop(NSPasteboard *pasteboard, NSString *targetPath, void *context, MEDropCallback callback) {
    int kind = MEDropKind(pasteboard);
    if (!kind || !targetPath.isAbsolutePath || !callback) return false;
    NSURL *target = [NSURL fileURLWithPath:targetPath isDirectory:YES];
    NSError *error = nil;
    MEDropStaging *staging = MECreateDropStaging(&error);
    if (!staging) {
        callback(context, targetPath.UTF8String, nullptr, error.localizedDescription.UTF8String, nullptr);
        return false;
    }
    if (kind == 3) return MEReceiveQtFiles(pasteboard, staging, target, context, callback);
    if (kind == 1) {
        NSArray *receivers = [pasteboard readObjectsForClasses:@[NSFilePromiseReceiver.class] options:nil];
        if (!receivers.count) {
            NSError *readError = [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadUnknownError userInfo:nil];
            callback(context, targetPath.UTF8String, nullptr, readError.localizedDescription.UTF8String, nullptr);
            return false;
        }
        MEReceivePromises(receivers, staging, target, context, callback);
        return true;
    }
    NSString *type = [pasteboard availableTypeFromArray:@[NSPasteboardTypePNG, @"public.jpeg", @"com.compuserve.gif", NSPasteboardTypeTIFF]];
    NSData *data = [pasteboard dataForType:type];
    NSString *extension = [type isEqualToString:@"public.jpeg"] ? @"jpg" : [type isEqualToString:@"com.compuserve.gif"] ? @"gif" : @"png";
    if ([type isEqualToString:NSPasteboardTypeTIFF])
        data = [[NSBitmapImageRep imageRepWithData:data] representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
    if (!data.length) {
        NSError *readError = [NSError errorWithDomain:NSCocoaErrorDomain code:NSFileReadCorruptFileError userInfo:nil];
        callback(context, targetPath.UTF8String, nullptr, readError.localizedDescription.UTF8String, nullptr);
        return false;
    }
    [MEDropQueue() addOperationWithBlock:^{
        NSError *writeError = nil;
        NSURL *source = [staging.url URLByAppendingPathComponent:[@"Image." stringByAppendingString:extension]];
        NSURL *saved = [data writeToURL:source options:NSDataWritingWithoutOverwriting error:&writeError]
            ? MECommitDroppedFile(source, target, &writeError) : nil;
        callback(context, targetPath.UTF8String, saved.path.UTF8String, writeError.localizedDescription.UTF8String, nullptr);
    }];
    return true;
}

extern "C" __attribute__((visibility("default")))
bool me_file_drop_receive(const char *targetPath, void *context, MEDropCallback callback) {
    if (!NSThread.isMainThread || !targetPath) return false;
    @try { return MEReceiveDrop(MECurrentDropPasteboard, [NSString stringWithUTF8String:targetPath], context, callback); }
    @catch (NSException *exception) {
        if (callback) callback(context, targetPath, nullptr, exception.reason.UTF8String, nullptr);
        return false;
    }
}

extern "C" __attribute__((visibility("default")))
bool me_file_drop_copy_file(const char *sourcePath, const char *targetPath, void *context, MEDropCallback callback) {
    if (!NSThread.isMainThread || !sourcePath || !targetPath || !callback) return false;
    NSString *source = [NSString stringWithUTF8String:sourcePath];
    NSString *target = [NSString stringWithUTF8String:targetPath];
    if (!source.isAbsolutePath || !target.isAbsolutePath) return false;
    NSError *error = nil;
    MEDropStaging *staging = MECreateDropStaging(&error);
    if (!staging) {
        callback(context, targetPath, nullptr, error.localizedDescription.UTF8String, nullptr);
        return false;
    }
    return MEReceiveFileURLs(@[[NSURL fileURLWithPath:source]], staging,
        [NSURL fileURLWithPath:target isDirectory:YES], context, callback);
}

extern "C" __attribute__((visibility("default")))
void me_file_drop_authorize_folder(const char *sourcePath, const char *folderPath, void *viewHandle,
    const char *title, const char *message, const char *prompt, const char *invalidSelection,
    void *context, MEFolderGrantCallback callback) {
    if (!callback) return;
    NSWindow *window = viewHandle ? ((__bridge NSView *)viewHandle).window : nil;
    if (!NSThread.isMainThread || !sourcePath || !folderPath || !window.visible) {
        callback(context, nullptr, nullptr);
        return;
    }
    NSString *selectionError = [NSString stringWithUTF8String:invalidSelection];
    NSURL *source = [NSURL fileURLWithPath:[NSString stringWithUTF8String:sourcePath]];
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    panel.canChooseDirectories = YES; panel.canChooseFiles = NO; panel.allowsMultipleSelection = NO;
    panel.directoryURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:folderPath] isDirectory:YES];
    panel.title = [NSString stringWithUTF8String:title];
    panel.message = [NSString stringWithUTF8String:message];
    panel.prompt = [NSString stringWithUTF8String:prompt];
    [NSApp activateIgnoringOtherApps:YES];
    [panel beginSheetModalForWindow:window completionHandler:^(NSModalResponse response) {
        if (response != NSModalResponseOK) { callback(context, nullptr, nullptr); return; }
        NSURL *folder = panel.URL;
        BOOL scoped = [folder startAccessingSecurityScopedResource];
        NSString *folderName = folder.URLByResolvingSymlinksInPath.path.stringByStandardizingPath;
        NSString *fileName = source.URLByResolvingSymlinksInPath.path.stringByStandardizingPath;
        if (![fileName isEqualToString:folderName] && ![fileName hasPrefix:([folderName hasSuffix:@"/"] ? folderName : [folderName stringByAppendingString:@"/"])]) {
            callback(context, nullptr, selectionError.UTF8String);
        } else {
            NSError *error = nil;
            NSData *bookmark = [folder bookmarkDataWithOptions:NSURLBookmarkCreationWithSecurityScope
                includingResourceValuesForKeys:nil relativeToURL:nil error:&error];
            callback(context, [bookmark base64EncodedStringWithOptions:0].UTF8String, error.localizedDescription.UTF8String);
        }
        if (scoped) [folder stopAccessingSecurityScopedResource];
    }];
}
