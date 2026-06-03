using System.Diagnostics;
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

    public Task AddAsync(string bundleId, string label, bool isTopLevel)
    {
        try
        {
            // Read system icon for the app
            var iconBase64 = ReadAppIconBase64(bundleId);

            int maxOrder;
            lock (_lock) { maxOrder = _cache.Count > 0 ? _cache.Max(a => a.SortOrder) + 1 : 0; }

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
            cmd.ExecuteNonQuery();

            LoadAll(); // Refresh cache
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to add open_with_app {BundleId}", bundleId);
        }
        return Task.CompletedTask;
    }

    public Task UpdateAsync(int id, string? label, bool? isTopLevel, int? sortOrder)
    {
        try
        {
            var existing = _cache.FirstOrDefault(a => a.Id == id);
            if (existing == null) return Task.CompletedTask;

            var newLabel = label ?? existing.Label;
            var newIsTopLevel = isTopLevel ?? existing.IsTopLevel;
            var newSortOrder = sortOrder ?? existing.SortOrder;

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE open_with_apps SET label = @label, is_top_level = @isTopLevel, sort_order = @sortOrder WHERE id = @id
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@label", newLabel);
            cmd.Parameters.AddWithValue("@isTopLevel", newIsTopLevel ? 1 : 0);
            cmd.Parameters.AddWithValue("@sortOrder", newSortOrder);
            cmd.ExecuteNonQuery();

            LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update open_with_app {Id}", id);
        }
        return Task.CompletedTask;
    }

    public Task RemoveAsync(int id)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM open_with_apps WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", id);
            cmd.ExecuteNonQuery();

            LoadAll();
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to remove open_with_app {Id}", id);
        }
        return Task.CompletedTask;
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

                            var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
                            if (dict == null) continue;

                            var bundleId = dict["CFBundleIdentifier"]?.ToString();
                            var name = dict["CFBundleName"]?.ToString() ?? Path.GetFileNameWithoutExtension(dir);
                            if (string.IsNullOrEmpty(bundleId)) continue;

                            var iconBase64 = ExtractIconBase64(dir);

                            apps.Add(new AppListItem
                            {
                                Name = name,
                                BundleId = bundleId,
                                AppPath = dir,
                                IconBase64 = iconBase64,
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

    private static string? ExtractIconBase64(string appPath)
    {
        try
        {
            // Use JXA to get app icon as base64 PNG — write to temp file (matching MacFileService pattern)
            var escapedPath = appPath.Replace("\\", "\\\\").Replace("'", "\\'");
            var script = "ObjC.import('AppKit');\n" +
                "ObjC.import('Foundation');\n" +
                "var ws = $.NSWorkspace.sharedWorkspace;\n" +
                "var icon = ws.iconForFile('" + escapedPath + "');\n" +
                "var sz = $.NSMakeSize(128, 128);\n" +
                "var newImg = $.NSImage.alloc.initWithSize(sz);\n" +
                "newImg.lockFocus;\n" +
                "icon.drawInRectFromRectOperationFraction($.NSMakeRect(0,0,128,128), $.NSZeroRect, $.NSCompositingOperationSourceOver, 1.0);\n" +
                "newImg.unlockFocus;\n" +
                "var tiff = newImg.TIFFRepresentation;\n" +
                "var rep = $.NSBitmapImageRep.imageRepWithData(tiff);\n" +
                "var png = rep.representationUsingTypeProperties($.NSBitmapImageFileTypePNG, $({}));\n" +
                "var base64 = png.base64EncodedStringWithOptions(0);\n" +
                "ObjC.unwrap(base64);\n";

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
                process.WaitForExit(5000);

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
        // Find the .app path for this bundleId, then extract icon
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
                        var dict = Foundation.NSMutableDictionary.FromFile(plistPath);
                        if (dict?["CFBundleIdentifier"]?.ToString() == bundleId)
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
    }
}
