using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacCatalyst.Services;

internal static class MacDirectoryPackages
{
    private static string? CandidateIcon(FileSystemEntry entry)
    {
        if (!OperatingSystem.IsMacOS() || !Path.IsPathFullyQualified(entry.FullPath)) return null;
        var iconKey = SqliteFileIndex.ResolveBundleIconKey(entry.Extension);
        if (iconKey != "folder") return iconKey;
        if (entry.Name.StartsWith("Photo Booth", StringComparison.OrdinalIgnoreCase)) return "app-bundle";
        return entry.Extension.Length != 0 && FileKindNames.GetPackageIconKey(entry.Extension.ToLowerInvariant()) != null
            ? iconKey : null;
    }

    private static DirectoryPackage Classify(string icon, MacSandboxNative.PackageInfo result)
    {
        if (result.IsPackage != true) return DirectoryPackage.Folder;
        return result.ApplicationPath == null ? new(true, icon)
            : new(true, "app-bundle", result.ApplicationPath, Math.Max(0, result.ApplicationSize));
    }

    internal static FileSystemEntry Confirm(FileSystemEntry entry, Batch? batch = null)
    {
        if (!entry.IsDirectory || entry.IsVirtual || CandidateIcon(entry) is not { } icon) return entry;
        if (!DirectoryAccess.Current.CanAccess(entry.FullPath))
            entry.Package = DirectoryPackage.Folder;
        else if (batch != null)
            batch.Add(entry, icon);
        else
        {
            var package = MacSandboxNative.DirectoryPackage(entry.FullPath, out var application);
            entry.Package = Classify(icon, new(package, application, application == null ? 0 : MacSandboxNative.ApplicationSize(application)));
        }
        return entry;
    }

    internal static void ConfirmAll(IEnumerable<FileSystemEntry> entries, CancellationToken token = default)
    {
        var batch = new Batch(MacSandboxNative.DirectoryPackages, token);
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            Confirm(entry, batch);
        }
        batch.Flush();
    }

    // Owned by one directory producer. Resolve before publishing any of these entries.
    internal sealed class Batch(Func<string[], MacSandboxNative.PackageInfo[]> read, CancellationToken token)
    {
        private List<(FileSystemEntry Entry, string Icon)>? _pending;

        internal void Add(FileSystemEntry entry, string icon)
        {
            (_pending ??= []).Add((entry, icon));
            if (_pending.Count == 256) Flush();
        }

        internal void Flush()
        {
            if (_pending is not { Count: > 0 }) return;
            token.ThrowIfCancellationRequested();
            var paths = new string[_pending.Count];
            for (var i = 0; i < paths.Length; i++) paths[i] = _pending[i].Entry.FullPath;
            var results = read(paths);
            token.ThrowIfCancellationRequested();
            for (var i = 0; i < paths.Length; i++)
            {
                var (entry, icon) = _pending[i];
                entry.Package = Classify(icon, results[i]);
            }
            _pending.Clear();
        }
    }
}
