using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services.Impl;

public sealed class DiskInformationService : IDiskInformationService
{
    private readonly SemaphoreSlim _queryLock = new(1);

    public async Task<IReadOnlyList<DiskInfo>> GetMountedDisksAsync(CancellationToken cancellationToken)
    {
        await _queryLock.WaitAsync(cancellationToken);
        try
        {
            var result = await Task.Run(() => OperatingSystem.IsMacOS()
                ? MacDiskNative.GetMountedDisks() : Array.Empty<DiskInfo>(), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { _queryLock.Release(); }
    }
}
