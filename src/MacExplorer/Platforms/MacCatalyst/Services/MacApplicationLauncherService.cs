using System.Runtime.InteropServices;
using Foundation;
using ObjCRuntime;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IApplicationLauncherService.
/// Uses NSWorkspace native API for file opening (Process.Start can fail silently in sandbox).
/// </summary>
public class MacApplicationLauncherService : IApplicationLauncherService
{
    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool objc_msgSend_bool(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(IntPtr receiver, IntPtr selector,
        IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);

    private static IntPtr GetSharedWorkspace()
    {
        var cls = Class.GetHandle("NSWorkspace");
        return objc_msgSend(cls, Selector.GetHandle("sharedWorkspace"));
    }

    public Task OpenFileAsync(string filePath)
    {
        Console.WriteLine($"[MacLauncher] OpenFileAsync called: {filePath}");
        try
        {
            var url = NSUrl.FromFilename(filePath);
            if (url == null)
            {
                Console.WriteLine($"[MacLauncher] NSUrl.FromFilename returned null for: {filePath}");
                return Task.CompletedTask;
            }

            var workspace = GetSharedWorkspace();
            Console.WriteLine($"[MacLauncher] Got workspace: {workspace != IntPtr.Zero}");
            var result = objc_msgSend_bool(workspace, Selector.GetHandle("openURL:"), url.Handle);
            Console.WriteLine($"[MacLauncher] NSWorkspace.openURL result: {result} for: {filePath}");
            if (!result)
            {
                // Fallback: use Process.Start with UseShellExecute=true
                Console.WriteLine($"[MacLauncher] Trying fallback: Process.Start UseShellExecute=true");
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = filePath,
                        UseShellExecute = true
                    };
                    System.Diagnostics.Process.Start(psi);
                    Console.WriteLine($"[MacLauncher] Fallback Process.Start succeeded");
                }
                catch (Exception pex)
                {
                    Console.WriteLine($"[MacLauncher] Fallback Process.Start error: {pex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacLauncher] OpenFileAsync error: {ex}");
        }
        return Task.CompletedTask;
    }

    public Task OpenFileWithAppAsync(string filePath, string bundleIdentifier)
    {
        try
        {
            var workspace = GetSharedWorkspace();

            // Get app URL from bundle identifier:
            // NSURL *appURL = [ws URLForApplicationWithBundleIdentifier:bundleId];
            using var bundleIdStr = new NSString(bundleIdentifier);
            var appUrl = objc_msgSend(workspace,
                Selector.GetHandle("URLForApplicationWithBundleIdentifier:"), bundleIdStr.Handle);

            if (appUrl == IntPtr.Zero)
            {
                Console.WriteLine($"[MacLauncher] App not found for bundle: {bundleIdentifier}");
                return Task.CompletedTask;
            }

            var fileUrl = NSUrl.FromFilename(filePath);
            if (fileUrl == null) return Task.CompletedTask;

            // NSArray *urls = @[fileUrl];
            using var urlArray = NSArray.FromNSObjects(fileUrl);

            // NSWorkspaceOpenConfiguration *config = [NSWorkspaceOpenConfiguration configuration];
            var configClass = Class.GetHandle("NSWorkspaceOpenConfiguration");
            var config = objc_msgSend(configClass, Selector.GetHandle("configuration"));

            // [ws openURLs:urls withApplicationAtURL:appUrl configuration:config completionHandler:nil];
            objc_msgSend_void(workspace,
                Selector.GetHandle("openURLs:withApplicationAtURL:configuration:completionHandler:"),
                urlArray.Handle, appUrl, config, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacLauncher] OpenFileWithAppAsync error: {ex}");
        }
        return Task.CompletedTask;
    }

    public async Task OpenInTerminalAsync(string directoryPath)
    {
        // Use osascript via stdin to open Terminal and cd to the directory
        var escapedPath = directoryPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script = $"tell application \"Terminal\"\nactivate\ndo script \"cd \" & quoted form of \"{escapedPath}\"\nend tell";
        await RunOsascriptAsync(script);
    }

    public async Task OpenInVsCodeAsync(string directoryPath)
    {
        await OpenInEditorAsync(directoryPath, "code", "com.microsoft.VSCode");
    }

    public async Task OpenInEditorAsync(string path, string cliName, string bundleId)
    {
        var cliPath = $"/usr/local/bin/{cliName}";
        if (!File.Exists(cliPath))
            cliPath = $"/opt/homebrew/bin/{cliName}";

        if (File.Exists(cliPath))
        {
            await RunProcessAsync(cliPath, $"\"{path}\"");
        }
        else
        {
            await RunProcessAsync("open", $"-b {bundleId} \"{path}\"");
        }
    }

    public Task RevealInFinderAsync(string path)
    {
        try
        {
            var workspace = GetSharedWorkspace();
            using var pathStr = new NSString(path);
            using var emptyStr = new NSString("");
            objc_msgSend_bool_2(workspace,
                Selector.GetHandle("selectFile:inFileViewerRootedAtPath:"),
                pathStr.Handle, emptyStr.Handle);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacLauncher] RevealInFinderAsync error: {ex}");
        }
        return Task.CompletedTask;
    }

    [DllImport("/usr/lib/libobjc.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool objc_msgSend_bool_2(IntPtr receiver, IntPtr selector,
        IntPtr arg1, IntPtr arg2);

    private static async Task RunOsascriptAsync(string script)
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "osascript",
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var process = System.Diagnostics.Process.Start(psi);
                if (process != null)
                {
                    process.StandardInput.Write(script);
                    process.StandardInput.Close();
                    process.WaitForExit(5000);
                }
            }
            catch { }
        });
    }

    private static async Task RunProcessAsync(string fileName, string arguments)
    {
        await Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                var process = System.Diagnostics.Process.Start(psi);
                process?.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MacLauncher] RunProcessAsync({fileName}) error: {ex.Message}");
            }
        });
    }
}
