using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Markdown;
using MacExplorer.Services.Preview;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private ILocalizationService? _toolbarLanguage;
    private string? _loadedPreviewText;
    internal Action<FileSystemEntry>? ExtractToNamedFolder { get; set; }

    private void ConfigureToolbar()
    {
        PreviewToolbar.PreviousClicked += () => _ = StepPreviewAsync(isPrevious: true);
        PreviewToolbar.NextClicked += () => _ = StepPreviewAsync(isPrevious: false);
        PreviewToolbar.BranchClicked += () => _ = RunToolbarActionAsync(async () =>
        {
            if (_selectedNode is { } node) await TogglePreviewNodeAsync(node);
        });
        PreviewToolbar.ZoomClicked += () => _ = RunToolbarActionAsync(() =>
        {
            ToggleImageZoom();
            return Task.CompletedTask;
        });
        PreviewToolbar.RotateClicked += () => _ = RunToolbarActionAsync(() =>
        {
            RotateGalleryImage(90);
            return Task.CompletedTask;
        });
        PreviewToolbar.CheckerboardToggled += enabled => _ = RunToolbarActionAsync(() =>
        {
            _checkerboardOverride = enabled;
            GalleryShowCheckerboard = enabled;
            UpdatePreviewToolbar();
            return Task.CompletedTask;
        });
        PreviewToolbar.CopyImageClicked += () => _ = RunToolbarActionAsync(CopyCurrentImageAsync);
        PreviewToolbar.CopyTextClicked += () => _ = RunToolbarActionAsync(CopyLoadedTextAsync);
        PreviewToolbar.FirstPageClicked += () => _ = RunToolbarActionAsync(() =>
        {
            NavigateDocumentFirstPage();
            return Task.CompletedTask;
        });
        PreviewToolbar.ExtractClicked += () => _ = RunToolbarActionAsync(() =>
        {
            if (_selectedEntry is { } entry) ExtractToNamedFolder?.Invoke(entry);
            return Task.CompletedTask;
        });
        PreviewToolbar.TagClicked += () =>
        {
            if (_fileListContext?.FileTagService != null && _selectedEntry is { IsVirtual: false } entry)
                PreviewToolbar.ToggleTagPopup(_fileListContext, entry.FullPath);
        };
        PreviewToolbar.TagPopupClosed += () =>
        {
            if (_selectedEntry is { } entry && IsVisible)
                _ = LoadOverlayTagsAsync(entry);
        };
        PreviewToolbar.OpenClicked += () => _ = RunToolbarActionAsync(async () =>
        {
            if (_selectedEntry is { } entry && GetLocalPath(entry.FullPath) is { } path
                && App.Services?.GetService<IApplicationLauncherService>() is { } launcher)
                await launcher.OpenFileAsync(path);
        });
        PreviewToolbar.DeleteClicked += () =>
        {
            if (_fileListContext == null || _selectedEntry is not { IsVirtual: false, IsWritable: true } entry) return;
            _fileListContext.SelectEntry(entry);
            Close();
            _fileListContext.ShowDeleteConfirmDialog();
        };
        PreviewToolbar.RevealClicked += () => _ = RunToolbarActionAsync(async () =>
        {
            if (GetLocalPath(_selectedNode?.RevealPath ?? _selectedEntry?.FullPath) is { } path
                && App.Services?.GetService<IApplicationLauncherService>() is { } launcher)
                await launcher.RevealInFinderAsync(path);
        });
        PreviewToolbar.CopyPathClicked += () => _ = RunToolbarActionAsync(() =>
            CopyPreviewTextAsync(_selectedNode?.DisplayPath ?? _selectedEntry?.FullPath));
    }

    private async Task RunToolbarActionAsync(Func<Task> execute)
    {
        try { await execute(); }
        catch (Exception ex) when (TopLevel.GetTopLevel(this) is Window window)
        {
            AppToast.ShowError(window, ex.Message);
        }
    }

    private static string? GetLocalPath(string? path)
        => path != null && Path.IsPathFullyQualified(path) ? path : null;

    private static Task CopyPreviewTextAsync(string? text)
        => text != null && App.Services?.GetService<IClipboardService>() is { } clipboard
            ? clipboard.CopyTextAsync(text) : Task.CompletedTask;

    private void RefreshToolbarLanguage()
    {
        UpdatePreviewToolbar();
        if (_selectedNode is { } node && (node.IsArchive || node.Entry.IsFolder)) ShowNodeSummary(node);
    }

    private void UpdatePreviewToolbar()
    {
        if (_isCompactPreview || !IsVisible) return;
        if (_selectedEntry is not { } entry)
        {
            PreviewToolbar.IsVisible = false;
            UpdatePreviewToolbarPlacement();
            return;
        }

        var canExpand = _selectedNode?.CanExpand == true;
        var expanded = _selectedNode?.IsExpanded == true;
        var archive = _selectedNode?.IsArchive == true;
        var folder = entry.IsFolder;
        var localPath = GetLocalPath(entry.FullPath);
        var revealPath = GetLocalPath(_selectedNode?.RevealPath ?? entry.FullPath);
        var launcherAvailable = App.Services?.GetService<IApplicationLauncherService>() != null;
        var clipboardAvailable = App.Services?.GetService<IClipboardService>() != null;
        var icon = FileIconResolver.ResolveIconKey(Path.GetExtension(entry.Name), entry.Name);
        var image = !archive && !entry.IsDirectory && icon == "file-image";
        var pdf = !entry.IsDirectory && Path.GetExtension(entry.Name).Equals(".pdf", StringComparison.OrdinalIgnoreCase);
        var text = !entry.IsDirectory && !archive && !pdf && !IsDocumentFile(entry.Name)
            && !DiffPreviewDocument.IsDiff(entry.Name) && (IsTextFile(entry) || MarkdownDocument.IsMarkdown(entry.Name));
        var media = icon is "file-video" or "file-audio";

        string? counterText = null;
        if (_selectedNode == null && _previewFiles.Length > 1)
        {
            var idx = Array.FindIndex(_previewFiles, f => f.FullPath == entry.FullPath);
            if (idx >= 0) counterText = $"{idx + 1} / {_previewFiles.Length}";
        }

        string? metaText = null;
        if (HasGalleryImage && _previewBitmap is { } bmp)
        {
            var parts = new List<string>(3) { $"{bmp.PixelSize.Width} × {bmp.PixelSize.Height}" };
            if (!string.IsNullOrWhiteSpace(entry.FormattedSize)) parts.Add(entry.FormattedSize);
            if (_galleryRotationDegrees != 0) parts.Add($"{_galleryRotationDegrees}°");
            metaText = string.Join(" · ", parts);
        }
        else if (!string.IsNullOrWhiteSpace(entry.FormattedSize) && !entry.IsDirectory)
        {
            metaText = entry.FormattedSize;
        }

        PreviewToolbar.ApplyState(new PreviewActionBarState
        {
            CounterText = counterText,
            FileName = null, // Header already displays the filename in overlay mode.
            MetaText = metaText,
            CompactIdentity = true,
            ShowStepButtons = HasNativePreview && (_previousPreviewIndex >= 0 || _nextPreviewIndex >= 0),
            CanStepPrevious = _previousPreviewIndex >= 0,
            CanStepNext = _nextPreviewIndex >= 0,
            ShowBranch = canExpand,
            IsBranchExpanded = expanded,
            ShowZoom = image,
            IsZoomed = ZoomedImageScroll.IsVisible,
            IsZoomEnabled = _previewBitmap != null,
            ShowRotate = image && _previewBitmap != null,
            ShowCheckerboard = image && _previewBitmap != null,
            IsCheckerboardChecked = _galleryShowCheckerboard,
            ShowCopyImage = image && _previewBitmap != null && clipboardAvailable,
            ShowCopyText = text && clipboardAvailable,
            IsCopyTextEnabled = _loadedPreviewText != null,
            ShowFirstPage = pdf,
            IsFirstPageEnabled = _documentPreview != null,
            ShowExtract = archive && localPath != null && ExtractToNamedFolder != null,
            ShowTag = _fileListContext?.FileTagService != null && !entry.IsVirtual && localPath != null && _selectedNode == null,
            ShowOpen = launcherAvailable && localPath != null && !archive && !folder,
            OpenUsesPlayIcon = media,
            ShowDelete = _fileListContext != null && !entry.IsVirtual && entry.IsWritable && localPath != null && _selectedNode == null,
            ShowReveal = launcherAvailable && revealPath != null,
            ShowCopyPath = clipboardAvailable && (localPath != null || ArchivePathHelper.IsArchivePath(entry.FullPath))
        });

        PreviewToolbar.IsVisible = PreviewToolbar.HasVisibleContent;
        UpdateToolbarLabels();
        UpdatePreviewToolbarPlacement();
        _ = LoadOverlayTagsAsync(entry);
    }

    private async Task LoadOverlayTagsAsync(FileSystemEntry entry)
    {
        CancelOverlayTags();
        if (_isCompactPreview || _fileListContext?.FileTagService is not { } tagService || entry.IsVirtual || _selectedNode != null)
        {
            PreviewToolbar.SetTags(null);
            return;
        }
        var cts = _overlayTagCts = new CancellationTokenSource();
        var token = cts.Token;
        try
        {
            var tags = await tagService.GetFileTagsAsync(entry.FullPath, token);
            if (token.IsCancellationRequested || !IsVisible || !ReferenceEquals(_selectedEntry, entry)) return;
            PreviewToolbar.SetTags(tags);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch { }
    }

    private void UpdatePreviewToolbarPlacement()
    {
        if (_isCompactPreview) return;

        var native = HasNativePreview;
        PreviewPreviousSlot.IsVisible = !native && _previousPreviewIndex >= 0;
        PreviewNextSlot.IsVisible = !native && _nextPreviewIndex >= 0;

        // Match FileGalleryView: reserve bottom breathing room so AppKit previews
        // never overlap the in-page glass toolbar (no floating Popup windows needed).
        PreviewHost.Margin = native && PreviewToolbar.IsVisible
            ? new Thickness(0, 0, 0, 68)
            : default;

        // The transparent hover zones must sit outside the selectable document.
        _markdownPreview?.SetNavigationGutters(ArrowGutter(PreviewPreviousSlot), ArrowGutter(PreviewNextSlot));

        static double ArrowGutter(Border slot)
            => slot.IsVisible ? slot.Width + slot.Margin.Left + slot.Margin.Right : 0;
    }

    private void OnPreviewCardSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (ZoomedImageScroll.IsVisible) SizeZoomedImage();
        UpdateToolbarLabels();
    }

    private void UpdateToolbarLabels()
    {
        PreviewToolbar.UpdateAvailableWidth(PreviewCard.Bounds.Width);
    }

    private void ToggleImageZoom()
    {
        if (_previewBitmap == null) return;
        var zoomed = !ZoomedImageScroll.IsVisible;
        if (zoomed)
        {
            if (UseEnhancedImageCanvas)
            {
                ZoomedImage.IsVisible = false;
                ZoomedImage.Source = null;
                GalleryZoomedCanvas.Source = _previewBitmap;
                GalleryZoomedCanvas.IsVisible = true;
                SyncGalleryImageCanvas();
            }
            else
            {
                GalleryZoomedCanvas.IsVisible = false;
                GalleryZoomedCanvas.Source = null;
                ZoomedImage.IsVisible = true;
                ZoomedImage.Source = _previewBitmap;
            }
            SizeZoomedImage();
            ZoomedImageScroll.Offset = default;
        }
        else if (UseEnhancedImageCanvas)
        {
            GalleryZoomedCanvas.IsVisible = false;
        }
        ZoomedImageScroll.IsVisible = zoomed;
        PreviewImage.IsVisible = !UseEnhancedImageCanvas && !zoomed;
        GalleryImageCanvas.IsVisible = UseEnhancedImageCanvas && !zoomed;
        UpdatePreviewToolbar();
    }

    private void SizeZoomedImage()
    {
        if (_previewBitmap is not { } bitmap || PreviewHost.Bounds.Width <= 0 || PreviewHost.Bounds.Height <= 0) return;
        if (UseEnhancedImageCanvas)
        {
            var swapped = _galleryRotationDegrees is 90 or 270;
            var effW = swapped ? bitmap.Size.Height : bitmap.Size.Width;
            var effH = swapped ? bitmap.Size.Width : bitmap.Size.Height;
            var scale = 2 * Math.Min(PreviewHost.Bounds.Width / effW, PreviewHost.Bounds.Height / effH);
            GalleryZoomedCanvas.Width = effW * scale + GalleryZoomedCanvas.InnerMargin * 2;
            GalleryZoomedCanvas.Height = effH * scale + GalleryZoomedCanvas.InnerMargin * 2;
            return;
        }
        var normalScale = 2 * Math.Min(PreviewHost.Bounds.Width / bitmap.Size.Width, PreviewHost.Bounds.Height / bitmap.Size.Height);
        ZoomedImage.Width = bitmap.Size.Width * normalScale;
        ZoomedImage.Height = bitmap.Size.Height * normalScale;
    }
}
