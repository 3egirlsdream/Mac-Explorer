using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IAppUpdateService
{
    /// <summary>
    /// Queries the backend for a newer version.
    /// Returns VersionInfo if an update is available, null otherwise.
    /// </summary>
    Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the update zip, extracts it, and triggers install+restart.
    /// Reports progress as (percentage 0-100, status text).
    /// </summary>
    Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default);
}
