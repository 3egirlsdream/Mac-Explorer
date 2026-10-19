using MacExplorer.Models;

namespace MacExplorer.Services;

public interface INetworkDiscoveryService
{
    IReadOnlyList<NetworkServer> Servers { get; }
    string? Error { get; }
    event Action? Changed;
    IDisposable Acquire();
    void Refresh();
}
