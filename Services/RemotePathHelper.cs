namespace MacExplorer.Services;

/// <summary>
/// POSIX-style path arithmetic shared by every remote backend. Accepts both raw
/// remote paths and <see cref="VirtualPath.RemotePrefix"/> sentinel paths, and
/// preserves whichever form it was given.
/// </summary>
internal static class RemotePathHelper
{
    public static string GetParentPath(string path)
    {
        if (VirtualPath.IsRemotePath(path))
        {
            var (serverId, remotePath) = VirtualPath.ParseRemotePath(path);
            if (string.IsNullOrEmpty(remotePath) || remotePath == "/")
                return VirtualPath.BuildRemotePath(serverId, "/");
            var normalized = remotePath.TrimEnd('/');
            var lastSlash = normalized.LastIndexOf('/');
            var parentRemote = lastSlash <= 0 ? "/" : normalized[..lastSlash];
            return VirtualPath.BuildRemotePath(serverId, parentRemote);
        }
        if (string.IsNullOrEmpty(path) || path == "/") return "/";
        var norm = path.TrimEnd('/');
        var ls = norm.LastIndexOf('/');
        return ls <= 0 ? "/" : norm[..ls];
    }

    public static string CombinePath(string directory, string name)
    {
        if (VirtualPath.IsRemotePath(directory))
        {
            var (serverId, remotePath) = VirtualPath.ParseRemotePath(directory);
            var combined = CombinePath(remotePath, name);
            return VirtualPath.BuildRemotePath(serverId, combined);
        }
        if (string.IsNullOrEmpty(directory) || directory == "/")
            return "/" + name;
        return directory.TrimEnd('/') + "/" + name;
    }

    /// <summary>Strips the sentinel prefix, leaving the backend-native path.</summary>
    public static string ToRemotePath(string path)
        => VirtualPath.IsRemotePath(path) ? VirtualPath.ParseRemotePath(path).RemotePath : path;
}
