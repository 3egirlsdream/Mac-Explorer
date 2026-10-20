namespace MacExplorer.Services;

internal static class ArchiveExtractionPathHelper
{
    private static readonly object CreateDirectoryLock = new();

    internal static string GetArchiveFolderName(string archivePath)
        => EnsureFolderName(ArchiveFormats.RemoveExtension(archivePath));

    internal static string CreateUniqueExtractionDirectory(string parentPath, string archivePath)
    {
        var folderName = GetArchiveFolderName(archivePath);

        lock (CreateDirectoryLock)
        {
            var destinationPath = GetUniqueDirectoryPath(parentPath, folderName);
            Directory.CreateDirectory(destinationPath);
            return destinationPath;
        }
    }

    internal static string GetUniqueDirectoryPath(string parentPath, string folderName)
    {
        var destinationPath = Path.Combine(parentPath, folderName);
        if (!PathExists(destinationPath))
            return destinationPath;

        for (var suffix = 2; ; suffix++)
        {
            destinationPath = Path.Combine(parentPath, $"{folderName} {suffix}");
            if (!PathExists(destinationPath))
                return destinationPath;
        }
    }

    private static bool PathExists(string path) => File.Exists(path) || Directory.Exists(path);

    private static string EnsureFolderName(string name) =>
        string.IsNullOrWhiteSpace(name) ? "解压内容" : name;
}
