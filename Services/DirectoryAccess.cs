using System.Diagnostics;
using System.Text.Json;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services;

/// <summary>Owns persistent grants and their native access lifetime. Restores before services start.</summary>
public sealed class DirectoryAccess : IDisposable
{
    private static readonly Lazy<DirectoryAccess> Instance = new(() => new(
        Path.Combine(RuntimePaths.DataDirectory, "directory-bookmarks.json"), DistributionChannel.IsAppStore));
    private static readonly AsyncLocal<DirectoryAccess?> TestOverride = new();
    public static DirectoryAccess Current => TestOverride.Value ?? Instance.Value;
    internal static IDisposable UseForTests(DirectoryAccess access)
    {
        var previous = TestOverride.Value; TestOverride.Value = access;
        return new RestoreTestAccess(() => TestOverride.Value = previous);
    }
    private sealed class RestoreTestAccess(Action restore) : IDisposable
    { public void Dispose() => restore(); }
    internal static void PrepareSandboxTestProfile()
    {
        if (!DistributionChannel.IsAppStore || RuntimePaths.TestRoot is not { } root) return;
        var bookmark = MacSandboxNative.PickTestDirectory(root)
            ?? throw new OperationCanceledException("未授权本次沙盒测试目录。");
        var opened = MacSandboxNative.OpenBookmark(bookmark);
        if (opened.Scope == IntPtr.Zero) throw new UnauthorizedAccessException("无法启用沙盒测试目录授权。");
        // Keep this bootstrap scope until the persistent manager owns an equivalent grant.
        try
        {
            Directory.CreateDirectory(RuntimePaths.DataDirectory);
            var store = Path.Combine(RuntimePaths.DataDirectory, "directory-bookmarks.json");
            if (!File.Exists(store)) File.WriteAllText(store, JsonSerializer.Serialize(new Dictionary<string, string> { [root] = bookmark }));
            _ = Current;
        }
        finally { MacSandboxNative.CloseBookmark(opened.Scope); }
    }
    private readonly string _store;
    private readonly IBookmarkAccess _backend;
    private readonly IReadOnlyList<string> _internalRoots;
    private readonly bool _restricted;
    private readonly object _gate = new();
    private readonly Dictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _movedPaths = new(StringComparer.Ordinal);
    private sealed record Grant(string Bookmark, IntPtr Scope);
    public IReadOnlyList<string> UnavailableRoots { get; private set; } = [];
    public IReadOnlyList<string> AuthorizedRoots { get { lock (_gate) return _grants.Keys.ToArray(); } }
    public event Action? GrantsChanged;

    internal DirectoryAccess(string store, bool restricted, IBookmarkAccess? backend = null, IReadOnlyList<string>? internalRoots = null)
    {
        _store = store; _restricted = restricted;
        _backend = backend ?? new NativeBookmarkAccess();
        _internalRoots = internalRoots ?? [RuntimePaths.DataDirectory, RuntimePaths.CacheDirectory, RuntimePaths.TemporaryDirectory, AppContext.BaseDirectory];
        if (!restricted) return;
        var loaded = Load();
        if (loaded != null) { _saved = loaded; if (RestoreError == null) Restore(); }
    }

    private sealed class BookmarkStore
    {
        public Dictionary<string, string> Bookmarks { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.Ordinal);
    }
    private BookmarkStore _saved = new();
    public string? RestoreError { get; private set; }

    private bool _loadFailed;

