using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

public partial class ArchiveService
{
    private static readonly SemaphoreSlim[] ExtractionGates = Enumerable.Range(0, 32)
        .Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public Task ExtractAsync(string archivePath, string destinationPath, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default, string? password = null)
        => ExtractAsync(archivePath, new ArchiveExtractionOptions(destinationPath, ArchiveExtractionMode.CurrentFolder), progress, ct, password);

    public Task ExtractAsync(string archivePath, ArchiveExtractionOptions options, IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default, string? password = null)
        => FileTaskContext.RunAsync(_tasks, new(BackgroundTaskKind.Extract, LocalizationText.Get("正在解压..."),
            [archivePath], options.Directory), async context =>
            {
                await ExtractCoreAsync(archivePath, options, progress, context.CancellationToken, password);
                return true;
            }, ct);

    private async Task ExtractCoreAsync(
        string archivePath, ArchiveExtractionOptions options,
        IProgress<ArchiveProgress>? progress = null,
        CancellationToken ct = default,
        string? password = null)
    {
        DirectoryAccess.Current.EnsureAccess(archivePath);
        DirectoryAccess.Current.EnsureAccess(options.Directory);
        var destinationKey = ArchiveOutputDirectory(options.Directory);
        var gate = ExtractionGates[(StringComparer.OrdinalIgnoreCase.GetHashCode(destinationKey) & int.MaxValue) % ExtractionGates.Length];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        ArchivePasswordVault.Source? source = null;
        bool verified;
        try
        {
            verified = await ReadArchiveAsync(() => ExtractEntries(archivePath, options, progress, ct, password,
                out source), ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
        if (verified && password != null && _passwords != null)
            await _passwords.SaveAsync(archivePath, source, password, ct).ConfigureAwait(false);
    }

    private bool ExtractEntries(string archivePath, ArchiveExtractionOptions options, IProgress<ArchiveProgress>? progress,
        CancellationToken ct, string? password, out ArchivePasswordVault.Source? source)
    {
        source = password != null ? _passwords?.Capture(archivePath) : null;
        var context = FileTaskContext.Current;
        if (context != null) context.Task.IsIndeterminate = true;
        progress?.Report(new ArchiveProgress { IsActive = true, IsIndeterminate = true,
            CurrentFile = Path.GetFileName(archivePath), OperationLabel = LocalizationText.Get("正在解压...") });
        CheckCompressionSource(archivePath);
        var metadata = new List<ArchiveReadEntry>();
        var requested = options.EntryKeys?.Select(NormalizePath).ToArray();
        bool Includes(string key) => key.Length != 0 && (requested == null || requested.Any(item =>
            key.TrimEnd('/') == item.TrimEnd('/') || item.EndsWith('/') && key.StartsWith(item, StringComparison.Ordinal)));
        var parentPrefix = NormalizePath(options.RelativeTo).TrimEnd('/');
        if (parentPrefix.Length > 0) parentPrefix += "/";
        string OutputKey(string key) => parentPrefix.Length == 0 ? key
            : key.StartsWith(parentPrefix, StringComparison.Ordinal) ? key[parentPrefix.Length..]
            : throw new IOException(LocalizationText.Get("压缩包包含越界路径。"));
        using (var scan = OpenArchive(archivePath, password, ct))
        {
            while (scan.MoveNext())
            {
                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                var entry = scan.Entry with { Key = NormalizePath(scan.Entry.Key) };
                if (!Includes(entry.Key)) continue;
                ValidateEntry(options.Directory, entry, resolveLinks: false);
                ValidateExtractionPath(options.Directory, OutputKey(entry.Key), resolveLinks: false);
                metadata.Add(entry);
            }
        }
        CheckCompressionSource(archivePath);

        if (metadata.Count == 0)
        {
            if (requested != null) throw new FileNotFoundException(LocalizationText.Get("archive.entry_missing", requested.FirstOrDefault() ?? ""));
            if (options.Mode != ArchiveExtractionMode.CurrentFolder)
                throw new IOException(LocalizationText.Get("archive.empty"));
            return false;
        }

        var rootNames = metadata.Select(e => OutputKey(e.Key).Split('/')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationPath = options.Mode == ArchiveExtractionMode.NamedFolder
            ? ArchiveExtractionPathHelper.GetNamedDestination(options.Directory, archivePath)
            : options.Mode == ArchiveExtractionMode.Smart && rootNames.Count > 1
                ? context?.Remember("smart-extract-directory", () => ArchiveExtractionPathHelper.CreateUniqueExtractionDirectory(options.Directory, archivePath))
                    ?? ArchiveExtractionPathHelper.CreateUniqueExtractionDirectory(options.Directory, archivePath)
                : options.Directory;
        EnsureArchiveDirectory(options.Directory, destinationPath);
        var askOnConflict = options.Mode == ArchiveExtractionMode.NamedFolder || requested != null;
        var mapKey = "extract-map:" + archivePath + ":" + destinationPath;
        var renameMap = context?.Remember(mapKey, () => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!askOnConflict && context?.IsDone(mapKey) != true)
        {
            renameMap.Clear();
            var reservedNames = new HashSet<string>(rootNames, StringComparer.OrdinalIgnoreCase);
            foreach (var rootName in rootNames)
            {
                if (!PathExists(Path.Combine(destinationPath, rootName))) continue;
                var name = Path.GetFileNameWithoutExtension(rootName);
                var extension = Path.GetExtension(rootName);
                var counter = 2;
                string renamed;
                do { renamed = $"{name} {counter++}{extension}"; }
                while (reservedNames.Contains(renamed) || PathExists(Path.Combine(destinationPath, renamed)));
                renameMap[rootName] = renamed;
                reservedNames.Add(renamed);
            }
            context?.MarkDone(mapKey);
        }

        var totalEntries = metadata.Count(e => !e.IsDirectory);
        var processed = 0;
        string[] CalculateOutputPaths() => BuildExtractionPaths(destinationPath,
            metadata.Select(entry => entry with { Key = OutputKey(entry.Key) }).ToList(), renameMap);
        var outputPaths = context?.Remember(mapKey + ":paths", CalculateOutputPaths) ?? CalculateOutputPaths();
        var reserved = outputPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        FileConflictDecision? remaining = null;
        var passwordVerified = false;
        var entryIndex = 0;
        if (context != null) context.Task.TotalItems = metadata.Count;
        using var archive = OpenArchive(archivePath, password, ct);
        while (archive.MoveNext())
        {
            ct.ThrowIfCancellationRequested();
            FileTaskContext.Checkpoint();
            var entry = archive.Entry with { Key = NormalizePath(archive.Entry.Key) };
            if (!Includes(entry.Key)) continue;
            ValidateEntry(destinationPath, entry, resolveLinks: false);
            if (entryIndex >= metadata.Count || entry.Key != metadata[entryIndex].Key
                || entry.IsDirectory != metadata[entryIndex].IsDirectory || entry.Size != metadata[entryIndex].Size)
                throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
            var entryPath = Path.GetRelativePath(destinationPath, outputPaths[entryIndex++]);
            var fullDest = ValidateExtractionPath(destinationPath, entryPath);
            if (entry.IsDirectory)
            {
                EnsureArchiveDirectory(options.Directory, fullDest);
                context?.ReportItem(entry.Key, fullDest, FileTaskItemState.Completed);
                continue;
            }

            var key = "extract-entry:" + archivePath + ":" + fullDest;
            if (context?.IsDone(key) == true) { processed++; continue; }
            var parent = Path.GetDirectoryName(fullDest)!;
            EnsureArchiveDirectory(options.Directory, parent);
            FileIdentitySnapshot? replaced = null;
            if (askOnConflict && (PathExists(fullDest) || new FileInfo(fullDest).LinkTarget != null))
            {
                var choice = ResolveArchiveConflict(archivePath, entry, fullDest, remaining, ct);
                if (choice.Decision.ApplyToRemaining) remaining = choice.Decision;
                if (choice.Decision.Action == FileConflictAction.Skip)
                {
                    context?.MarkDone(key);
                    context?.ReportItem(entry.Key, fullDest, FileTaskItemState.Skipped);
                    processed++;
                    continue;
                }
                if (choice.Decision.Action == FileConflictAction.KeepBoth)
                    fullDest = FindAvailableArchiveFile(fullDest, reserved);
                else
                    replaced = choice.Expected;
            }
            var temporary = askOnConflict
                ? Path.Combine(parent, "." + Guid.NewGuid().ToString("N") + ".fkfinder-tmp")
                : fullDest + "." + (context?.Task.Id ?? Guid.NewGuid().ToString("N")) + ".fkfinder-tmp";
            var temporaryOwned = false;
            void Report(long copied, bool complete)
            {
                var indeterminate = entry.Size == null && !complete;
                var fraction = complete ? 1 : entry.Size > 0 ? Math.Min((double)copied / entry.Size.Value, 0.999) : 0;
                var percentage = totalEntries > 0 ? (processed + fraction) / totalEntries * 100 : 0;
                if (context != null) context.Task.IsIndeterminate = indeterminate;
                progress?.Report(new ArchiveProgress { IsActive = true, Percentage = percentage,
                    IsIndeterminate = indeterminate, CurrentFile = entryPath,
                    OperationLabel = LocalizationText.Get("正在解压...") });
            }
            try
            {
                Report(0, false);
                using (var entryStream = archive.OpenEntryStream())
                using (var fileStream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
                {
                    temporaryOwned = true;
                    try
                    {
                        passwordVerified |= CopyArchiveEntry(entry, entryStream, fileStream, ct,
                            archive.AuthenticatesEntry, copied => Report(copied, false));
                    }
                    catch { archive.StopReading(); throw; }
                }
                ct.ThrowIfCancellationRequested();
                FileTaskContext.Checkpoint();
                CheckCompressionSource(archivePath);
                EnsureArchiveDirectory(options.Directory, parent, create: false);
                ValidateExtractionPath(destinationPath, Path.GetRelativePath(destinationPath, fullDest));
                if (replaced != null && FileIdentitySnapshot.Capture(fullDest, true, ct) != replaced)
                    throw new IOException(LocalizationText.Get("conflict.changed"));
                File.Move(temporary, fullDest, overwrite: replaced != null);
                context?.MarkDone(key);
                context?.ReportItem(entry.Key, fullDest, FileTaskItemState.Completed);
                Report(0, true);
            }
            finally { if (temporaryOwned) TryDeleteFile(temporary); }
            processed++;
        }
        if (entryIndex != metadata.Count)
            throw new IOException(LocalizationText.Get("源项目已变化，请重新创建任务。"));
        if (context != null) context.Task.IsIndeterminate = false;
        progress?.Report(new ArchiveProgress { IsActive = true, Percentage = 100,
            OperationLabel = LocalizationText.Get("正在解压...") });
        return passwordVerified;
    }

    private (FileConflictDecision Decision, FileIdentitySnapshot? Expected) ResolveArchiveConflict(
        string archivePath, ArchiveReadEntry entry, string target, FileConflictDecision? remaining, CancellationToken ct)
    {
        var info = new FileInfo(target);
        var attributes = info.Attributes;
        if (attributes == (FileAttributes)(-1)) throw new IOException(LocalizationText.Get("conflict.changed"));
        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        var isLink = info.LinkTarget != null;
        var canReplace = !isDirectory && !isLink;
        var expected = canReplace ? FileIdentitySnapshot.Capture(target, true, ct) : null;
        if (canReplace && expected == null) throw new IOException(LocalizationText.Get("conflict.changed"));
        var modified = entry.LastModifiedTime ?? DateTime.MinValue;
        var source = new FileSystemEntry { Name = Path.GetFileName(entry.Key), Size = entry.Size ?? 0,
            FullPath = Path.Combine(archivePath, entry.Key), LastModified = modified, Extension = Path.GetExtension(entry.Key) };
        var existing = new FileSystemEntry { Name = info.Name, FullPath = target, IsDirectory = isDirectory,
            IsSymbolicLink = isLink, Size = canReplace ? info.Length : 0, LastModified = info.LastWriteTime,
            Extension = info.Extension };
        var decision = remaining;
        if (decision == null || !canReplace && decision.Action is FileConflictAction.Replace or FileConflictAction.ReplaceOlder)
            decision = _conflicts?.ResolveAsync(new(source, existing, false, false) { CanReplace = canReplace }, ct)
                .GetAwaiter().GetResult() ?? new(FileConflictAction.KeepBoth);
        if (!canReplace && decision.Action is FileConflictAction.Replace or FileConflictAction.ReplaceOlder)
            return (new(FileConflictAction.Skip), null);
        if (decision.Action == FileConflictAction.ReplaceOlder && modified <= existing.LastModified)
            return (new(FileConflictAction.Skip, decision.ApplyToRemaining), null);
        return (decision, expected);
    }

    private static string FindAvailableArchiveFile(string path, HashSet<string> reserved)
    {
        var parent = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        string alternative;
        var index = 1;
        do { alternative = Path.Combine(parent, $"{name} ({index++}){extension}"); }
        while (reserved.Contains(alternative) || PathExists(alternative) || new FileInfo(alternative).LinkTarget != null);
        reserved.Add(alternative);
        return alternative;
    }

    private static string ArchiveOutputDirectory(string path) => OperatingSystem.IsMacOS()
        ? Platforms.MacOS.MacSandboxNative.RealPath(path) : Path.GetFullPath(path);

    private static void EnsureArchiveDirectory(string root, string path, bool create = true)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative != ".") ValidateExtractionPath(root, relative, resolveLinks: false);
        var current = root;
        EnsureCurrent();
        if (relative == ".") return;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            EnsureCurrent();
        }

        void EnsureCurrent()
        {
            if (new FileInfo(current).LinkTarget != null || File.Exists(current))
                throw new IOException(LocalizationText.Get("archive.blocked_directory", current));
            if (Directory.Exists(current)) return;
            if (!create) throw new IOException(LocalizationText.Get("conflict.changed"));
            Directory.CreateDirectory(current);
        }
    }
}
