namespace MacExplorer.Services;

public interface IApplicationLauncherService
{
    Task OpenFileAsync(string filePath);
    Task OpenFileWithAppAsync(string filePath, string bundleIdentifier);
    Task OpenInTerminalAsync(string directoryPath);
    Task OpenInVsCodeAsync(string directoryPath);
    Task RevealInFinderAsync(string path);
}
