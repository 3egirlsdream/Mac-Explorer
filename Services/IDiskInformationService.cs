using MacExplorer.Models;

namespace MacExplorer.Services;

public interface IDiskInformationService
{
    Task<IReadOnlyList<DiskInfo>> GetMountedDisksAsync(CancellationToken cancellationToken);
}
