using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IVolumeMonitorService : IDisposable
{
    /// <summary>Currently mounted external volumes.</summary>
    IReadOnlyList<VolumeInfo> ExternalVolumes { get; }

    /// <summary>Returns true if the given path resides on an external volume.</summary>
    bool IsExternalVolume(string path);

    /// <summary>Safely eject/unmount an external volume. Returns true on success.</summary>
    Task<bool> EjectVolumeAsync(string volumePath);

    /// <summary>Raised when the external volume list changes (mount/unmount).</summary>
    event Action? VolumesChanged;
}
