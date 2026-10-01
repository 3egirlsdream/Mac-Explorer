#import <AppKit/AppKit.h>
#include <dlfcn.h>
#include <cstdio>

// Initialize AppKit's sandbox identity before CoreCLR starts. The runtime and
// application remain the same self-contained build, using public hostfxr APIs.
int main(int argc, const char **argv) {
    @autoreleasepool {
        [NSApplication sharedApplication];
        [NSApp setActivationPolicy:NSApplicationActivationPolicyRegular];
        [NSApp finishLaunching];
        NSString *contents = NSBundle.mainBundle.bundlePath;
        NSString *runtime = [contents stringByAppendingPathComponent:@"Contents/Frameworks/libhostfxr.dylib"];
        void *library = dlopen(runtime.fileSystemRepresentation, RTLD_NOW | RTLD_LOCAL);
        if (!library) { fprintf(stderr, "Cannot load bundled hostfxr: %s\n", dlerror()); return 1; }
        using Main = int (*)(int, const char **, const char *, const char *, const char *);
        auto run = reinterpret_cast<Main>(dlsym(library, "hostfxr_main_startupinfo"));
        if (!run) { fprintf(stderr, "Bundled apphost startup API is unavailable.\n"); dlclose(library); return 1; }
        NSString *assembly = [contents stringByAppendingPathComponent:@"Contents/Resources/Managed/MacExplorer.dll"];
        NSString *host = [contents stringByAppendingPathComponent:@"Contents/MacOS/MacExplorer"];
        NSString *runtimeRoot = [contents stringByAppendingPathComponent:@"Contents/Frameworks"];
        // Use the same startup entry as the standalone .NET apphost for this
        // self-contained deployment; component-hosting APIs require frameworks.
        int result = run(argc, argv, host.fileSystemRepresentation, runtimeRoot.fileSystemRepresentation, assembly.fileSystemRepresentation);
        // CoreCLR may retain runtime code until process exit; do not dlclose it.
        return result;
    }
}
