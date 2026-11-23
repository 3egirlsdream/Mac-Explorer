namespace MacExplorer.Services;

public interface IApplicationLauncherService
{
    Task OpenFileAsync(string filePath);
    Task OpenFilesAsync(IReadOnlyList<string> filePaths)
        => filePaths.Count == 1 ? OpenFileAsync(filePaths[0]) : Task.WhenAll(filePaths.Select(OpenFileAsync));

    Task OpenFileWithAppAsync(string filePath, string bundleIdentifier);
    Task OpenFilesWithAppAsync(IReadOnlyList<string> filePaths, string bundleIdentifier)
        => filePaths.Count == 1 ? OpenFileWithAppAsync(filePaths[0], bundleIdentifier) : Task.WhenAll(filePaths.Select(p => OpenFileWithAppAsync(p, bundleIdentifier)));

    Task OpenInTerminalAsync(string directoryPath);
    Task OpenInEditorAsync(string path, string cliName, string bundleId);

    Task RevealInFinderAsync(string path);
    Task RevealInFinderAsync(IReadOnlyList<string> paths)
        => paths.Count == 1 ? RevealInFinderAsync(paths[0]) : Task.WhenAll(paths.Select(RevealInFinderAsync));
}
