using System.Text.Json;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacOS;

internal sealed class FileDropFolderAccess(string store, IBookmarkAccess backend) : IDisposable
{
    private sealed record Grant(string Bookmark, nint Scope);
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private bool _loaded;
    private bool _storeReadable = true;
    internal string? PersistenceError { get; private set; }

    internal static string SuggestedFolder(string source)
    {
        const string marker = "/Library/Containers/com.tencent.xinWeChat/Data/Documents/xwechat_files/";
        var start = source.IndexOf(marker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var accountStart = start + marker.Length;
            var accountEnd = source.IndexOf('/', accountStart);
            if (accountEnd > accountStart && source.AsSpan(accountEnd).StartsWith("/temp/", StringComparison.Ordinal))
                return source[..(accountEnd + "/temp".Length)];
        }
        return Path.GetDirectoryName(source)!;
    }

    internal static bool IsWithin(string path, string folder)
        => path == folder || path.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);

    internal void Restore()
    {
        if (_loaded) return;
        _loaded = true;
        _storeReadable = true;
        PersistenceError = null;
        try
        {
            if (!File.Exists(store)) return;
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(store))
                ?? throw new JsonException();
            foreach (var bookmark in saved.Values)
                Remember(bookmark, null, persist: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _storeReadable = false;
            PersistenceError = ex.Message;
        }
    }

    internal bool Covers(string source)
    {
        Restore();
        var path = backend.RealPath(source);
        return _grants.Keys.Any(folder => IsWithin(path, folder));
    }

    internal bool Remember(string bookmark, string? source, bool persist = true)
    {
        if (persist) Restore();
        var opened = backend.Open(bookmark);
        if (opened.Scope == 0 || opened.Path == null)
        {
            if (opened.Scope != 0) backend.Close(opened.Scope);
            return false;
        }
        var folder = backend.RealPath(opened.Path);
        if (source != null && !IsWithin(backend.RealPath(source), folder))
        {
            backend.Close(opened.Scope);
            return false;
        }
        if (_grants.Remove(folder, out var previous)) backend.Close(previous.Scope);
        _grants[folder] = new(opened.Refreshed ?? bookmark, opened.Scope);
        if (persist) Save();
        return true;
    }

    private void Save()
    {
        if (!_storeReadable) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store)!);
            temporary = store + "." + Guid.NewGuid().ToString("N");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var writer = new StreamWriter(new FileStream(temporary, options)))
                writer.Write(JsonSerializer.Serialize(_grants.ToDictionary(pair => pair.Key, pair => pair.Value.Bookmark)));
            File.Move(temporary, store, overwrite: true);
            PersistenceError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PersistenceError = ex.Message;
        }
        finally { if (temporary != null && File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        foreach (var grant in _grants.Values) backend.Close(grant.Scope);
        _grants.Clear();
        _loaded = false;
    }
}
