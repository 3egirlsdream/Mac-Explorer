using Avalonia.Controls;

namespace MacExplorer.Services;

public interface IAirDropService
{
    bool IsSupported { get; }
    Task SendAsync(Window? owner, IReadOnlyList<string> paths);
}
