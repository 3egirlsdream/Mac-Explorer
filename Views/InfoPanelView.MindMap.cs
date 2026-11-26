using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Preview;
using MacExplorer.ViewModels;
using SharpCompress.Common;

namespace MacExplorer.Views;

public partial class InfoPanelView
{
    private static async Task<PreviewLoadResult?> BuildMindMapPreviewResultAsync(FileSystemEntry entry,
        FileListViewModel viewModel, string extension, int maxPixels, CancellationToken token)
    {
        const string unavailable = "preview.mind-map.unavailable";
        var archived = ArchivePathHelper.IsArchivePath(entry.FullPath);
        if (!archived && !Path.IsPathFullyQualified(entry.FullPath)) return null;

        string? temporary = null;
        try
        {
            if (archived)
                temporary = await viewModel.ExtractAutomaticArchivePreviewAsync(entry.FullPath, token).ConfigureAwait(false);
            var path = archived ? temporary : entry.FullPath;
            if (path == null) return PreviewLoadResult.ForPlaceholder(unavailable);

            if (extension == ".emmx")
            {
                var bytes = await EmbeddedThumbnailReader.ReadPngAsync(path, extension, maxPixels, token).ConfigureAwait(false);
                return bytes != null && BoundedImageDecoder.Decode(bytes, maxPixels, token) is { } bitmap
                    ? PreviewLoadResult.ForImage(bitmap, bytes, badge: null)
                    : PreviewLoadResult.ForPlaceholder(unavailable);
            }

            var (status, document) = await MindMapReader.ReadAsync(path, extension, token).ConfigureAwait(false);
            if (document != null) return PreviewLoadResult.ForMindMap(document);
            if (status == MindMapReader.Status.NotMindMap && !archived) return null;
            return PreviewLoadResult.ForPlaceholder(status == MindMapReader.Status.TooLarge
                ? "preview.mind-map.too-large" : unavailable);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                   or ArchiveException or CryptographicException)
        {
            return PreviewLoadResult.ForPlaceholder(unavailable);
        }
        finally
        {
            if (temporary != null)
            {
                try { Directory.Delete(Path.GetDirectoryName(temporary)!, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void ClearMindMapPreview()
    {
        if (_mindMapPreview == null) return;
        _mindMapPreview.Clear();
        PreviewSurface.Children.Remove(_mindMapPreview);
        _mindMapPreview = null;
    }
}
