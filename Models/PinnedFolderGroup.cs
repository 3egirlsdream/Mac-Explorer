using CommunityToolkit.Mvvm.ComponentModel;

namespace MacExplorer.Models;

public sealed class PinnedFolderGroup : ObservableObject
{
    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set => SetProperty(ref _isCollapsed, value);
    }

    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<PinnedFolder> Folders { get; init; } = [];
}