    private BookmarkStore? Load()
    {
        _loadFailed = false;
        RestoreError = null;
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllText(_store));
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var saved = json.RootElement.TryGetProperty(nameof(BookmarkStore.Bookmarks), out _)
                ? JsonSerializer.Deserialize<BookmarkStore>(json.RootElement.GetRawText())!
                : new BookmarkStore { Bookmarks = JsonSerializer.Deserialize<Dictionary<string, string>>(json.RootElement.GetRawText())! };
            if (saved.Bookmarks == null || saved.Aliases == null
                || saved.Bookmarks.Any(pair => !Path.IsPathFullyQualified(pair.Key) || string.IsNullOrEmpty(pair.Value))
                || saved.Aliases.Any(pair => !Path.IsPathFullyQualified(pair.Key) || string.IsNullOrEmpty(pair.Value) || !Path.IsPathFullyQualified(pair.Value)))
                throw new JsonException();
            return saved;
        }
        catch (FileNotFoundException) { return new(); }
        catch (DirectoryNotFoundException) { return new(); }
        catch (JsonException)
        {
            try
            {
                File.Copy(_store, _store + ".invalid-" + Guid.NewGuid().ToString("N"));
                RestoreError = "目录授权记录格式损坏，请重新选择文件夹。原记录已备份。";
                return new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _loadFailed = true;
                RestoreError = "目录授权记录损坏且备份失败，原文件未改写。请修复存储后重试：" + ex.Message;
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _loadFailed = true;
            RestoreError = "目录授权记录读取失败，原文件未改写。请修复存储后重试：" + ex.Message;
            return null;
        }
    }

    public void RetryUnavailable()
    {
        if (!_restricted) return;
        lock (_gate)
        {
            var loaded = Load();
            if (loaded == null) { Dispose(); _movedPaths.Clear(); UnavailableRoots = _saved.Bookmarks.Keys.ToArray(); }
            else
            {
                _saved = loaded;
                if (RestoreError == null) Restore();
                else { Dispose(); _movedPaths.Clear(); UnavailableRoots = []; }
            }
        }
        GrantsChanged?.Invoke();
    }

    private void Restore()
    {
        var restored = new Dictionary<string, Grant>(StringComparer.Ordinal);
        var saved = new BookmarkStore { Bookmarks = new(_saved.Bookmarks), Aliases = new(_saved.Aliases) };
        var unavailable = new List<string>();
        try
        {
            foreach (var (oldPath, bookmark) in _saved.Bookmarks)
            {
                var opened = _backend.Open(bookmark);
                if (opened.Scope == IntPtr.Zero || opened.Path == null)
                { if (opened.Scope != IntPtr.Zero) _backend.Close(opened.Scope); unavailable.Add(oldPath); continue; }
                if (restored.Remove(opened.Path, out var duplicate)) _backend.Close(duplicate.Scope);
                restored[opened.Path] = new(opened.Refreshed ?? bookmark, opened.Scope);
                saved.Bookmarks.Remove(oldPath);
                saved.Bookmarks[opened.Path] = opened.Refreshed ?? bookmark;
                if (opened.Path != oldPath)
                {
                    foreach (var alias in saved.Aliases.Keys.ToArray())
                        if (IsWithin(saved.Aliases[alias], oldPath))
                            saved.Aliases[alias] = opened.Path + saved.Aliases[alias][oldPath.Length..];
                    saved.Aliases[oldPath] = opened.Path;
                }
            }
            Save(saved);
        }
        catch (Exception ex)
        {
            foreach (var grant in restored.Values) _backend.Close(grant.Scope);
            Dispose();
            _movedPaths.Clear();
            UnavailableRoots = _saved.Bookmarks.Keys.ToArray();
            RestoreError = "目录授权恢复未完成，当前未授权，原记录已保留。请修复存储后重试：" + ex.Message;
            return;
        }
        foreach (var grant in _grants.Values) _backend.Close(grant.Scope);
        _grants.Clear();
        foreach (var item in restored) _grants.Add(item.Key, item.Value);
        _saved = saved;
        _movedPaths.Clear();
        foreach (var item in saved.Aliases) _movedPaths.Add(item.Key, item.Value);
        UnavailableRoots = unavailable;
    }

    public void RememberSelection(string path)
    {
        if (!_restricted) return;
        path = Path.GetFullPath(path);
        if (RuntimePaths.TestRoot is { } root && !IsWithin(_backend.RealPath(path), _backend.RealPath(root)))
            throw new UnauthorizedAccessException("测试授权必须位于本次测试目录。");
        lock (_gate)
        {
            if (_loadFailed)
            {
                var loaded = Load();
                if (loaded == null) throw new IOException(RestoreError);
                _saved = loaded;
            }
            var bookmark = _backend.Create(path, true);
            var opened = _backend.Open(bookmark);
            if (opened.Scope == IntPtr.Zero || opened.Path == null)
            {
                if (opened.Scope != IntPtr.Zero) _backend.Close(opened.Scope);
                throw new UnauthorizedAccessException("目录授权已失效，请重新选择。");
            }
            var saved = new BookmarkStore { Bookmarks = new(_saved.Bookmarks), Aliases = new(_saved.Aliases) };
            saved.Bookmarks.Remove(path);
            saved.Bookmarks[opened.Path] = opened.Refreshed ?? bookmark;
            if (path != opened.Path) saved.Aliases[path] = opened.Path;
            try { Save(saved); }
            catch { _backend.Close(opened.Scope); throw; }
            if (_grants.Remove(opened.Path, out var old)) _backend.Close(old.Scope);
            _grants[opened.Path] = new(opened.Refreshed ?? bookmark, opened.Scope);
            _saved = saved;
            if (path != opened.Path) _movedPaths[path] = opened.Path;
            UnavailableRoots = UnavailableRoots.Where(p => p != path && p != opened.Path).ToArray();
            RestoreError = null;
        }
        GrantsChanged?.Invoke();
    }

    public void Revoke(string path)
    {
        if (!_restricted) return;
        lock (_gate)
        {
            if (_loadFailed) throw new IOException(RestoreError);
            var saved = new BookmarkStore { Bookmarks = new(_saved.Bookmarks), Aliases = new(_saved.Aliases) };
            saved.Bookmarks.Remove(path);
            foreach (var alias in saved.Aliases.Where(p => IsWithin(p.Value, path)).Select(p => p.Key).ToArray())
                saved.Aliases.Remove(alias);
            Save(saved);
            _saved = saved;
            if (_grants.Remove(path, out var grant)) _backend.Close(grant.Scope);
            _movedPaths.Clear();
            foreach (var item in saved.Aliases) _movedPaths.Add(item.Key, item.Value);
            UnavailableRoots = UnavailableRoots.Where(p => p != path).ToArray();
        }
        GrantsChanged?.Invoke();
    }

    private void Save(BookmarkStore saved)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_store)!);
        var temporary = _store + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(saved));
            File.Move(temporary, _store, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public string ResolvePath(string path)
    {
        lock (_gate)
            foreach (var (oldPath, newPath) in _movedPaths.OrderByDescending(p => p.Key.Length))
                if (IsWithin(path, oldPath)) return newPath + path[oldPath.Length..];
        return path;
    }

    public bool CanAccess(string path)
    {
        if (!_restricted) return true;
        if (!Path.IsPathFullyQualified(path)) return false;
        path = _backend.RealPath(path);
        if (_internalRoots.Any(root => IsWithin(path, _backend.RealPath(root)))) return true;
        lock (_gate) return _grants.Keys.Any(root => IsWithin(path, _backend.RealPath(root)));
    }

    public void EnsureAccess(string path)
    {
        if (!CanAccess(path)) throw new UnauthorizedAccessException("此位置尚未授权或授权已失效。请通过“选择文件夹”重新授权；离线磁盘请先连接。");
    }

    public void ConfigureHelper(ProcessStartInfo start, params string[] paths)
    {
        if (!_restricted) return;
        foreach (var path in paths) EnsureAccess(path);
        lock (_gate) start.Environment["MACEXPLORER_FILE_BOOKMARKS"] = JsonSerializer.Serialize(
            _grants.Keys.Select(root => _backend.Create(root, explicitScope: false)).ToArray());
    }

    internal static bool IsWithin(string path, string root) => path == root
        || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var grant in _grants.Values) _backend.Close(grant.Scope);
            _grants.Clear();
        }
    }
}
