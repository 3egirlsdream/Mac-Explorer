namespace MacExplorer.Services;

public interface IDisplayNameService
{
    string GetDisplayName(string fullPath);
    string GetUserName();
    void RecordRename(string oldPath, string newPath);
}
