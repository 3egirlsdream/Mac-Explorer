namespace MacExplorer.Services.Impl;

internal readonly record struct DirectorySizeResult(long Bytes, bool IsComplete);

internal static class DirectorySizeCalculator
{
    private static readonly SemaphoreSlim Slots = new(2);

    internal static async Task<DirectorySizeResult> CalculateAsync(string path, CancellationToken token = default)
    {
        await Slots.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Calculate(path, token), token).ConfigureAwait(false);
        }
        finally { Slots.Release(); }
    }

    private static DirectorySizeResult Calculate(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var access = DirectoryAccess.Current;
        access.EnsureAccess(path);
        var rootAttributes = File.GetAttributes(path);
        if (!rootAttributes.HasFlag(FileAttributes.Directory)) throw new DirectoryNotFoundException(path);
        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint)) return new(0, false);

        long bytes = 0;
        var complete = true;
        var pending = new Stack<string>();
        pending.Push(path);
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                access.EnsureAccess(directory);
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var attributes = entry.Attributes;
                        // Never traverse link targets or count their contents twice.
                        if (attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                        access.EnsureAccess(entry.FullName);
                        if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry.FullName);
                        else bytes = checked(bytes + ((FileInfo)entry).Length);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        complete = false;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                complete = false;
            }
        }
        token.ThrowIfCancellationRequested();
        return new(bytes, complete);
    }
}
