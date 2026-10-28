#import <AppKit/AppKit.h>
#import <NetFS/NetFS.h>
#import <dns_sd.h>
#import <arpa/inet.h>
#import <Network/Network.h>
#import <sys/mount.h>

using NetworkCallback = void (*)(long long, const char *);
static NSMutableDictionary<NSNumber *, NSValue *> *mountRequests;
static NSString *JSONText(id value) {
    NSData *data = [NSJSONSerialization dataWithJSONObject:value options:0 error:nil];
    return [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding] ?: @"{}";
}

@class MENetworkBrowser;
@interface MEResolution : NSObject
@property DNSServiceRef service;
@property NSString *key;
@property NSString *name;
@property (weak) MENetworkBrowser *owner;
@end
@implementation MEResolution
@end
@interface MENetworkBrowser : NSObject
@property nw_browser_t browser;
@property NSMutableDictionary<NSString *, MEResolution *> *resolvers;
@property NetworkCallback callback;
@property long long identifier;
@end
@implementation MENetworkBrowser
@end
static NSMutableDictionary<NSNumber *, MENetworkBrowser *> *browsers;
static dispatch_queue_t discoveryQueue() {
    static dispatch_queue_t queue;
    static dispatch_once_t once;
    dispatch_once(&once, ^{ queue = dispatch_queue_create("com.macexplorer.network-discovery", DISPATCH_QUEUE_SERIAL); });
    return queue;
}
static void resolvedService(DNSServiceRef service, DNSServiceFlags flags, uint32_t interfaceIndex, DNSServiceErrorType error,
    const char *fullname, const char *host, uint16_t port, uint16_t txtLength, const unsigned char *txt, void *context) {
    @autoreleasepool {
        MEResolution *resolution = (__bridge MEResolution *)context;
        MENetworkBrowser *owner = resolution.owner;
        if (owner && !error) owner.callback(owner.identifier, JSONText(@{@"Id":resolution.key, @"Name":resolution.name,
            @"Host":[NSString stringWithUTF8String:host], @"Port":@(ntohs(port)), @"Removed":@NO}).UTF8String);
        if (resolution.service) { DNSServiceRefDeallocate(resolution.service); resolution.service = nullptr; }
    }
}
static void updateService(MENetworkBrowser *owner, nw_browse_result_t result, BOOL removed) {
    nw_endpoint_t endpoint=nw_browse_result_copy_endpoint(result);
    const char *name=nw_endpoint_get_bonjour_service_name(endpoint);
    const char *type=nw_endpoint_get_bonjour_service_type(endpoint);
    const char *domain=nw_endpoint_get_bonjour_service_domain(endpoint);
    if (!name || !type || !domain) return;
    NSString *key=[NSString stringWithFormat:@"%@|%@|%@",[NSString stringWithUTF8String:name],
        [NSString stringWithUTF8String:type],[NSString stringWithUTF8String:domain]];
    MEResolution *previous=owner.resolvers[key];
    if (previous.service) DNSServiceRefDeallocate(previous.service);
    [owner.resolvers removeObjectForKey:key];
    if (removed) { owner.callback(owner.identifier, JSONText(@{@"Id":key,@"Removed":@YES}).UTF8String); return; }
    MEResolution *resolution=[MEResolution new]; resolution.key=key; resolution.name=[NSString stringWithUTF8String:name]; resolution.owner=owner;
    DNSServiceRef ref=nullptr;
    DNSServiceErrorType status=DNSServiceResolve(&ref,0,kDNSServiceInterfaceIndexAny,name,type,domain,resolvedService,(__bridge void *)resolution);
    if (!status) {
        resolution.service=ref; owner.resolvers[key]=resolution;
        status=DNSServiceSetDispatchQueue(ref,discoveryQueue());
        if (status) { DNSServiceRefDeallocate(ref); resolution.service=nullptr; [owner.resolvers removeObjectForKey:key]; }
    }
}

