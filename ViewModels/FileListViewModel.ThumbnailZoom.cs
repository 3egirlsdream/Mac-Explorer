using System.Globalization;
using MacExplorer.Services;

namespace MacExplorer.ViewModels;

public partial class FileListViewModel
{
    private const string ThumbnailZoomSetting = "file_list_thumbnail_zoom";
    private int? _thumbnailZoomLevel;

    public int ThumbnailZoomLevel => _thumbnailZoomLevel ??= ReadThumbnailZoomLevel();
    public bool IsGalleryMode => ViewMode == ViewMode.Grid && !IsBrowseOnly
        && ThumbnailZoomLevel == FileThumbnailSizing.GalleryLevel;

    private int ReadThumbnailZoomLevel()
    {
        var level = _settingsService?.Get(ThumbnailZoomSetting, FileThumbnailSizing.DefaultGridLevel)
            ?? FileThumbnailSizing.DefaultGridLevel;
        return level is >= 0 and <= FileThumbnailSizing.LargestGridLevel ? level : FileThumbnailSizing.DefaultGridLevel;
    }

    internal void SetThumbnailZoomLevel(int level)
    {
        if (ViewMode != ViewMode.Grid || IsBrowseOnly) return;
        level = Math.Clamp(level, 0, FileThumbnailSizing.GalleryLevel);
        if (level == ThumbnailZoomLevel) return;
        var wasGallery = IsGalleryMode;
        _thumbnailZoomLevel = level;
        if (level != FileThumbnailSizing.GalleryLevel)
            _settingsService?.SetDeferred(ThumbnailZoomSetting, level.ToString(CultureInfo.InvariantCulture));
        OnPropertyChanged(nameof(ThumbnailZoomLevel));
        if (wasGallery != IsGalleryMode) OnPropertyChanged(nameof(IsGalleryMode));
    }
}
