using MacExplorer.Models;

namespace MacExplorer.Services;

public interface INetworkLocationService
{
    IReadOnlyList<NetworkLocationRow> GetLocations(bool includeDiscovered = false);
    event Action? Changed;
}
