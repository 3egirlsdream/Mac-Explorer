namespace MacExplorer.Services;

public interface IFileWatcherService
{
    event EventHandler<FileChangedEventArgs>? FileChanged;
    void WatchDirectory(string path);
    void StopWatching();
}

public class FileChangedEventArgs : EventArgs
{
    public string DirectoryPath { get; init; } = string.Empty;
    public FileChangeType ChangeType { get; init; }
}

public enum FileChangeType
{
    Created,
    Deleted,
    Modified,
    Renamed
}
