using System.Diagnostics;
using System.Runtime.InteropServices;
using CoreFoundation;
using Foundation;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Monitors /Volumes for external drive mount/unmount events using FSEvents.
/// Provides volume classification (internal vs external) via NSURL resource values.
/// Handles AI data cleanup when external volumes are physically removed.
/// </summary>
public class MacVolumeMonitorService : IVolumeMonitorService, IDisposable
{
    private readonly IAiTagService _aiTagService;
    private readonly ILogger<MacVolumeMonitorService>? _logger;
    private readonly object _lock = new();
    private readonly object _cleanupLock = new();
    private readonly List<string> _pendingRemovedVolumePaths = [];
    private readonly Dictionary<string, int> _missingVolumeScanCounts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cleanupDebounceCts;
    private int _confirmationScanScheduled;

    private List<VolumeInfo> _externalVolumes = [];
    private IntPtr _stream;
    private bool _disposed;

    // Static instance reference for FSEvents callback
    private static MacVolumeMonitorService? _instance;

    public event Action? VolumesChanged;

    public IReadOnlyList<VolumeInfo> ExternalVolumes
    {
        get { lock (_lock) return _externalVolumes.ToList(); }
    }

    public MacVolumeMonitorService(IAiTagService aiTagService, ILogger<MacVolumeMonitorService>? logger = null)
    {
        _aiTagService = aiTagService;
        _logger = logger;
        _instance = this;

        // Initial scan + start monitoring
        _externalVolumes = ScanExternalVolumes();
        StartMonitoring();
    }

    public bool IsExternalVolume(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        // Extract volume root: /Volumes/X/... → /Volumes/X
        string volumeRoot;
        if (path.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var idx = path.IndexOf('/', "/Volumes/".Length);
            volumeRoot = idx < 0 ? path : path[..idx];
        }
        else
        {
            return false; // Root volume is not external
        }

        lock (_lock)
        {
            return _externalVolumes.Any(v =>
                string.Equals(v.Path, volumeRoot, StringComparison.Ordinal));
        }
    }

