using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal static class FolderPhotoCoverStyles
{
    internal const string SettingKey = "folder_photo_cover_style";

    internal static FolderPhotoCoverStyle Read(ISettingsService? settings)
        => settings?.Get(SettingKey)?.ToLowerInvariant() switch
        {
            "frosted" => FolderPhotoCoverStyle.Frosted,
            "skeuomorphic" => FolderPhotoCoverStyle.Skeuomorphic,
            _ => FolderPhotoCoverStyle.Default
        };

    internal static string Value(FolderPhotoCoverStyle style) => style switch
    {
        FolderPhotoCoverStyle.Frosted => "frosted",
        FolderPhotoCoverStyle.Skeuomorphic => "skeuomorphic",
        _ => "default"
    };
}
