using System.Collections.Concurrent;
using System.Diagnostics;
using System.Xml.Linq;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace MacExplorer.Services.Impl;

public class OpenWithAppService : IOpenWithAppService, IDisposable
{
    private readonly DatabaseConnectionFactory _connectionFactory;
    private readonly SqliteConnection _connection;
    private readonly ILogger<OpenWithAppService>? _logger;
    private List<OpenWithApp> _cache = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<string?>> _iconLoads = new(StringComparer.OrdinalIgnoreCase);

    public OpenWithAppService(DatabaseConnectionFactory connectionFactory, ILogger<OpenWithAppService>? logger = null)
    {
        _connectionFactory = connectionFactory;
        _connection = connectionFactory.GetConnection();
        _logger = logger;
        LoadAll();
        _ = Task.Run(FillMissingIcons);
    }

    private void LoadAll()
    {
        try
        {
            var list = ReadAll(_connection);
            lock (_lock) { _cache = list; }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load open_with_apps in {Method}", nameof(LoadAll));
        }
    }

    private static List<OpenWithApp> ReadAll(SqliteConnection connection)
    {
        var list = new List<OpenWithApp>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id, bundle_id, label, is_top_level, sort_order, icon_base64 FROM open_with_apps ORDER BY sort_order";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new OpenWithApp
            {
                Id = reader.GetInt32(0),
                BundleId = reader.GetString(1),
                Label = reader.GetString(2),
                IsTopLevel = reader.GetInt32(3) != 0,
                SortOrder = reader.GetInt32(4),
                IconBase64 = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
        return list;
    }

    private void FillMissingIcons()
    {
        List<OpenWithApp> needsIcon;
        lock (_lock) { needsIcon = _cache.Where(a => a.IconBase64 == null).ToList(); }

        if (needsIcon.Count == 0) return;

        using var connection = _connectionFactory.GetConnection();
        foreach (var app in needsIcon)
        {
            try
            {
                var icon = ReadAppIconBase64(app.BundleId);
                if (icon != null)
                {
                    using var cmd = connection.CreateCommand();
                    cmd.CommandText = "UPDATE open_with_apps SET icon_base64 = @icon WHERE id = @id";
                    cmd.Parameters.AddWithValue("@id", app.Id);
                    cmd.Parameters.AddWithValue("@icon", icon);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        try
        {
            var refreshed = ReadAll(connection);
            lock (_lock) { _cache = refreshed; }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to refresh open_with_apps cache after icon fill");
        }
    }

    public Task<List<OpenWithApp>> GetAllAsync()
    {
        lock (_lock) { return Task.FromResult(new List<OpenWithApp>(_cache)); }
    }

    public Task<List<OpenWithApp>> GetTopLevelAppsAsync()
    {
        lock (_lock) { return Task.FromResult(_cache.Where(a => a.IsTopLevel).ToList()); }
    }

    public Task<List<OpenWithApp>> GetSubmenuAppsAsync()
    {
        lock (_lock) { return Task.FromResult(_cache.Where(a => !a.IsTopLevel).ToList()); }
    }

    public Task<string?> GetAppIconBase64Async(string bundleId)
    {
        if (_iconCache.TryGetValue(bundleId, out var cachedIcon))
            return Task.FromResult<string?>(cachedIcon);

        return _iconLoads.GetOrAdd(bundleId, id => Task.Run<string?>(() =>
        {
            try
            {
                var icon = ReadAppIconBase64(id);
                if (!string.IsNullOrEmpty(icon))
                    _iconCache.TryAdd(id, icon);
                return icon;
            }
            finally
            {
                _iconLoads.TryRemove(id, out _);
            }
        }));
    }

    public Task<string?> GetAppIconBase64ByPathAsync(string appPath)
    {
        if (string.IsNullOrWhiteSpace(appPath))
            return Task.FromResult<string?>(null);

        var cacheKey = $"path:{appPath}";
        if (_iconCache.TryGetValue(cacheKey, out var cachedIcon))
            return Task.FromResult<string?>(cachedIcon);

        return _iconLoads.GetOrAdd(cacheKey, key => Task.Run<string?>(() =>
        {
            try
            {
                var icon = ExtractIconBase64(appPath);
                if (!string.IsNullOrEmpty(icon))
                    _iconCache.TryAdd(key, icon);
                return icon;
            }
            finally
            {
                _iconLoads.TryRemove(key, out _);
            }
        }));
    }

    public async Task AddAsync(string bundleId, string label, bool isTopLevel, string? iconBase64 = null)
    {
        try
        {
            int maxOrder;
            lock (_lock) { maxOrder = _cache.Count > 0 ? _cache.Max(a => a.SortOrder) + 1 : 0; }

            await _connectionLock.WaitAsync();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO open_with_apps (bundle_id, label, is_top_level, sort_order, icon_base64)
                    VALUES (@bundleId, @label, @isTopLevel, @sortOrder, @iconBase64)
                    """;
                cmd.Parameters.AddWithValue("@bundleId", bundleId);
                cmd.Parameters.AddWithValue("@label", label);
                cmd.Parameters.AddWithValue("@isTopLevel", isTopLevel ? 1 : 0);
                cmd.Parameters.AddWithValue("@sortOrder", maxOrder);
                cmd.Parameters.AddWithValue("@iconBase64", (object?)iconBase64 ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync();

                LoadAll(); // Refresh cache
            }
            finally
            {
                _connectionLock.Release();
            }

            if (string.IsNullOrWhiteSpace(iconBase64))
                _ = Task.Run(FillMissingIcons);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add open_with_app {BundleId}", bundleId);
        }
    }

    public async Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder)
    {
        try
        {
            OpenWithApp? existing;
            lock (_lock) { existing = _cache.FirstOrDefault(a => a.Id == id); }
            if (existing == null) return;

            var newLabel = label ?? existing.Label;
            var newIsTopLevel = isTopLevel ?? existing.IsTopLevel;
            var newSortOrder = sortOrder ?? existing.SortOrder;

            await _connectionLock.WaitAsync();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = """
                    UPDATE open_with_apps SET label = @label, is_top_level = @isTopLevel, sort_order = @sortOrder WHERE id = @id
                    """;
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@label", newLabel);
                cmd.Parameters.AddWithValue("@isTopLevel", newIsTopLevel ? 1 : 0);
                cmd.Parameters.AddWithValue("@sortOrder", newSortOrder);
                await cmd.ExecuteNonQueryAsync();

                LoadAll();
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update open_with_app {Id}", id);
        }
    }

    public async Task RemoveAsync(int id)
    {
        try
        {
            await _connectionLock.WaitAsync();
            try
            {
                using var cmd = _connection.CreateCommand();
                cmd.CommandText = "DELETE FROM open_with_apps WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();

                LoadAll();
            }
            finally
            {
                _connectionLock.Release();
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove open_with_app {Id}", id);
        }
    }

    public async Task<int> RemoveUnavailableAppsAsync()
    {
        List<OpenWithApp> configuredApps;
        lock (_lock) { configuredApps = new List<OpenWithApp>(_cache); }

        var unavailableIds = await Task.Run(() => configuredApps
            .Where(app => GetApplicationAvailability(app.BundleId) == ApplicationAvailability.NotInstalled)
            .Select(app => app.Id)
            .ToList());
        if (unavailableIds.Count == 0)
            return 0;

        await _connectionLock.WaitAsync();
        try
        {
            using var transaction = _connection.BeginTransaction();
            foreach (var id in unavailableIds)
            {
                using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM open_with_apps WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                await cmd.ExecuteNonQueryAsync();
            }
            transaction.Commit();
            LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove unavailable open-with applications");
            return 0;
        }
        finally
        {
            _connectionLock.Release();
        }

        return unavailableIds.Count;
    }

    public Task<List<AppListItem>> GetInstalledAppsAsync()
    {
        return Task.Run(() =>
        {
            var apps = new List<AppListItem>();
            var searchPaths = new[] { "/Applications", "/System/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") };

            foreach (var searchPath in searchPaths)
            {
                try
                {
                    if (!Directory.Exists(searchPath)) continue;
                    foreach (var dir in Directory.EnumerateDirectories(searchPath, "*.app", SearchOption.TopDirectoryOnly))
                    {
                        try
                        {
                            var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                            if (!File.Exists(plistPath)) continue;

                            var bundleId = ReadPlistValue(plistPath, "CFBundleIdentifier");
                            var name = ReadPlistValue(plistPath, "CFBundleName") ?? Path.GetFileNameWithoutExtension(dir);
                            if (string.IsNullOrEmpty(bundleId)) continue;

                            apps.Add(new AppListItem
                            {
                                Name = name,
                                BundleId = bundleId,
                                AppPath = dir,
                            });
                        }
                        catch { }
                    }
                }
                catch { }
            }

            return apps.OrderBy(a => a.Name).ToList();
        });
    }

    private static ApplicationAvailability GetApplicationAvailability(string bundleId)
    {
        try
        {
            var script = $"""
                ObjC.import('AppKit');
                var url = $.NSWorkspace.sharedWorkspace.URLForApplicationWithBundleIdentifier('{EscapeJavaScript(bundleId)}');
                url ? ObjC.unwrap(url.path) : '';
                """;
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-l", "JavaScript", "-e", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process == null)
                return ApplicationAvailability.Unknown;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return ApplicationAvailability.Unknown;
            }

            _ = errorTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                return ApplicationAvailability.Unknown;

            var appPath = outputTask.GetAwaiter().GetResult().Trim();
            return !string.IsNullOrEmpty(appPath) && Directory.Exists(appPath)
                ? ApplicationAvailability.Installed
                : ApplicationAvailability.NotInstalled;
        }
        catch
        {
            return ApplicationAvailability.Unknown;
        }
    }

    private static string EscapeJavaScript(string value) =>
        value.Replace("\\", "\\\\").Replace("'", "\\'");

    private enum ApplicationAvailability
    {
        Unknown,
        Installed,
        NotInstalled
    }

    private static string? ExtractIconBase64(string appPath)
    {
        try
        {
            // Use AppKit directly so menu icons match the actual app installed on this Mac.
            var escapedPath = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = 
                "ObjC.import('AppKit');\n" +
                "ObjC.import('Foundation');\n" +
                "var ws = $.NSWorkspace.sharedWorkspace;\n" +
                "var icon = ws.iconForFile('" + escapedPath + "');\n" +
                "var rep = $.NSBitmapImageRep.alloc.initWithBitmapDataPlanesPixelsWidePixelsHighBitsPerSampleSamplesPerPixelHasAlphaIsPlanarColorSpaceNameBytesPerRowBitsPerPixel(null, 64, 64, 8, 4, true, false, $.NSDeviceRGBColorSpace, 0, 0);\n" +
                "var ctx = $.NSGraphicsContext.graphicsContextWithBitmapImageRep(rep);\n" +
                "$.NSGraphicsContext.saveGraphicsState;\n" +
                "$.NSGraphicsContext.setCurrentContext(ctx);\n" +
                "icon.drawInRectFromRectOperationFraction($.NSMakeRect(0, 0, 64, 64), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);\n" +
                "$.NSGraphicsContext.restoreGraphicsState;\n" +
                "var png = rep.representationUsingTypeProperties(4, $.NSDictionary.dictionary);\n" +
                "var base64 = ObjC.unwrap(png.base64EncodedStringWithOptions(0));\n" +
                "base64;\n";

            var scriptPath = Path.Combine(Path.GetTempPath(), $"fkfinder_icon_{Guid.NewGuid():N}.js");
            File.WriteAllText(scriptPath, script);

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("-l");
                psi.ArgumentList.Add("JavaScript");
                psi.ArgumentList.Add(scriptPath);

                var process = Process.Start(psi);
                if (process == null) return null;
                var output = process.StandardOutput.ReadToEnd().Trim();
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }
                if (process.ExitCode != 0)
                    return null;

                return string.IsNullOrEmpty(output) ? null : output;
            }
            finally
            {
                try { File.Delete(scriptPath); } catch { }
            }
        }
        catch { return null; }
    }

    private string? ReadAppIconBase64(string bundleId)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "/usr/bin/mdfind",
                ArgumentList = { $"kMDItemCFBundleIdentifier == '{bundleId}'" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process != null)
            {
                if (!process.WaitForExit(1500))
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    return null;
                }

                var appPath = process.StandardOutput.ReadToEnd()
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(path => path.EndsWith(".app", StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(appPath))
                    return ExtractIconBase64(appPath);
            }
        }
        catch { }

        // Fallback for systems where Spotlight indexing is unavailable.
        var searchPaths = new[] { "/Applications", "/System/Applications", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications") };
        foreach (var searchPath in searchPaths)
        {
            try
            {
                if (!Directory.Exists(searchPath)) continue;
                foreach (var dir in Directory.EnumerateDirectories(searchPath, "*.app", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        var plistPath = Path.Combine(dir, "Contents", "Info.plist");
                        if (!File.Exists(plistPath)) continue;
                        var bundleId2 = ReadPlistValue(plistPath, "CFBundleIdentifier");
                        if (string.IsNullOrEmpty(bundleId2)) continue;
                        if (bundleId2 == bundleId)
                        {
                            var icon = ExtractIconBase64(dir);
                            System.Diagnostics.Debug.WriteLine($"[ReadAppIconBase64] {bundleId} -> {dir} -> icon: {(icon != null ? $"{icon.Length} chars" : "NULL")}");
                            return icon;
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }
        System.Diagnostics.Debug.WriteLine($"[ReadAppIconBase64] {bundleId} -> NOT FOUND");
        return null;
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
        _connectionLock.Dispose();
    }

    private static string? ReadPlistValue(string plistPath, string key)
    {
        try
        {
            var xmlValue = TryReadXmlPlistValue(plistPath, key);
            if (!string.IsNullOrWhiteSpace(xmlValue))
                return xmlValue;

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/libexec/PlistBuddy",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add($"Print :{key}");
            psi.ArgumentList.Add(plistPath);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return null;
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return process.ExitCode == 0 && !string.IsNullOrEmpty(output) ? output : null;
        }
        catch { return null; }
    }

    private static string? TryReadXmlPlistValue(string plistPath, string key)
    {
        try
        {
            using var stream = File.OpenRead(plistPath);
            Span<byte> header = stackalloc byte[8];
            var read = stream.Read(header);
            stream.Position = 0;
            if (read >= 6 && header[..6].SequenceEqual("bplist"u8))
                return null;

            var doc = XDocument.Load(stream);
            var dict = doc.Root?.Element("dict");
            if (dict == null) return null;

            var elements = dict.Elements().ToList();
            for (var i = 0; i < elements.Count - 1; i++)
            {
                if (elements[i].Name.LocalName != "key" || elements[i].Value != key)
                    continue;

                var value = elements[i + 1];
                return value.Name.LocalName switch
                {
                    "string" => value.Value,
                    "true" => "true",
                    "false" => "false",
                    "integer" => value.Value,
                    _ => null
                };
            }
        }
        catch
        {
        }

        return null;
    }

}
