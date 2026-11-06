using SharpCompress.Archives.Zip;

namespace MacExplorer.Services;

internal static class EmbeddedThumbnailReader
{
    private const int MaxEntries = 512;
    private const int MaxPreviewBytes = 8 * 1024 * 1024;

    internal static bool Supports(string extension) => extension is ".kra" or ".krz" or ".ora";

    internal static async Task<byte[]?> ReadPngAsync(string path, string extension, int maxPixelSize, CancellationToken ct)
    {
        var previewName = extension == ".ora" ? "Thumbnails/thumbnail.png" : "preview.png";
        try
        {
            using var file = File.OpenRead(path);
            using var archive = ZipArchive.OpenArchive(file);
            var entries = archive.Entries.Take(MaxEntries);
            foreach (var name in new[] { previewName, "mergedimage.png" })
            {
                var entry = entries.FirstOrDefault(candidate =>
                {
                    ct.ThrowIfCancellationRequested();
                    return candidate.Key == name;
                });
                if (entry is not { IsDirectory: false, IsEncrypted: false, Size: > 0 and <= MaxPreviewBytes })
                    continue;

                // Read the known preview entry in memory. Paths inside the container
                // never become file system paths and layers are never decompressed.
                using var stream = entry.OpenEntryStream();
                var bytes = new byte[(int)entry.Size];
                await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
                if (BoundedImageDecoder.DecodePng(bytes, maxPixelSize, ct) is { } png)
                    return png;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* Invalid containers fall back to their file type icon. */ }
        return null;
    }
}
