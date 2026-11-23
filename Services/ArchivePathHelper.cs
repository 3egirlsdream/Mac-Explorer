namespace MacExplorer.Services;

public static class ArchivePathHelper
{
    private const string Prefix = VirtualPath.ArchivePrefix;

    public static bool IsArchivePath(string path) =>
        path.StartsWith(Prefix, StringComparison.Ordinal);

    public static (string archivePath, string internalPath) Parse(string sentinelPath)
    {
        if (!IsArchivePath(sentinelPath))
            return (sentinelPath, "");

        var payload = sentinelPath[Prefix.Length..];
        var hashIdx = payload.IndexOf('#');
        if (hashIdx < 0)
            return (payload, "");
        var archive = payload[..hashIdx];
        return (archive.StartsWith("!", StringComparison.Ordinal) ? Uri.UnescapeDataString(archive[1..]) : archive,
            payload[(hashIdx + 1)..]);
    }

    public static string Build(string archivePath, string internalPath) =>
        $"{Prefix}{(archivePath.Contains('#') ? "!" + Uri.EscapeDataString(archivePath) : archivePath)}#{internalPath}";
}
