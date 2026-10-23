using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;

internal static class Program
{
    private static readonly List<object> Samples = [];
    private static string Output = "";
    private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    public static void Main(string[] args)
    {
        if (args is [StartupUpdateChecker.WorkerArgument])
        {
            MacExplorer.Program.Main(args);
            return;
        }
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")))
            throw new InvalidOperationException("Run through Tools/Testing/run-isolated.sh.");
        Output = Path.GetFullPath(args.FirstOrDefault() ?? "outputs/memory-benchmark");
        Directory.CreateDirectory(Output);
        using var diagnostics = MacExplorer.Services.MemoryDiagnostics.Start(Path.Combine(Output, "diagnostics"));
        foreach (var name in new[] { "samples.json", "verification.json", "error.txt" }) File.Delete(Path.Combine(Output, name));
        AppBuilder.Configure<MacExplorer.App>().UsePlatformDetect()
            .AfterSetup(_ => Dispatcher.UIThread.Post(async () => await RunAsync()))
            .StartWithClassicDesktopLifetime([]);
    }

    private static void Sample(string phase, bool collect = true)
    {
        if (collect)
        {
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true, true);
        }
        using var process = Process.GetCurrentProcess();
        var gc = GC.GetGCMemoryInfo();
        var sample = new { phase, pid = process.Id, utc = DateTime.UtcNow,
            physicalFootprintMiB = MacExplorer.Services.MemoryDiagnostics.ReadPhysicalFootprint() / 1048576.0,
            rssMiB = process.WorkingSet64 / 1048576.0, managedMiB = GC.GetTotalMemory(false) / 1048576.0,
            committedMiB = gc.TotalCommittedBytes / 1048576.0, heapMiB = gc.HeapSizeBytes / 1048576.0 };
        Samples.Add(sample);
        Console.WriteLine(JsonSerializer.Serialize(sample));
        File.WriteAllText(Path.Combine(Output, "samples.json"), JsonSerializer.Serialize(Samples, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunAsync()
    {
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        try
        {
            var quick = Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_QUICK") == "1";
            var window = (MainWindow)lifetime.MainWindow!;
            var model = (MainWindowViewModel)window.DataContext!;
            Require(MacExplorer.Platforms.MacOS.MacAutomationLifetime.NativeChildrenFixActive,
                "The pinned native accessibility reference fix was not installed.");
            await Task.Delay(quick ? 100 : 5000); Sample("startup", false);
            await Task.Delay(quick ? 100 : 20000); Sample("idle", false); Sample("baseline-gc");
            var data = Path.Combine(Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!, "fixtures");
            for (var d = 0; d < 4; d++)
            {
                var directory = Path.Combine(data, d.ToString()); Directory.CreateDirectory(directory);
                for (var f = 0; f < 500; f++) File.WriteAllText(Path.Combine(directory, $"file-{f:D4}.txt"), "memory benchmark");
            }
            for (var round = 0; round < (quick ? 0 : 3); round++)
            {
                for (var n = 0; n < 12; n++)
                {
                    await model.FileList.NavigateToAsync(Path.Combine(data, (n % 4).ToString()));
                    await Task.Delay(200);
                }
                Sample($"navigation-{(round + 1) * 12}-gc");
            }
            await model.FileList.NavigateToAsync(Environment.GetEnvironmentVariable("MACEXPLORER_TEST_ROOT")!);
            await Task.Delay(2000);
            var tabs = new List<WeakReference>();
            var close = typeof(MainWindow).GetMethod("CloseTabCoreAsync", PrivateInstance)!;
            var axNodes = 0;
            var closedTabBytes = new List<long>();
            for (var round = 0; round < (quick ? 1 : 3); round++)
            {
                for (var n = 0; n < 12; n++)
                {
                    await window.OpenNewTabAsync(Path.Combine(data, (n % 4).ToString()));
                    tabs.Add(new(model.SelectedTab!.FileList));
                    var currentNodes = ReadAccessibility(window);
                    Require(currentNodes > 1, "The active accessibility tree was lost.");
                    axNodes = Math.Max(axNodes, currentNodes);
                    await (Task)close.Invoke(window, [model.SelectedTab])!;
                    await Task.Delay(150);
                }
                await Task.Delay(500); Sample($"closed-tabs-{(round + 1) * 12}-gc");
                closedTabBytes.Add(GC.GetTotalMemory(false));
                Require(tabs.All(reference => !reference.IsAlive), "Closed tab view models survived collection.");
            }
            if (!quick)
                Require(closedTabBytes[^1] - closedTabBytes[0] < 8 * 1024 * 1024,
                    "Repeated closed tabs accumulated more than 8 MiB after warmup.");
            Require(axNodes > 1, "The native accessibility tree was not exercised.");
            var manager = new BackgroundTaskManager();
            var payloads = new List<WeakReference>();
            for (var n = 0; n < 30; n++) payloads.Add(await CompleteCapturedTaskAsync(manager, n));
            await Task.Delay(500); Sample("30-completed-tasks-gc");
            Require(payloads.All(reference => !reference.IsAlive), "Completed tasks retained their captured payloads.");
            Require(manager.Tasks.Count == 30, "Task summaries were lost.");
            var settings = new List<WeakReference>();
            for (var n = 0; n < 3; n++) settings.Add(await OpenAndCloseSettingsAsync(window, model.FileList));
            await Task.Delay(1000); Sample("closed-settings-gc");
            Require(settings.All(reference => !reference.IsAlive), "Closed settings windows survived collection.");
            Console.WriteLine("READY FOR HEAP SNAPSHOT");
            await Task.Delay(quick ? 100 : 60000);
            Sample(quick ? "idle-quick" : "idle-60s", false); Sample(quick ? "idle-quick-gc" : "idle-60s-gc");
            File.WriteAllText(Path.Combine(Output, "verification.json"), JsonSerializer.Serialize(new
            {
                version = typeof(MacExplorer.App).Assembly.GetName().Version?.ToString(), quick,
                closedTabs = tabs.Count, retainedTabs = tabs.Count(r => r.IsAlive),
                closedSettings = settings.Count, retainedSettings = settings.Count(r => r.IsAlive),
                retainedPayloads = payloads.Count(r => r.IsAlive), taskSummaries = manager.Tasks.Count, axNodes,
                closedTabGrowthMiB = (closedTabBytes[^1] - closedTabBytes[0]) / 1048576.0
            }, new JsonSerializerOptions { WriteIndented = true }));
            GC.KeepAlive(manager);
            lifetime.Shutdown();
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Output, "error.txt"), error.ToString());
            Console.Error.WriteLine(error);
            if (Environment.GetEnvironmentVariable("MACEXPLORER_MEMORY_HOLD_ON_FAILURE") == "1") await Task.Delay(30000);
            lifetime.Shutdown(1);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> CompleteCapturedTaskAsync(BackgroundTaskManager manager, int n)
    {
        var payload = new byte[1024 * 1024]; Random.Shared.NextBytes(payload);
        var reference = new WeakReference(payload);
        await manager.RunAsync(new FileTaskRequest(BackgroundTaskKind.Copy, "probe", ["/tmp/memory-probe-" + n]),
            context => { context.Remember("payload", () => payload); return Task.FromResult(payload.Length); });
        return reference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> OpenAndCloseSettingsAsync(Window owner, FileListViewModel model)
    {
        var dialog = new SettingsDialog { DataContext = model };
        dialog.Show(owner); await Task.Delay(300); ReadAccessibility(dialog);
        var reference = new WeakReference(dialog);
        dialog.Close(); await Task.Delay(300); return reference;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    // Force native AX wrappers to exist even when no screen reader is currently connected.
    private static int ReadAccessibility(Window window)
    {
        var handle = window.TryGetPlatformHandle()!;
        var native = handle is IMacOSTopLevelPlatformHandle mac
            ? Send(mac.NSView, Selector("window"))
            : handle.HandleDescriptor == "NSView" ? Send(handle.Handle, Selector("window")) : handle.Handle;
        var seen = new HashSet<IntPtr>(); var pending = new Stack<IntPtr>(); pending.Push(native);
        while (pending.TryPop(out var current) && seen.Count < 10000)
        {
            if (current == IntPtr.Zero || !seen.Add(current)) continue;
            var children = Send(current, Selector("accessibilityChildren"));
            var count = children == IntPtr.Zero ? 0 : (int)Send(children, Selector("count"));
            for (var i = 0; i < count; i++) pending.Push(SendIndex(children, Selector("objectAtIndex:"), (nuint)i));
        }
        return seen.Count;
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr target, IntPtr selector);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIndex(IntPtr target, IntPtr selector, nuint index);
}
