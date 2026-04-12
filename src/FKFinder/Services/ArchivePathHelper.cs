namespace FKFinder.Services;

public static class ArchivePathHelper
{
    private const string Prefix = "__archive:";

    public static bool IsArchivePath(string path) =>
        path.StartsWith(Prefix, StringComparison.Ordinal);

    public static (string archivePath, string internalPath) Parse(string sentinelPath)
    {
        var payload = sentinelPath[Prefix.Length..];
        var hashIdx = payload.IndexOf('#');
        if (hashIdx < 0)
            return (payload, "");
        return (payload[..hashIdx], payload[(hashIdx + 1)..]);
    }

    public static string Build(string archivePath, string internalPath) =>
        $"{Prefix}{archivePath}#{internalPath}";
}