extern "C" {
void me_network_mount(long long identifier, const char *address, const char *user, const char *password, NetworkCallback callback) {
    NSString *urlText = [NSString stringWithUTF8String:address];
    NSString *username = [NSString stringWithUTF8String:user];
    NSString *secret = [NSString stringWithUTF8String:password];
    dispatch_async(dispatch_get_main_queue(), ^{
        if (!mountRequests) mountRequests = [NSMutableDictionary new];
        AsyncRequestID request = nullptr;
        NSURL *mountURL = [NSURL URLWithString:urlText];
        BOOL webdav = [mountURL.scheme isEqualToString:@"http"] || [mountURL.scheme isEqualToString:@"https"];
        BOOL explicitLogin = (webdav || mountURL.path.length > 1) && (username.length || mountURL.user.length);
        NSMutableDictionary *openOptions = [@{(__bridge NSString *)kNAUIOptionKey:
            (__bridge NSString *)(explicitLogin ? kNAUIOptionNoUI : kNAUIOptionAllowUI)} mutableCopy];
        if (getenv("MACEXPLORER_TEST_ROOT")) openOptions[(__bridge NSString *)kNetFSAllowLoopbackKey] = @YES;
        int result = NetFSMountURLAsync((__bridge CFURLRef)[NSURL URLWithString:urlText], nullptr,
            username.length ? (__bridge CFStringRef)username : nullptr,
            secret.length ? (__bridge CFStringRef)secret : nullptr,
            (__bridge CFMutableDictionaryRef)openOptions, nullptr, &request, dispatch_get_main_queue(),
            ^(int status, AsyncRequestID completed, CFArrayRef points) {
                [mountRequests removeObjectForKey:@(identifier)];
                callback(identifier, JSONText(@{@"Status":@(status), @"Paths":points ? (__bridge NSArray *)points : @[]}).UTF8String);
            });
        if (result) callback(identifier, JSONText(@{@"Status":@(result), @"Paths":@[]}).UTF8String);
        else mountRequests[@(identifier)] = [NSValue valueWithPointer:request];
    });
}
void me_network_mount_cancel(long long identifier) {
    dispatch_async(dispatch_get_main_queue(), ^{
        NSValue *request = mountRequests[@(identifier)];
        if (request) { NetFSMountURLCancel(request.pointerValue); [mountRequests removeObjectForKey:@(identifier)]; }
    });
}
char *me_network_volumes() {
    @autoreleasepool {
        // Foundation's mounted-volume list can lag behind NetFS's success callback.
        // Snapshot the kernel table so a newly mounted volume is immediately identifiable.
        struct statfs *mounts = nullptr;
        int mountCount = getmntinfo_r_np(&mounts, MNT_NOWAIT);
        NSArray *keys = @[NSURLVolumeIsRemovableKey, NSURLVolumeNameKey, NSURLVolumeURLForRemountingKey];
        NSMutableArray *result = [NSMutableArray new];
        for (int i = 0; i < mountCount; i++) {
            const struct statfs &mount = mounts[i];
            NSString *path = [NSString stringWithUTF8String:mount.f_mntonname];
            BOOL network = (mount.f_flags & MNT_LOCAL) == 0;
            if ([path isEqualToString:@"/"] || (mount.f_flags & MNT_DONTBROWSE)) continue;
            if (!network && ![path hasPrefix:@"/Volumes/"]) continue;
            NSURL *url = [NSURL fileURLWithPath:path isDirectory:YES];
            BOOL webdav = strcmp(mount.f_fstypename, "webdav") == 0;
            // Avoid filesystem queries against a provisional or unresponsive WebDAV mount.
            NSDictionary *values = webdav ? @{} : [url resourceValuesForKeys:keys error:nil];
            NSURL *source = values[NSURLVolumeURLForRemountingKey];
            // WebDAV's mount source preserves the full endpoint, including its port and path.
            if (webdav)
                source = [NSURL URLWithString:[NSString stringWithUTF8String:mount.f_mntfromname]];
            if (network && !source) source = CFBridgingRelease(NetFSCopyURLForRemountingVolume((__bridge CFURLRef)url));
            NSURLComponents *safe = source ? [NSURLComponents componentsWithURL:source resolvingAgainstBaseURL:NO] : nil;
            safe.password = nil;
            [result addObject:@{@"Path":path, @"DisplayName":values[NSURLVolumeNameKey] ?: path.lastPathComponent,
                @"IsExternal":@YES, @"IsRemovable":@([values[NSURLVolumeIsRemovableKey] boolValue]), @"IsNetwork":@(network),
                @"SourceUrl":safe.string ?: @"", @"MountId":[NSString stringWithFormat:@"%d:%d", mount.f_fsid.val[0], mount.f_fsid.val[1]]}];
        }
        free(mounts);
        return strdup(JSONText(result).UTF8String);
    }
}
void me_network_browse(long long identifier, NetworkCallback callback) {
    dispatch_async(discoveryQueue(), ^{
        if (!browsers) browsers=[NSMutableDictionary new];
        MENetworkBrowser *browser=[MENetworkBrowser new]; browser.identifier=identifier; browser.callback=callback;
        browser.resolvers=[NSMutableDictionary new]; browsers[@(identifier)]=browser;
        nw_parameters_t parameters=nw_parameters_create_secure_tcp(NW_PARAMETERS_DISABLE_PROTOCOL,NW_PARAMETERS_DEFAULT_CONFIGURATION);
        nw_parameters_set_include_peer_to_peer(parameters,true);
        browser.browser=nw_browser_create(nw_browse_descriptor_create_bonjour_service("_smb._tcp","local."),parameters);
        __weak MENetworkBrowser *weakBrowser=browser;
        nw_browser_set_state_changed_handler(browser.browser, ^(nw_browser_state_t state,nw_error_t error) {
            MENetworkBrowser *owner=weakBrowser;
            if (!owner) return;
            if (state==nw_browser_state_ready) { owner.callback(owner.identifier,JSONText(@{@"Ready":@YES}).UTF8String); return; }
            if (!error) return;
            int code=nw_error_get_error_code(error);
            if (state==nw_browser_state_failed || state==nw_browser_state_waiting)
                owner.callback(owner.identifier,JSONText(@{@"Error":@(code)}).UTF8String);
        });
        nw_browser_set_browse_results_changed_handler(browser.browser, ^(nw_browse_result_t oldResult,nw_browse_result_t newResult,bool complete) {
            MENetworkBrowser *owner=weakBrowser;
            if (!owner) return;
            if (newResult) updateService(owner,newResult,NO);
            else if (oldResult) updateService(owner,oldResult,YES);
        });
        nw_browser_set_queue(browser.browser,discoveryQueue());
        nw_browser_start(browser.browser);
    });
}
void me_network_browse_stop(long long identifier) {
    dispatch_async(discoveryQueue(), ^{
        MENetworkBrowser *browser=browsers[@(identifier)];
        if (browser.browser) nw_browser_cancel(browser.browser);
        for (MEResolution *resolution in browser.resolvers.allValues)
            if (resolution.service) DNSServiceRefDeallocate(resolution.service);
        [browsers removeObjectForKey:@(identifier)];
    });
}

bool me_network_open(const char *address) {
    @autoreleasepool {
        NSURL *url = [NSURL URLWithString:[NSString stringWithUTF8String:address]];
        // HTTP(S) must go to Finder's Connect to Server handler, not the default browser.
        if ([url.scheme isEqualToString:@"http"] || [url.scheme isEqualToString:@"https"])
            return [NSWorkspace.sharedWorkspace openURLs:@[url] withAppBundleIdentifier:@"com.apple.finder"
                options:NSWorkspaceLaunchDefault additionalEventParamDescriptor:nil launchIdentifiers:nil];
        return [NSWorkspace.sharedWorkspace openURL:url];
    }
}
}
