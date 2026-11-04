using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IAppUpdateService
{
    /// <summary>
    /// Version reported by the running app bundle.
    /// </summary>
    string CurrentVersion { get; }

    /// <summary>The store page used instead of downloading an update, or null for website builds.</summary>
    Uri? UpdatePageUri { get; }

    /// <summary>
    /// Queries the release source for this distribution channel.
    /// Returns VersionInfo if an update is available, null otherwise.
    /// </summary>
    Task<VersionInfo?> CheckVersionAsync(CancellationToken ct = default);

    /// <summary>Returns the latest release and history, including when already up to date.</summary>
    Task<VersionInfo?> GetVersionDetailsAsync(CancellationToken ct = default);

    /// <summary>
    /// Website builds download the update zip, extract it, and trigger install+restart.
    /// Reports progress as (percentage 0-100, status text).
    /// </summary>
    Task DownloadAndInstallAsync(
        VersionInfo versionInfo,
        IProgress<(double Progress, string Status)>? progress = null,
        CancellationToken ct = default);
}
