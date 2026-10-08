#import <AppKit/AppKit.h>
#include <pwd.h>
#include <unistd.h>
#import <Security/Security.h>
#import <Quartz/Quartz.h>
#import <CoreServices/CoreServices.h>
#include <sys/xattr.h>
#include <sys/stat.h>
#include <limits.h>
#include <vector>

@interface MEPreviewSource : NSObject <QLPreviewPanelDataSource>
@property(nonatomic, strong) NSURL *url;
@end
@implementation MEPreviewSource
- (NSInteger)numberOfPreviewItemsInPreviewPanel:(QLPreviewPanel *)panel { return 1; }
- (id<QLPreviewItem>)previewPanel:(QLPreviewPanel *)panel previewItemAtIndex:(NSInteger)index { return self.url; }
@end
static MEPreviewSource *MEPreview;

static char *MEString(NSString *value) { return value ? strdup(value.UTF8String) : nullptr; }
// Resolve existing prefixes with POSIX realpath. Foundation can shorten /private/tmp
// only for existing files, which gives new children a different authorization root.
static NSString *MERealPath(NSString *path, int depth = 0) {
    if (depth > 40) return nil;
    char *resolved = realpath(path.fileSystemRepresentation, nullptr);
    if (resolved) {
        NSString *value = [NSString stringWithUTF8String:resolved]; free(resolved); return value;
    }
    NSArray<NSString *> *parts = path.pathComponents;
    NSString *current = @"/";
    for (NSUInteger index = 1; index < parts.count; index++) {
        NSString *candidate = [current stringByAppendingPathComponent:parts[index]];
        struct stat status;
        if (lstat(candidate.fileSystemRepresentation, &status) == 0 && S_ISLNK(status.st_mode)) {
            char target[PATH_MAX + 1];
            ssize_t size = readlink(candidate.fileSystemRepresentation, target, PATH_MAX);
            if (size < 0 || size == PATH_MAX) return nil;
            target[size] = 0;
            NSString *link = [NSString stringWithUTF8String:target];
            NSString *next = link.isAbsolutePath ? link : [current stringByAppendingPathComponent:link];
            for (NSUInteger remaining = index + 1; remaining < parts.count; remaining++)
                next = [next stringByAppendingPathComponent:parts[remaining]];
            return MERealPath(next.stringByStandardizingPath, depth + 1);
        }
        current = candidate;
    }
    return current;
}
extern "C" {
char *me_user_home() { return strdup(getpwuid(getuid())->pw_dir); }
char *me_temporary_directory() { @autoreleasepool { return MEString(NSTemporaryDirectory()); } }
char *me_plist_value(const char *path, const char *key) {
    @autoreleasepool {
        NSDictionary *plist = [NSDictionary dictionaryWithContentsOfURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:path]]];
        return MEString([plist[[NSString stringWithUTF8String:key]] description]);
    }
}
bool me_open_file(const char *path, const char *bundle) {
    @autoreleasepool {
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        if (!bundle) return [NSWorkspace.sharedWorkspace openURL:url];
        NSURL *application = [NSWorkspace.sharedWorkspace URLForApplicationWithBundleIdentifier:[NSString stringWithUTF8String:bundle]];
        if (!application) return false;
        [NSWorkspace.sharedWorkspace openURLs:@[url] withApplicationAtURL:application
            configuration:[NSWorkspaceOpenConfiguration configuration] completionHandler:nil];
        return true;
    }
}
void me_reveal_file(const char *path) {
    @autoreleasepool { [NSWorkspace.sharedWorkspace activateFileViewerSelectingURLs:@[[NSURL fileURLWithPath:[NSString stringWithUTF8String:path]]]]; }
}
void me_quicklook_show(const char *path) {
    @autoreleasepool {
        MEPreview = [MEPreviewSource new]; MEPreview.url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        QLPreviewPanel *panel = QLPreviewPanel.sharedPreviewPanel; panel.dataSource = MEPreview;
        [panel reloadData]; [panel makeKeyAndOrderFront:nil];
    }
}
bool me_quicklook_visible() { return QLPreviewPanel.sharedPreviewPanelExists && QLPreviewPanel.sharedPreviewPanel.visible; }
void me_quicklook_close() {
    if (QLPreviewPanel.sharedPreviewPanelExists) { [QLPreviewPanel.sharedPreviewPanel orderOut:nil]; QLPreviewPanel.sharedPreviewPanel.dataSource = nil; }
    MEPreview = nil;
}
char *me_app_icon(const char *path, int size) {
    @autoreleasepool {
        NSImage *icon = [NSWorkspace.sharedWorkspace iconForFile:[NSString stringWithUTF8String:path]];
        if (!icon) return nullptr;
        NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithBitmapDataPlanes:nil pixelsWide:size pixelsHigh:size
            bitsPerSample:8 samplesPerPixel:4 hasAlpha:YES isPlanar:NO colorSpaceName:NSDeviceRGBColorSpace bytesPerRow:0 bitsPerPixel:0];
        NSGraphicsContext *context = [NSGraphicsContext graphicsContextWithBitmapImageRep:bitmap];
        [NSGraphicsContext saveGraphicsState];
        NSGraphicsContext.currentContext = context;
        [icon drawInRect:NSMakeRect(0, 0, size, size) fromRect:NSZeroRect operation:NSCompositingOperationSourceOver fraction:1.0];
        [NSGraphicsContext restoreGraphicsState];
        return MEString([[bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}] base64EncodedStringWithOptions:0]);
    }
}
char *me_app_path(const char *bundle) {
    @autoreleasepool { return MEString([NSWorkspace.sharedWorkspace URLForApplicationWithBundleIdentifier:[NSString stringWithUTF8String:bundle]].path); }
}
char *me_default_app(const char *path) {
    @autoreleasepool { return MEString([NSWorkspace.sharedWorkspace URLForApplicationToOpenURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:path]]].path); }
}
char *me_registered_apps(const char *path) {
    @autoreleasepool {
        NSMutableArray *apps = [NSMutableArray array];
        for (NSURL *url in [NSWorkspace.sharedWorkspace URLsForApplicationsToOpenURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:path]]]) {
            NSBundle *bundle = [NSBundle bundleWithURL:url];
            if (bundle.bundleIdentifier) [apps addObject:@{@"name":url.URLByDeletingPathExtension.lastPathComponent,
                @"bundleId":bundle.bundleIdentifier, @"appPath":url.path}];
        }
        NSData *json = [NSJSONSerialization dataWithJSONObject:apps options:0 error:nil];
        return MEString([[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding]);
    }
}
char *me_metadata(const char *path) {
    @autoreleasepool {
        MDItemRef item = MDItemCreate(kCFAllocatorDefault, (__bridge CFStringRef)[NSString stringWithUTF8String:path]);
        if (!item) return nullptr;
        NSArray *names = CFBridgingRelease(MDItemCopyAttributeNames(item));
        NSDictionary *attributes = CFBridgingRelease(MDItemCopyAttributes(item, (__bridge CFArrayRef)names));
        CFRelease(item);
        NSMutableString *text = [NSMutableString string];
        for (NSString *key in attributes) [text appendFormat:@"%@ = %@\n", key, attributes[key]];
        return MEString(text);
    }
}
char *me_owner_group(const char *path) {
    @autoreleasepool {
        NSDictionary *attributes = [NSFileManager.defaultManager attributesOfItemAtPath:[NSString stringWithUTF8String:path] error:nil];
        return MEString([NSString stringWithFormat:@"%@ %@", attributes[NSFileOwnerAccountName] ?: @"--", attributes[NSFileGroupOwnerAccountName] ?: @"--"]);
    }
}
char *me_xattr_names(const char *path) {
    @autoreleasepool {
        ssize_t size = listxattr(path, nullptr, 0, 0); if (size <= 0) return nullptr;
        std::vector<char> bytes(size); size = listxattr(path, bytes.data(), bytes.size(), 0); if (size < 0) return nullptr;
        NSMutableArray *names = [NSMutableArray array];
        for (ssize_t offset = 0; offset < size; offset += strlen(bytes.data() + offset) + 1)
            [names addObject:[NSString stringWithUTF8String:bytes.data() + offset]];
        return MEString([names componentsJoinedByString:@"\n"]);
    }
}
char *me_storage_path(bool cache) {
    @autoreleasepool {
        return MEString(NSSearchPathForDirectoriesInDomains(cache ? NSCachesDirectory : NSApplicationSupportDirectory,
                                                          NSUserDomainMask, YES).firstObject);
    }
}
char *me_pick_test_directory(const char *path) {
    @autoreleasepool {
        NSOpenPanel *panel = [NSOpenPanel openPanel];
        panel.canChooseDirectories = YES; panel.canChooseFiles = NO; panel.allowsMultipleSelection = NO;
        panel.directoryURL = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        panel.message = @"选择本次临时测试文件夹以验证真实 App Sandbox。";
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        [NSApp activateIgnoringOtherApps:YES];
        if ([panel runModal] != NSModalResponseOK) return nullptr;
        if (![panel.URL.path.stringByStandardizingPath isEqualToString:[NSString stringWithUTF8String:path].stringByStandardizingPath]) return nullptr;
        NSData *data = [panel.URL bookmarkDataWithOptions:NSURLBookmarkCreationWithSecurityScope
            includingResourceValuesForKeys:nil relativeToURL:nil error:nil];
        return MEString([data base64EncodedStringWithOptions:0]);
    }
}
char *me_bookmark_create(const char *path, bool explicitScope) {
    @autoreleasepool {
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        NSError *error = nil;
        NSData *data = [url bookmarkDataWithOptions:explicitScope ? NSURLBookmarkCreationWithSecurityScope : 0
                    includingResourceValuesForKeys:nil relativeToURL:nil error:&error];
        return MEString([data base64EncodedStringWithOptions:0]);
    }
}
void *me_bookmark_open(const char *bookmark, char **path, char **refreshed, bool *stale) {
    @autoreleasepool {
        NSData *data = [[NSData alloc] initWithBase64EncodedString:[NSString stringWithUTF8String:bookmark] options:0];
        BOOL wasStale = NO;
        NSURL *url = [NSURL URLByResolvingBookmarkData:data
            options:NSURLBookmarkResolutionWithSecurityScope | NSURLBookmarkResolutionWithoutUI
            relativeToURL:nil bookmarkDataIsStale:&wasStale error:nil];
        *stale = wasStale;
        if (!url || ![url startAccessingSecurityScopedResource]) return nullptr;
        *path = MEString(url.path);
        if (wasStale) *refreshed = MEString([[url bookmarkDataWithOptions:NSURLBookmarkCreationWithSecurityScope
            includingResourceValuesForKeys:nil relativeToURL:nil error:nil] base64EncodedStringWithOptions:0]);
        return (__bridge_retained void *)url;
    }
}
void me_bookmark_close(void *scope) {
    NSURL *url = (__bridge_transfer NSURL *)scope;
    [url stopAccessingSecurityScopedResource];
}
char *me_real_path(const char *path) {
    @autoreleasepool { return MEString(MERealPath([NSString stringWithUTF8String:path])); }
}
char *me_trash(const char *path) {
    @autoreleasepool {
        NSError *error = nil;
        if ([[NSFileManager defaultManager] trashItemAtURL:[NSURL fileURLWithPath:[NSString stringWithUTF8String:path]]
            resultingItemURL:nil error:&error]) return nullptr;
        return MEString(error.localizedDescription ?: @"无法移入废纸篓。");
    }
}
bool me_eject_volume(const char *path) {
    @autoreleasepool {
        NSURL *url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
        return [NSWorkspace.sharedWorkspace unmountAndEjectDeviceAtURL:url error:nil];
    }
}
void me_string_free(void *string) { free(string); }
char *me_secret_read(const char *service, const char *account, int *status) {
    @autoreleasepool {
        NSDictionary *query = @{(__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
            (__bridge id)kSecAttrService: [NSString stringWithUTF8String:service],
            (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:account],
            (__bridge id)kSecReturnData: @YES, (__bridge id)kSecMatchLimit: (__bridge id)kSecMatchLimitOne};
        CFTypeRef result = nullptr;
        *status = SecItemCopyMatching((__bridge CFDictionaryRef)query, &result);
        if (*status != errSecSuccess) return nullptr;
        NSData *data = CFBridgingRelease(result);
        return MEString([[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding]);
    }
}
int me_secret_save(const char *service, const char *account, const char *secret) {
    @autoreleasepool {
        NSMutableDictionary *query = [@{(__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
            (__bridge id)kSecAttrService: [NSString stringWithUTF8String:service],
            (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:account]} mutableCopy];
        NSData *data = [[NSString stringWithUTF8String:secret] dataUsingEncoding:NSUTF8StringEncoding];
        OSStatus status = SecItemUpdate((__bridge CFDictionaryRef)query,
            (__bridge CFDictionaryRef)@{(__bridge id)kSecValueData: data});
        if (status != errSecItemNotFound) return status;
        query[(__bridge id)kSecValueData] = data;
        return SecItemAdd((__bridge CFDictionaryRef)query, nullptr);
    }
}
int me_secret_delete(const char *service, const char *account) {
    @autoreleasepool {
        return SecItemDelete((__bridge CFDictionaryRef)@{(__bridge id)kSecClass: (__bridge id)kSecClassGenericPassword,
            (__bridge id)kSecAttrService: [NSString stringWithUTF8String:service],
            (__bridge id)kSecAttrAccount: [NSString stringWithUTF8String:account]});
    }
}
}
