using MacExplorer.Models;

namespace MacExplorer.Services;

internal static class ArchivePreferences
{
    internal const string ExtractOnDoubleClick = "archive_extract_on_double_click";
    internal const string ShowThumbnails = "archive_show_thumbnails";
    internal const string DefaultFormat = "archive_default_format";
    internal const string SavePasswords = "archive_save_passwords";

    internal static ArchiveFormat ReadFormat(ISettingsService? settings)
        => Enum.TryParse<ArchiveFormat>(settings?.Get(DefaultFormat), out var format) && Enum.IsDefined(format)
            ? format : ArchiveFormat.Zip;
}
