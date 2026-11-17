using System.Collections.Concurrent;
using Avalonia.Platform;
using MacExplorer.Models;
using SkiaSharp;

namespace MacExplorer.Services.Impl;

internal static class FolderCoverPreviewService
{
    private static readonly ConcurrentDictionary<(FolderPhotoCoverStyle Style, bool Photo), Lazy<Task<byte[]>>> Previews = new();

    internal static Task<byte[]> GetAsync(FolderPhotoCoverStyle style, CancellationToken token, bool photo = true)
        => Previews.GetOrAdd((style, photo), value => new Lazy<Task<byte[]>>(() => Task.Run(() => Create(value.Style, value.Photo))))
            .Value.WaitAsync(token);

    private static byte[] Create(FolderPhotoCoverStyle style, bool withPhoto)
    {
        if (style != FolderPhotoCoverStyle.Default)
            return (withPhoto ? FolderArtwork.ReadPreview(style) : FolderArtwork.ReadIcon(256, style))
                ?? SvgIconCache.RenderFolderPng(256);
        if (!withPhoto) return SvgIconCache.RenderFolderPng(256, style);
        var photos = new List<SKBitmap>();
        try
        {
            foreach (var asset in new[] { "FolderCoverSamples/alpine-lake.jpg", "BackgroundThemes/Previews/nature-trellis.png", "BackgroundThemes/Previews/nature-marigold.png" })
            {
                using var input = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/" + asset));
                using var bytes = new MemoryStream(); input.CopyTo(bytes);
                if (BoundedImageDecoder.DecodeSkia(bytes.ToArray(), 256) is { } bitmap) photos.Add(bitmap);
            }
            return FolderPhotoCoverService.ComposeStyle(photos, 256, style);
        }
        finally { foreach (var photo in photos) photo.Dispose(); }
    }
}