    public async Task<bool> EjectVolumeAsync(string volumePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/sbin/diskutil",
                Arguments = $"eject \"{volumePath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process == null) return false;

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to eject volume {Path}", volumePath);
            return false;
        }
    }

    // ── Volume scanning ──

    private static List<VolumeInfo> ScanExternalVolumes()
    {
        var results = new List<VolumeInfo>();
        try
        {
            var volumesDir = new DirectoryInfo("/Volumes");
            if (!volumesDir.Exists) return results;

            foreach (var dir in volumesDir.EnumerateDirectories())
            {
                try
                {
                    // Skip APFS system data volumes (e.g. "Macintosh HD - Data")
                    if (dir.Name.EndsWith(" - Data", StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var url = NSUrl.CreateFileUrl(dir.FullName);
                    if (url == null) continue;

                    // Query volume properties via NSURL resource values
                    url.TryGetResource(NSUrl.VolumeIsInternalKey, out var internalVal);
                    url.TryGetResource(NSUrl.VolumeIsRemovableKey, out var removableVal);

                    var isInternal = (internalVal as NSNumber)?.BoolValue ?? true;

                    // Skip internal volumes (the boot volume symlink in /Volumes, etc.)
                    if (isInternal) continue;

                    var isRemovable = (removableVal as NSNumber)?.BoolValue ?? false;
                    var displayName = NSFileManager.DefaultManager.DisplayName(dir.FullName);

                    results.Add(new VolumeInfo
                    {
                        Path = dir.FullName,
                        DisplayName = string.IsNullOrEmpty(displayName)
                            ? dir.Name
                            : displayName,
                        IsExternal = true,
                        IsRemovable = isRemovable,
                    });
                }
                catch
                {
                    // Skip inaccessible volumes
                }
            }
        }
        catch
        {
            // /Volumes itself inaccessible
        }

        return results;
    }

    // ── FSEvents monitoring of /Volumes ──

    private unsafe void StartMonitoring()
    {
        var pathStr = IntPtr.Zero;
        var pathsArray = IntPtr.Zero;
        try
        {
            pathStr = CFStringCreateWithCString(IntPtr.Zero, "/Volumes", kCFStringEncodingUTF8);
            pathsArray = CFArrayCreate(IntPtr.Zero, [pathStr], 1, IntPtr.Zero);

            _stream = FSEventStreamCreate(
                IntPtr.Zero,
                &OnFSEvent,
                IntPtr.Zero,
                pathsArray,
                kFSEventStreamEventIdSinceNow,
                1.0,  // 1 second latency — good balance for volume mount events
                kFSEventStreamCreateFlagNoDefer);

            if (_stream != IntPtr.Zero)
            {
                var mainRunLoop = CFRunLoopGetMain();
                FSEventStreamScheduleWithRunLoop(_stream, mainRunLoop, _kCFRunLoopDefaultMode);
                FSEventStreamStart(_stream);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacExplorer] VolumeMonitor StartMonitoring failed: {ex.Message}");
        }
        finally
        {
            if (pathStr != IntPtr.Zero) CFRelease(pathStr);
            if (pathsArray != IntPtr.Zero) CFRelease(pathsArray);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnFSEvent(
        IntPtr streamRef, IntPtr clientCallBackInfo, nint numEvents,
        IntPtr eventPaths, IntPtr eventFlags, IntPtr eventIds)
    {
        var instance = _instance;
        if (instance == null || instance._disposed) return;

        try
        {
            instance.HandleVolumesChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacExplorer] VolumeMonitor FSEvents callback error: {ex.Message}");
        }
    }

    private void HandleVolumesChanged()
    {
        var newVolumes = ScanExternalVolumes();

        List<VolumeInfo> removedVolumes;
        var needsConfirmationScan = false;
        lock (_lock)
        {
            var newPaths = new HashSet<string>(newVolumes.Select(v => v.Path), StringComparer.Ordinal);
            var retainedVolumes = new List<VolumeInfo>(newVolumes);
            removedVolumes = [];

            foreach (var oldVolume in _externalVolumes.Where(v => !newPaths.Contains(v.Path)))
            {
                var missingCount = _missingVolumeScanCounts.GetValueOrDefault(oldVolume.Path) + 1;
                if (missingCount >= 2)
                {
                    _missingVolumeScanCounts.Remove(oldVolume.Path);
                    removedVolumes.Add(oldVolume);
                }
                else
                {
                    _missingVolumeScanCounts[oldVolume.Path] = missingCount;
                    retainedVolumes.Add(oldVolume);
                    needsConfirmationScan = true;
                }
            }

            foreach (var mountedPath in newPaths)
                _missingVolumeScanCounts.Remove(mountedPath);

            _externalVolumes = retainedVolumes;
        }

        if (needsConfirmationScan)
            ScheduleConfirmationScan();

        if (removedVolumes.Count > 0)
            ScheduleVolumeCleanup(removedVolumes.Select(v => v.Path));

        // Notify UI on main thread
        MainThread.BeginInvokeOnMainThread(RaiseVolumesChanged);
    }

    private void ScheduleConfirmationScan()
    {
        if (Interlocked.Exchange(ref _confirmationScanScheduled, 1) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(500);
                if (!_disposed)
                    DispatchQueue.MainQueue.DispatchAsync(() => HandleVolumesChanged());
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to confirm external volume removal");
            }
            finally
            {
                Interlocked.Exchange(ref _confirmationScanScheduled, 0);
            }
        });
    }

    private void RaiseVolumesChanged()
    {
        var handlers = VolumesChanged?.GetInvocationList();
        if (handlers == null) return;

        foreach (var handler in handlers)
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger?.LogError(ex, "External volume change subscriber failed"); }
        }
    }

    private void ScheduleVolumeCleanup(IEnumerable<string> volumePaths)
    {
        lock (_cleanupLock)
        {
            foreach (var path in volumePaths)
            {
                if (!_pendingRemovedVolumePaths.Contains(path, StringComparer.Ordinal))
                    _pendingRemovedVolumePaths.Add(path);
            }

            _cleanupDebounceCts?.Cancel();
            _cleanupDebounceCts?.Dispose();
            _cleanupDebounceCts = new CancellationTokenSource();
            var token = _cleanupDebounceCts.Token;
            _ = RunDebouncedVolumeCleanupAsync(token);
        }
    }

    private async Task RunDebouncedVolumeCleanupAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!_disposed)
        {
            List<string> pathsToClean;
            lock (_cleanupLock)
            {
                if (_pendingRemovedVolumePaths.Count == 0)
                    return;

                pathsToClean = _pendingRemovedVolumePaths
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                _pendingRemovedVolumePaths.Clear();
            }

            try
            {
                foreach (var volumePath in pathsToClean)
                {
                    _logger?.LogInformation("Cleaning up AI data for removed volume: {Path}", volumePath);
                    await _aiTagService.DeleteAnalysisForPathPrefixAsync(volumePath);
                }

                await _aiTagService.RunClusteringAsync();
                _logger?.LogInformation("AI data cleanup completed for {Count} volume(s)", pathsToClean.Count);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to clean up AI data for removed volumes");
            }

            lock (_cleanupLock)
            {
                if (_pendingRemovedVolumePaths.Count == 0)
                    return;
            }
        }
    }

    // ── Dispose ──

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_cleanupLock)
        {
            _cleanupDebounceCts?.Cancel();
            _cleanupDebounceCts?.Dispose();
            _cleanupDebounceCts = null;
            _pendingRemovedVolumePaths.Clear();
        }
        lock (_lock)
            _missingVolumeScanCounts.Clear();

        if (_stream != IntPtr.Zero)
        {
            FSEventStreamStop(_stream);
            FSEventStreamInvalidate(_stream);
            FSEventStreamRelease(_stream);
            _stream = IntPtr.Zero;
        }

        if (ReferenceEquals(_instance, this))
            _instance = null;
    }

    // ── CoreServices FSEvents P/Invoke ──

    private const ulong kFSEventStreamEventIdSinceNow = 0xFFFFFFFFFFFFFFFF;
    private const uint kFSEventStreamCreateFlagNoDefer = 0x00000002;
    private const uint kCFStringEncodingUTF8 = 0x08000100;

    private static readonly IntPtr _kCFRunLoopDefaultMode = CFStringCreateWithCString(
        IntPtr.Zero, "kCFRunLoopDefaultMode", kCFStringEncodingUTF8);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern unsafe IntPtr FSEventStreamCreate(
        IntPtr allocator,
        delegate* unmanaged<IntPtr, IntPtr, nint, IntPtr, IntPtr, IntPtr, void> callback,
        IntPtr context,
        IntPtr pathsToWatch, ulong sinceWhen, double latency, uint flags);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamScheduleWithRunLoop(
        IntPtr streamRef, IntPtr runLoop, IntPtr runLoopMode);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool FSEventStreamStart(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamStop(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamInvalidate(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern void FSEventStreamRelease(IntPtr streamRef);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFRunLoopGetMain();

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFArrayCreate(
        IntPtr allocator, IntPtr[] values, nint numValues, IntPtr callBacks);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(
        IntPtr allocator, string cStr, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);
}
