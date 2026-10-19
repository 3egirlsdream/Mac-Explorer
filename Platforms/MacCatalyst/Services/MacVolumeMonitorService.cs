using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacVolumeMonitorService : IVolumeMonitorService, IDisposable
{
    private static readonly HashSet<string> HiddenSystemVolumeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Macintosh HD", "Preboot", "Recovery", "Update", "VM",
        ".timemachine", ".migration-timemachine"
    };

    private readonly IAiTagService? _aiTagService;
    private readonly ILogger<MacVolumeMonitorService>? _logger;
    private readonly object _sync = new();
    private Timer? _pollTimer;

    private IReadOnlyList<VolumeInfo> _externalVolumes = Array.Empty<VolumeInfo>();
    public IReadOnlyList<VolumeInfo> ExternalVolumes
    {
        get { lock (_sync) return _externalVolumes.ToArray(); }
    }
    public event Action? VolumesChanged;

    public MacVolumeMonitorService(IAiTagService? aiTagService = null, ILogger<MacVolumeMonitorService>? logger = null)
    {
        _aiTagService = aiTagService;
        _logger = logger;
        RefreshVolumes();
        _pollTimer = new Timer(_ => RefreshVolumes(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5));
    }

    public Task InitializeAsync()
    {
        RefreshVolumes();
        return Task.CompletedTask;
    }

    public async Task<bool> EjectVolumeAsync(string volumePath)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
                return await Task.Run(() => Platforms.MacOS.MacSandboxNative.EjectVolume(volumePath));
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "diskutil",
                Arguments = $"eject \"{volumePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return false;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(timeout.Token);
            return process.ExitCode == 0;
        }
        catch { return false; }
    }

    public bool IsExternalVolume(string path) => ExternalVolumes.Any(volume => DirectoryAccess.IsWithin(path, volume.Path));

    private void RefreshVolumes()
    {
        try
        {
            var volumes = OperatingSystem.IsMacOS()
                ? Platforms.MacOS.MacNetworkNative.GetVolumes().Where(v => ShouldShowInSidebar(v.DisplayName)).ToList()
                : new List<VolumeInfo>();

            string[] removed;
            var changed = false;
            lock (_sync)
            {
                var previousPaths = _externalVolumes.Select(volume => volume.Path).ToHashSet(StringComparer.Ordinal);
                var currentPaths = volumes.Select(volume => volume.Path).ToHashSet(StringComparer.Ordinal);
                changed = !previousPaths.SetEquals(currentPaths) || !_externalVolumes.Select(v => (v.Path, v.SourceUrl)).SequenceEqual(volumes.Select(v => (v.Path, v.SourceUrl)));
                removed = previousPaths.Except(currentPaths, StringComparer.Ordinal).ToArray();
                _externalVolumes = volumes.AsReadOnly();
            }

            if (changed)
                VolumesChanged?.Invoke();

            if (_aiTagService != null)
            {
                foreach (var removedPath in removed)
                    _ = CleanupRemovedVolumeAsync(removedPath);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to refresh volumes");
        }
    }

    internal static bool ShouldShowInSidebar(string? volumeName)
    {
        if (string.IsNullOrWhiteSpace(volumeName)
            || volumeName.StartsWith(".", StringComparison.Ordinal)
            || volumeName.StartsWith("com.apple.", StringComparison.OrdinalIgnoreCase))
            return false;

        return !HiddenSystemVolumeNames.Contains(volumeName);
    }

    private async Task CleanupRemovedVolumeAsync(string volumePath)
    {
        try
        {
            await _aiTagService!.DeleteAnalysisForPathPrefixAsync(volumePath.TrimEnd('/') + "/");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to clean AI data for removed volume {VolumePath}", volumePath);
        }
    }

    public void Dispose()
    {
        _pollTimer?.Dispose();
    }
}
