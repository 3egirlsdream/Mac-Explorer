using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.Views;

namespace MacExplorer.Converters;

/// <summary>
/// Converts a FileSystemEntry to a rendered SVG Bitmap (via SvgIconCache).
/// Replaces the old PathIcon + Geometry approach with full-colour Fluent-style icons.
/// </summary>
public class FileEntryToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Keep legacy preview bindings working while FileListView binds a small
        // value descriptor. Neither route is permitted to perform file I/O.
        FileIconSource source;
        if (value is FileIconSource descriptor)
            source = descriptor;
        else if (value is FileSystemEntry entry)
            source = entry.GridIconSource;
        else
            return null;

        var size = ParseIconSize(parameter, culture);
        if (!string.IsNullOrWhiteSpace(source.CachedImagePath))
        {
            var cached = FileListView.TryGetCachedEntryImage(source.CachedImagePath);
            if (cached != null) return cached;
        }

        if (!source.IsDirectory)
            return SvgIconCache.GetFileIcon(source.IconKey, source.Extension, size);

        return source.IconKey is { Length: > 3 } && source.IconKey.StartsWith("ai-")
            ? SvgIconCache.GetAiIcon(source.IconKey, size)
            : SvgIconCache.GetFolderIcon(size);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();

    private static int ParseIconSize(object? parameter, CultureInfo culture)
    {
        var size = parameter switch
        {
            int value => value,
            double value => (int)Math.Round(value),
            decimal value => (int)Math.Round(value),
            string value when int.TryParse(value, NumberStyles.Integer, culture, out var parsed) => parsed,
            string value when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 48
        };

        return Math.Clamp(size, 16, 512);
    }
}

/// <summary>
/// Legacy color converter — kept for backward compatibility.
/// The SVG bitmaps already carry their own colours; this returns a neutral fallback.
/// </summary>
public class FileEntryToIconColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return Brushes.Gray; // unused when Image replaces PathIcon
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
