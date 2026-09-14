using Avalonia.Input;

namespace MacExplorer.Services;

internal static class FileDropPolicy
{
    internal static DragDropEffects GetEffect(IReadOnlyList<string> sources, string targetDirectory)
    {
        if (sources.Count == 0 || string.IsNullOrWhiteSpace(targetDirectory)) return DragDropEffects.None;
        if (!VirtualPath.IsRemotePath(targetDirectory) && !Path.IsPathFullyQualified(targetDirectory)) return DragDropEffects.None;
        if (sources.Any(source => IsSelfOrDescendant(source, targetDirectory))) return DragDropEffects.None;

        // Preserve SFTP transfers as copies; only purely local drops move files.
        if (VirtualPath.IsRemotePath(targetDirectory) || sources.Any(VirtualPath.IsRemotePath))
            return DragDropEffects.Copy;

        // Releasing in the source directory is a no-op, including after switching tabs.
        if (sources.All(source => IsSameDestination(source, targetDirectory)))
            return DragDropEffects.None;
        return DragDropEffects.Move;
    }

    internal static bool IsSameDestination(string source, string targetDirectory)
    {
        if (VirtualPath.IsRemotePath(source) || VirtualPath.IsRemotePath(targetDirectory)) return false;
        return Normalize(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(source)) ?? "") == Normalize(targetDirectory);
    }

    private static bool IsSelfOrDescendant(string source, string targetDirectory)
    {
        if (VirtualPath.IsRemotePath(source) || VirtualPath.IsRemotePath(targetDirectory))
        {
            if (!VirtualPath.IsRemotePath(source) || !VirtualPath.IsRemotePath(targetDirectory)) return false;
            var from = VirtualPath.ParseRemotePath(source);
            var to = VirtualPath.ParseRemotePath(targetDirectory);
            if (from.ServerId != to.ServerId) return false;
            source = from.RemotePath;
            targetDirectory = to.RemotePath;
        }

        var parent = Normalize(source);
        var target = Normalize(targetDirectory);
        // A separator is essential: /foo-bar is not a child of /foo.
        return parent == null || target == null || target == parent
            || target.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                StringComparison.Ordinal);
    }

    private static string? Normalize(string path)
    {
        try { return Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null; }
        catch { return null; }
    }
}
