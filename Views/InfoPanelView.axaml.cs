using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Views;

public partial class InfoPanelView : UserControl
{
    private static readonly HashSet<string> TextPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml", ".yaml", ".yml",
        ".ini", ".conf", ".config", ".toml", ".properties", ".sh", ".zsh", ".bash", ".fish", ".ps1",
        ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx", ".html", ".htm", ".css", ".scss", ".less",
        ".py", ".rb", ".php", ".java", ".kt", ".kts", ".swift", ".m", ".mm", ".h", ".hpp", ".c", ".cpp",
        ".go", ".rs", ".sql", ".graphql", ".gql", "Dockerfile", ".gitignore", ".editorconfig"
    };
    private static readonly HashSet<string> DocumentPreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
        ".pages", ".numbers", ".key", ".rtf", ".odt", ".ods", ".odp"
    };
    private const long MaxTextPreviewBytes = 2L * 1024 * 1024;
    private FileListViewModel? _subscribedViewModel;
    private int _previewGeneration;
    private int _tagWriteGeneration;
    private readonly IImageAnalysisService? _imageAnalysisService;
    private readonly IClipboardService? _clipboardService;
    private readonly IDirectoryChangeNotifier? _directoryChangeNotifier;
    private CancellationTokenSource? _panelLoadCts;
    private string? _currentFilePath;
    private readonly HashSet<string> _selectedSystemTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _pendingFinderTagsLock = new();
    private readonly Dictionary<string, List<string>> _pendingFinderTags = new(StringComparer.Ordinal);
    private const int PreviewSelectionDebounceMs = 120;
    private const string FinderTagsAttribute = "com.apple.metadata:_kMDItemUserTags";
    private const string LegacyFinderTagsAttribute = "com.apple.metadata:kMDItemUserTags";
    private bool _isPreviewExpanded;
    private byte[]? _ocrPreviewBytes;
    private CancellationTokenSource? _ocrCts;
    private CancellationTokenSource? _panelUpdateDebounceCts;

    public event EventHandler<bool>? PreviewExpandedChanged;

    // macOS Finder tag names as shown in the user's Finder sidebar.
    private static readonly (string Name, string Color)[] FinderTagColors =
    [
        ("红色", "#FF3B30"), ("橙色", "#FF9500"), ("黄色", "#FFCC00"),
        ("绿色", "#34C759"), ("蓝色", "#007AFF"), ("紫色", "#AF52DE"),
        ("灰色", "#8E8E93")
    ];

    private static readonly Dictionary<string, int> FinderTagColorIndexes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["灰色"] = 1,
        ["绿色"] = 2,
        ["紫色"] = 3,
        ["蓝色"] = 4,
        ["黄色"] = 5,
        ["红色"] = 6,
        ["橙色"] = 7,
        ["Gray"] = 1,
        ["Green"] = 2,
        ["Purple"] = 3,
        ["Blue"] = 4,
        ["Yellow"] = 5,
        ["Red"] = 6,
        ["Orange"] = 7
    };

    public InfoPanelView()
    {
        InitializeComponent();
        _imageAnalysisService = App.Services.GetService<IImageAnalysisService>();
        _clipboardService = App.Services.GetService<IClipboardService>();
        _directoryChangeNotifier = App.Services.GetService<IDirectoryChangeNotifier>();
        RenderOptions.SetBitmapInterpolationMode(
            PreviewImage,
            global::Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality);
        BuildSystemTags();
    }

    private FileListViewModel? ViewModel => DataContext as FileListViewModel;

    protected override void OnDataContextChanged(EventArgs e)
    {
        CancelQueuedPanelUpdate();
        CancelPanelLoad();
        if (_subscribedViewModel != null)
        {
            _subscribedViewModel.SelectedEntries.CollectionChanged -= OnSelectionChanged;
            _subscribedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _subscribedViewModel.TransientInteractionStarted -= OnTransientInteractionStarted;
        }

        base.OnDataContextChanged(e);
        if (ViewModel != null)
        {
            _subscribedViewModel = ViewModel;
            _subscribedViewModel.SelectedEntries.CollectionChanged += OnSelectionChanged;
            _subscribedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _subscribedViewModel.TransientInteractionStarted += OnTransientInteractionStarted;
            _ = UpdatePanelAsync();
        }
    }

    private void OnTransientInteractionStarted()
    {
        CancelQueuedPanelUpdate();
        CancelPanelLoad();
    }

    private void OnSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (ViewModel?.IsSelectionPreviewSuppressed == true)
        {
            CancelQueuedPanelUpdate();
            CancelPanelLoad();
            return;
        }

        if (ViewModel?.IsInfoPanelVisible == true)
            QueuePanelUpdate();
        else
        {
            CancelQueuedPanelUpdate();
            CancelPanelLoad();
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileListViewModel.CurrentMetadata))
            ApplyCurrentMetadata();
        else if (e.PropertyName == nameof(FileListViewModel.IsInfoPanelVisible))
        {
            if (ViewModel?.IsInfoPanelVisible == true)
            {
                CancelQueuedPanelUpdate();
                _ = UpdatePanelAsync();
            }
            else
            {
                CancelQueuedPanelUpdate();
                CancelPanelLoad();
                ClearPanel();
            }
        }
    }

    private void QueuePanelUpdate()
    {
        CancelPanelLoad();
        CancelQueuedPanelUpdate();

        _panelUpdateDebounceCts = new CancellationTokenSource();
        var token = _panelUpdateDebounceCts.Token;
        _ = DebouncedUpdatePanelAsync(token);
    }

    private async Task DebouncedUpdatePanelAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(PreviewSelectionDebounceMs, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            await UpdatePanelAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UpdatePanelAsync()
    {
        CancelPanelLoad();
        var viewModel = ViewModel;
        if (viewModel == null || !viewModel.IsInfoPanelVisible || viewModel.SelectedEntries.Count != 1)
        {
            ClearPanel();
            return;
        }

        _panelLoadCts = new CancellationTokenSource();
        var cancellationToken = _panelLoadCts.Token;
        var generation = ++_previewGeneration;
        var entry = viewModel.SelectedEntries[0];
        _currentFilePath = entry.FullPath;
        ResetPreviewContent(entry.IsDirectory ? "文件夹无法预览" : "正在生成预览…");

        // Basic info
        InfoPath.Text = Path.GetDirectoryName(entry.FullPath) ?? "/";
        InfoSize.Text = entry.FormattedSize;
        InfoType.Text = entry.KindText;
        InfoModified.Text = entry.LastModified.ToString("yyyy-MM-dd HH:mm");
        InfoCreated.Text = entry.Created.ToString("yyyy-MM-dd HH:mm");
        InfoAccessed.Text = "—";
        UpdateExifTab(null);
        UpdateTagsFromMetadata(entry.FullPath, []);

        // Folder metadata invokes several native probes (owner, type, xattrs and tags).
        // The list already contains all information needed by this panel, so avoid
        // starting that work — especially while a large directory is still arriving in batches.
        if (entry.IsDirectory)
        {
            UpdateExifTab(null);
            UpdateTagsFromMetadata(entry.FullPath, []);
            return;
        }

        ApplyCurrentMetadata();
        StartPreviewLoad(entry, viewModel, _isPreviewExpanded, generation, cancellationToken);
    }

    private void ApplyCurrentMetadata()
    {
        if (ViewModel?.IsInfoPanelVisible != true || ViewModel.SelectedEntries.Count != 1)
            return;

        var entry = ViewModel.SelectedEntries[0];
        var metadata = ViewModel.CurrentMetadata;
        if (metadata?.FullPath != entry.FullPath)
            return;

        InfoAccessed.Text = metadata.LastAccessed.ToString("yyyy-MM-dd HH:mm");
        UpdateExifTab(metadata.ImageInfo);
        UpdateTagsFromMetadata(entry.FullPath, metadata.Tags);
    }

    private void StartPreviewLoad(
        FileSystemEntry entry,
        FileListViewModel viewModel,
        bool isPreviewExpanded,
        int generation,
        CancellationToken cancellationToken)
    {
        _ = Task.Factory.StartNew(async () =>
        {
            PreviewLoadResult? result = null;
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                result = await BuildPreviewResultAsync(entry, viewModel, isPreviewExpanded, cancellationToken)
                    .ConfigureAwait(false);
                if (result == null || generation != _previewGeneration || cancellationToken.IsCancellationRequested)
                {
                    result?.Dispose();
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (generation != _previewGeneration || cancellationToken.IsCancellationRequested)
                    {
                        result.Dispose();
                        return;
                    }

                    ApplyPreviewResult(result);
                }, DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                result?.Dispose();
            }
            catch
            {
                result?.Dispose();
                Dispatcher.UIThread.Post(() =>
                {
                    if (generation == _previewGeneration && !cancellationToken.IsCancellationRequested)
                        PreviewPlaceholder.Text = "预览生成失败";
                }, DispatcherPriority.Background);
            }
        }, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private async Task<PreviewLoadResult?> BuildPreviewResultAsync(
        FileSystemEntry entry,
        FileListViewModel viewModel,
        bool isPreviewExpanded,
        CancellationToken cancellationToken)
    {
        if (entry.IsDirectory || !await FileExistsAsync(entry.FullPath, cancellationToken).ConfigureAwait(false))
            return null;

        var extension = Path.GetExtension(entry.FullPath);
        var fileName = Path.GetFileName(entry.FullPath);
        if (TextPreviewExtensions.Contains(extension) || TextPreviewExtensions.Contains(fileName))
        {
            try
            {
                var fileLength = await GetFileLengthAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
                if (fileLength > MaxTextPreviewBytes)
                    return PreviewLoadResult.ForPlaceholder("文本文件过大（仅支持 2 MB 以内）");

                var bytes = await File.ReadAllBytesAsync(entry.FullPath, cancellationToken).ConfigureAwait(false);
                var textPreview = await Task.Run(() => BuildTextPreview(bytes, cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
                if (textPreview.IsBinary)
                    return PreviewLoadResult.ForPlaceholder("该文件包含二进制内容，无法作为文本预览");

                var label = extension.TrimStart('.').ToUpperInvariant() is { Length: > 0 } textLabel
                    ? textLabel
                    : "TEXT";
                return PreviewLoadResult.ForText(textPreview.Text, label);
            }
            catch
            {
                return PreviewLoadResult.ForPlaceholder("无法读取文本内容");
            }
        }

        try
        {
            var requestedPixels = isPreviewExpanded ? 1800 : 900;

            var thumbnailUrl = entry.ThumbnailUrl;
            var bytes = await TryGetPreviewBytesAsync(
                viewModel.GetPreviewThumbnailAsync(entry, requestedPixels, cancellationToken))
                .ConfigureAwait(false);
            bytes ??= await BuildFallbackPreviewBytesAsync(entry, thumbnailUrl, cancellationToken)
                .ConfigureAwait(false);
            if (bytes == null)
            {
                var placeholder = DocumentPreviewExtensions.Contains(extension)
                    ? "系统无法为此文档生成预览"
                    : "暂不支持此文件类型";
                return PreviewLoadResult.ForPlaceholder(placeholder);
            }

            var bitmap = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return CreatePreviewBitmap(bytes, extension, requestedPixels);
            }, cancellationToken).ConfigureAwait(false);

            string? badge = null;
            if (DocumentPreviewExtensions.Contains(extension))
                badge = extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
                    ? "PDF 首页"
                    : $"{extension.TrimStart('.').ToUpperInvariant()} 预览";
            return PreviewLoadResult.ForImage(bitmap, bytes, badge);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return PreviewLoadResult.ForPlaceholder("预览生成失败");
        }
    }

    private static async Task<byte[]?> TryGetPreviewBytesAsync(Task<byte[]?> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void ApplyPreviewResult(PreviewLoadResult result)
    {
        if (!string.IsNullOrEmpty(result.Placeholder))
        {
            PreviewPlaceholder.Text = result.Placeholder;
            return;
        }

        if (result.Text != null)
        {
            PreviewText.Text = result.Text;
            PreviewText.IsVisible = true;
            PreviewPlaceholder.IsVisible = false;
            if (!string.IsNullOrEmpty(result.Badge))
                ShowPreviewBadge(result.Badge);
            return;
        }

        if (result.Bitmap != null)
        {
            var bitmap = result.Bitmap;
            result.Bitmap = null;
            PreviewImage.Source = bitmap;
            PreviewImage.IsVisible = true;
            PreviewPlaceholder.IsVisible = false;
            _ocrPreviewBytes = result.OcrBytes;
            CopyImageTextBtn.IsVisible = _imageAnalysisService != null;
            if (!string.IsNullOrEmpty(result.Badge))
                ShowPreviewBadge(result.Badge);
        }
    }

    private void CancelPanelLoad()
    {
        _panelLoadCts?.Cancel();
        _panelLoadCts?.Dispose();
        _panelLoadCts = null;
        _previewGeneration++;
    }

    private void CancelQueuedPanelUpdate()
    {
        _panelUpdateDebounceCts?.Cancel();
        _panelUpdateDebounceCts?.Dispose();
        _panelUpdateDebounceCts = null;
    }

    private static global::Avalonia.Media.Imaging.Bitmap RenderSvgToBitmap(byte[] svgBytes, int maxSize)
    {
        using var svgImage = new Svg.Skia.SKSvg();
        using var stream = new MemoryStream(svgBytes);
        svgImage.Load(stream);

        if (svgImage.Picture is null)
        {
            using var bmp = new SkiaSharp.SKBitmap(1, 1);
            using var data = bmp.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return new global::Avalonia.Media.Imaging.Bitmap(data.AsStream());
        }

        var width = svgImage.Picture.CullRect.Width;
        var height = svgImage.Picture.CullRect.Height;
        var scale = Math.Min(maxSize / width, maxSize / height);
        if (scale <= 0) scale = 1;

        var renderWidth = (int)(width * scale);
        var renderHeight = (int)(height * scale);

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(renderWidth, renderHeight));
        var canvas = surface.Canvas;
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        canvas.Scale(scale, scale);
        canvas.DrawPicture(svgImage.Picture);
        canvas.Flush();

        using var encoded = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return new global::Avalonia.Media.Imaging.Bitmap(encoded.AsStream());
    }

    private void ResetPreviewContent(string placeholder)
    {
        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = null;
        _ocrPreviewBytes = null;
        CopyImageTextBtn.IsVisible = false;
        CopyImageTextBtn.IsEnabled = true;
        CopyImageTextBtn.Content = "复制图片文字";
        var imageToDispose = PreviewImage.Source as IDisposable;
        PreviewImage.Source = null;
        if (imageToDispose != null)
            _ = Task.Run(imageToDispose.Dispose);
        PreviewImage.IsVisible = false;
        PreviewText.Text = string.Empty;
        PreviewText.IsVisible = false;
        PreviewKindBadge.IsVisible = false;
        PreviewPlaceholder.Text = placeholder;
        PreviewPlaceholder.IsVisible = true;
    }

    private void ShowPreviewBadge(string text)
    {
        PreviewKindText.Text = text;
        PreviewKindBadge.IsVisible = true;
    }

    private static bool HasUnicodeBom(byte[] bytes) => bytes.Length >= 2 &&
        ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF));

    private static TextPreviewResult BuildTextPreview(byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scanLength = Math.Min(bytes.Length, 4096);
        for (var i = 0; i < scanLength; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[i] == 0 && !HasUnicodeBom(bytes))
                return new TextPreviewResult(true, string.Empty);
        }

        return new TextPreviewResult(false, DecodeText(bytes));
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return new UTF8Encoding(false, false).GetString(bytes);
    }

    private readonly record struct TextPreviewResult(bool IsBinary, string Text);

    private static async Task<byte[]?> BuildFallbackPreviewBytesAsync(
        FileSystemEntry entry,
        string? thumbnailUrl,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(thumbnailUrl))
        {
            var thumbnailBytes = await LoadThumbnailUrlBytesAsync(thumbnailUrl, cancellationToken)
                .ConfigureAwait(false);
            if (thumbnailBytes != null)
                return thumbnailBytes;
        }

        return await TryReadSmallOriginalImageAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private static global::Avalonia.Media.Imaging.Bitmap CreatePreviewBitmap(
        byte[] bytes,
        string extension,
        int maxPixelSize)
    {
        if (extension.Equals(".svg", StringComparison.OrdinalIgnoreCase))
            return RenderSvgToBitmap(bytes, maxPixelSize);

        using var decoded = SkiaSharp.SKBitmap.Decode(bytes);
        if (decoded == null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            using var stream = new MemoryStream(bytes);
            return new global::Avalonia.Media.Imaging.Bitmap(stream);
        }

        var longestEdge = Math.Max(decoded.Width, decoded.Height);
        if (longestEdge <= maxPixelSize)
        {
            using var stream = new MemoryStream(bytes);
            return new global::Avalonia.Media.Imaging.Bitmap(stream);
        }

        var scale = maxPixelSize / (double)longestEdge;
        var targetWidth = Math.Max(1, (int)Math.Round(decoded.Width * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(decoded.Height * scale));
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(
            targetWidth,
            targetHeight,
            SkiaSharp.SKColorType.Bgra8888,
            SkiaSharp.SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SkiaSharp.SKColors.Transparent);
        using var paint = new SkiaSharp.SKPaint
        {
            IsAntialias = true
        };
        using var image = SkiaSharp.SKImage.FromBitmap(decoded);
        canvas.DrawImage(
            image,
            new SkiaSharp.SKRect(0, 0, targetWidth, targetHeight),
            new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.Linear),
            paint);
        canvas.Flush();
        using var encoded = surface.Snapshot().Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
        return new global::Avalonia.Media.Imaging.Bitmap(encoded.AsStream());
    }

    private sealed class PreviewLoadResult : IDisposable
    {
        private PreviewLoadResult()
        {
        }

        public string? Placeholder { get; private init; }
        public string? Text { get; private init; }
        public string? Badge { get; private init; }
        public global::Avalonia.Media.Imaging.Bitmap? Bitmap { get; set; }
        public byte[]? OcrBytes { get; private init; }

        public static PreviewLoadResult ForPlaceholder(string placeholder)
            => new() { Placeholder = placeholder };

        public static PreviewLoadResult ForText(string text, string badge)
            => new() { Text = text, Badge = badge };

        public static PreviewLoadResult ForImage(
            global::Avalonia.Media.Imaging.Bitmap bitmap,
            byte[] ocrBytes,
            string? badge)
            => new() { Bitmap = bitmap, OcrBytes = ocrBytes, Badge = badge };

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
        }
    }

    private static Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() => File.Exists(path), cancellationToken);

    private static Task<long> GetFileLengthAsync(string path, CancellationToken cancellationToken)
        => Task.Run(() => new FileInfo(path).Length, cancellationToken);

    private static Task<byte[]?> LoadThumbnailUrlBytesAsync(
        string thumbnailUrl,
        CancellationToken cancellationToken)
    {
        return Task.Run(async () =>
        {
            const string base64Prefix = "data:image/png;base64,";
            if (thumbnailUrl.StartsWith(base64Prefix, StringComparison.OrdinalIgnoreCase))
                return Convert.FromBase64String(thumbnailUrl[base64Prefix.Length..]);

            if (File.Exists(thumbnailUrl))
                return await File.ReadAllBytesAsync(thumbnailUrl, cancellationToken);

            return null;
        }, cancellationToken);
    }

    private static async Task<byte[]?> TryReadSmallOriginalImageAsync(
        FileSystemEntry entry,
        CancellationToken cancellationToken)
    {
        const long maxInlinePreviewBytes = 8L * 1024 * 1024;
        if (entry.IconKey != "file-image")
            return null;

        try
        {
            var canRead = await Task.Run(() =>
            {
                if (!File.Exists(entry.FullPath))
                    return false;

                var info = new FileInfo(entry.FullPath);
                return info.Length <= maxInlinePreviewBytes;
            }, cancellationToken);

            if (!canRead) return null;

            return await File.ReadAllBytesAsync(entry.FullPath, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private void ClearPanel()
    {
        CancelQueuedPanelUpdate();
        _currentFilePath = null;
        _selectedSystemTags.Clear();
        InfoPath.Text = "—";
        InfoSize.Text = "—";
        InfoType.Text = "—";
        InfoModified.Text = "—";
        InfoCreated.Text = "—";
        InfoAccessed.Text = "—";
        UpdateExifTab(null);
        ResetPreviewContent("选择文件以预览");
        CustomTagsPanel.Children.Clear();
        UpdateSystemTagCheckmarks();
    }

    // ── Tabs ──

    private void SwitchToBasicTab(object? sender, RoutedEventArgs e)
    {
        BasicTabContent.IsVisible = true;
        ExifTabContent.IsVisible = false;
        TabBasicBtn.Classes.Remove("info-tab");
        TabBasicBtn.Classes.Add("info-tab-active");
        TabExifBtn.Classes.Remove("info-tab-active");
        TabExifBtn.Classes.Add("info-tab");
    }

    private void SwitchToExifTab(object? sender, RoutedEventArgs e)
    {
        BasicTabContent.IsVisible = false;
        ExifTabContent.IsVisible = true;
        TabExifBtn.Classes.Remove("info-tab");
        TabExifBtn.Classes.Add("info-tab-active");
        TabBasicBtn.Classes.Remove("info-tab-active");
        TabBasicBtn.Classes.Add("info-tab");
    }

    // ── EXIF ──

    private void UpdateExifTab(ImageMetadata? image)
    {
        ExifFieldsPanel.Children.Clear();
        if (image == null)
        {
            ExifNoDataLabel.IsVisible = true;
            return;
        }

        ExifNoDataLabel.IsVisible = false;
        var fields = new List<(string Label, string Value)>
        {
            ("尺寸", image.PixelWidth > 0 && image.PixelHeight > 0 ? $"{image.PixelWidth} × {image.PixelHeight}" : ""),
            ("色彩空间", image.ColorSpace),
            ("制造商", image.CameraMake),
            ("相机", image.CameraModel),
            ("镜头", image.LensModel),
            ("焦距", image.FocalLength),
            ("光圈", image.Aperture),
            ("曝光", image.ExposureTime),
            ("ISO", image.IsoSpeed),
            ("白平衡", image.WhiteBalance),
            ("闪光灯", image.Flash),
            ("曝光程序", image.ExposureProgram),
            ("测光模式", image.MeteringMode),
            ("拍摄日期", image.PhotoTakenDate?.ToString("yyyy-MM-dd HH:mm:ss") ?? ""),
        };

        if (image.Latitude != 0 || image.Longitude != 0)
        {
            fields.Add(("纬度", image.Latitude.ToString("F6")));
            fields.Add(("经度", image.Longitude.ToString("F6")));
            if (!string.IsNullOrEmpty(image.Altitude))
                fields.Add(("海拔", image.Altitude));
        }

        foreach (var (label, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            ExifFieldsPanel.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("72,*"),
                Children =
                {
                    new TextBlock
                    {
                        Text = label, FontSize = 11,
                        Foreground = Brush.Parse("#8E8E93"),
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    },
                    new TextBlock
                    {
                        [Grid.ColumnProperty] = 1,
                        Text = value, FontSize = 12,
                        TextWrapping = TextWrapping.Wrap,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center
                    }
                }
            });
        }
    }

    // ── Tags (Apple Finder style) ──

    private void BuildSystemTags()
    {
        foreach (var (name, color) in FinderTagColors)
        {
            var tagBtn = new Button
            {
                Width = 24, Height = 24,
                Background = Brush.Parse(color),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(0),
                Tag = name,
                BorderThickness = new Thickness(0),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
            };
            tagBtn.Classes.Add("system-tag-dot");
            ToolTip.SetTip(tagBtn, name);

            // Checkmark overlay (hidden by default)
            var checkmark = new TextBlock
            {
                Text = "✓",
                FontSize = 13,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                IsVisible = false,
                Tag = "check"
            };
            tagBtn.Content = checkmark;
            tagBtn.Click += OnSystemTagClick;
            SystemTagsPanel.Children.Add(tagBtn);
        }
    }

    private void UpdateSystemTagSelection(IReadOnlyList<string> selectedTags)
    {
        _selectedSystemTags.Clear();
        foreach (var tag in selectedTags)
            _selectedSystemTags.Add(tag);
        UpdateSystemTagCheckmarks();
    }

    private void UpdateSystemTagCheckmarks()
    {
        foreach (var child in SystemTagsPanel.Children)
        {
            if (child is Button btn && btn.Tag is string name && btn.Content is TextBlock check)
            {
                check.IsVisible = _selectedSystemTags.Contains(name);
            }
        }
    }

    private void UpdateTagsFromMetadata(string filePath, IReadOnlyList<string> finderTags)
    {
        lock (_pendingFinderTagsLock)
        {
            if (_pendingFinderTags.TryGetValue(filePath, out var pendingTags))
                finderTags = pendingTags;
        }

        CustomTagsPanel.Children.Clear();

        // Update system tag selection based on Finder tags
        UpdateSystemTagSelection(finderTags);

        // Show custom tags (non-color tags from Finder)
        var colorNames = new HashSet<string>(FinderTagColors.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var tag in finderTags)
        {
            if (colorNames.Contains(tag)) continue;
            CustomTagsPanel.Children.Add(CreateTagChip(tag, filePath));
        }
    }

    private static IBrush ResolveBrush(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key, out var value) == true && value is IBrush brush)
            return brush;
        return Brush.Parse(fallback);
    }

    private Border CreateTagChip(string tag, string filePath)
    {
        var textTertiary = ResolveBrush("ColorTextTertiary", "#7D8491");
        var surfaceSecondary = ResolveBrush("ColorSurfaceSecondary", "#F0F1F3");
        var textSecondary = ResolveBrush("ColorTextSecondary", "#5B6270");
        var bgHover = ResolveBrush("ColorBgHover", "#0B0F172A");

        var removeBtn = new Button
        {
            Content = "×",
            Width = 14, Height = 14,
            Background = Brushes.Transparent,
            FontSize = 10,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(7),
            Foreground = textTertiary,
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        removeBtn.Classes.Add("ghost");

        var chip = new Border
        {
            Background = surfaceSecondary,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 3, 4, 3),
            Margin = new Thickness(0, 0, 4, 4),
            Child = new StackPanel
            {
                Orientation = global::Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = tag, FontSize = 11.5,
                        VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center,
                        Foreground = textSecondary
                    },
                    removeBtn
                }
            }
        };

        chip.PointerEntered += (_, _) => chip.Background = bgHover;
        chip.PointerExited += (_, _) => chip.Background = surfaceSecondary;

        removeBtn.Click += (_, _) =>
        {
            CustomTagsPanel.Children.Remove(chip);
            QueueFinderTagsWrite(filePath, GetDisplayedFinderTags());
        };

        return chip;
    }

    private void OnSystemTagClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string tagName || ViewModel == null) return;
        if (ViewModel.SelectedEntries.Count != 1) return;
        var filePath = ViewModel.SelectedEntries[0].FullPath;

        // Toggle locally first for instant UI feedback
        if (_selectedSystemTags.Contains(tagName))
            _selectedSystemTags.Remove(tagName);
        else
            _selectedSystemTags.Add(tagName);

        UpdateSystemTagCheckmarks();

        QueueFinderTagsWrite(filePath, GetDisplayedFinderTags());
    }

    private void OnTagInputKeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key == global::Avalonia.Input.Key.Enter)
            AddTag();
    }

    private void AddTag()
    {
        var text = TagInput.Text?.Trim();
        if (string.IsNullOrEmpty(text) || ViewModel == null) return;
        if (ViewModel.SelectedEntries.Count != 1) return;

        var filePath = ViewModel.SelectedEntries[0].FullPath;
        if (GetDisplayedFinderTags().Contains(text, StringComparer.OrdinalIgnoreCase))
        {
            TagInput.Text = "";
            return;
        }

        TagInput.Text = "";

        // Add to UI immediately
        CustomTagsPanel.Children.Add(CreateTagChip(text, filePath));
        QueueFinderTagsWrite(filePath, GetDisplayedFinderTags());
    }

    // ── Finder Tag Sync (via xattr) ──

    private List<string> GetDisplayedFinderTags()
    {
        var tags = new List<string>(_selectedSystemTags);
        foreach (var child in CustomTagsPanel.Children)
        {
            if (child is Border { Child: StackPanel panel }
                && panel.Children.FirstOrDefault() is TextBlock { Text: { } text }
                && !string.IsNullOrWhiteSpace(text))
            {
                tags.Add(text);
            }
        }

        return NormalizeFinderTags(tags);
    }

    private void QueueFinderTagsWrite(string filePath, IReadOnlyList<string> desiredTags)
    {
        var tags = NormalizeFinderTags(desiredTags);
        lock (_pendingFinderTagsLock)
            _pendingFinderTags[filePath] = tags;

        var generation = ++_tagWriteGeneration;
        _ = WriteFinderTagsAsync(filePath, tags, generation);
    }

    private async Task WriteFinderTagsAsync(string filePath, List<string> desiredTags, int generation)
    {
        var succeeded = await Task.Run(() => SetFinderTags(filePath, desiredTags));
        var persistedTags = succeeded
            ? await WaitForFinderTagsAsync(filePath, desiredTags)
            : await Task.Run(() => GetFinderTags(filePath));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _tagWriteGeneration)
                return;

            if (!string.Equals(_currentFilePath, filePath, StringComparison.Ordinal))
            {
                lock (_pendingFinderTagsLock)
                    _pendingFinderTags.Remove(filePath);
                return;
            }

            var showPersistedTags = succeeded && FinderTagsEqual(persistedTags, desiredTags);
            lock (_pendingFinderTagsLock)
            {
                if (showPersistedTags || !succeeded)
                    _pendingFinderTags.Remove(filePath);
                else
                    _pendingFinderTags[filePath] = desiredTags;
            }

            UpdateTagsFromMetadata(filePath, showPersistedTags ? persistedTags : desiredTags);
        });
    }

    private static async Task<List<string>> WaitForFinderTagsAsync(string filePath, IReadOnlyList<string> expectedTags)
    {
        var delays = new[] { 120, 350, 800 };
        List<string> tags = [];
        foreach (var delay in delays)
        {
            tags = await Task.Run(() => GetFinderTags(filePath));
            if (FinderTagsEqual(tags, expectedTags))
                return tags;

            await Task.Delay(delay);
        }

        return await Task.Run(() => GetFinderTags(filePath));
    }

    private static List<string> GetFinderTags(string filePath)
    {
        try
        {
            var xattrTags = GetFinderTagsFromXattr(filePath);
            if (xattrTags.Count > 0)
                return xattrTags;

            var output = RunCommand("mdls", "-name", "kMDItemUserTags", filePath);
            return ParseFinderTagsFromMdls(output);
        }
        catch { }
        return [];
    }

    private static List<string> GetFinderTagsFromXattr(string filePath)
    {
        var hex = RunCommand("xattr", "-px", FinderTagsAttribute, filePath);
        return ParseFinderTagsFromBinaryPlistHex(hex);
    }

    private static List<string> ParseFinderTagsFromBinaryPlistHex(string hex)
    {
        hex = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return [];

        var tempBase = Path.Combine(Path.GetTempPath(), $"macexplorer-read-tags-{Guid.NewGuid():N}");
        var binaryPath = tempBase + ".bin";
        var xmlPath = tempBase + ".plist";

        try
        {
            File.WriteAllBytes(binaryPath, Convert.FromHexString(hex));
            RunCommand("plutil", "-convert", "xml1", "-o", xmlPath, binaryPath);
            if (!File.Exists(xmlPath)) return [];

            var document = XDocument.Load(xmlPath);
            return document.Descendants("string")
                .Select(node => NormalizeFinderTagForDisplay(node.Value))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            TryDeleteFile(binaryPath);
            TryDeleteFile(xmlPath);
        }
    }

    private bool SetFinderTags(string filePath, List<string> tags)
    {
        try
        {
            SuppressDirectoryRefreshForMetadataWrite(filePath);
            tags = NormalizeFinderTags(tags);

            if (SetFinderTagsViaResourceApi(filePath, tags))
            {
                RunCommand("xattr", "-d", LegacyFinderTagsAttribute, filePath);
                NotifyFinderToRefresh(filePath);
                return true;
            }

            if (tags.Count == 0)
            {
                RunCommand("xattr", "-d", FinderTagsAttribute, filePath);
                RunCommand("xattr", "-d", LegacyFinderTagsAttribute, filePath);
                return true;
            }
            else
            {
                var hex = CreateFinderTagsBinaryPlistHex(tags);
                if (string.IsNullOrEmpty(hex)) return false;

                if (!RunCommandSucceeded("xattr", "-wx", FinderTagsAttribute, hex, filePath))
                    return false;
                RunCommand("xattr", "-d", LegacyFinderTagsAttribute, filePath);
            }
            NotifyFinderToRefresh(filePath);
            return true;
        }
        catch { return false; }
    }

    private static bool SetFinderTagsViaResourceApi(string filePath, IReadOnlyList<string> tags)
    {
        var script = $$"""
ObjC.import('Foundation');
const path = {{JsonSerializer.Serialize(filePath)}};
const tags = {{JsonSerializer.Serialize(tags)}};
const url = $.NSURL.fileURLWithPath(path);
const nsTags = $.NSArray.arrayWithArray(tags);
const ok = url.setResourceValueForKeyError(nsTags, $.NSURLTagNamesKey, null);
if (!ok) {
  throw new Error('setResourceValueForKeyError failed');
}
""";

        return RunCommandSucceeded("osascript", "-l", "JavaScript", "-e", script);
    }

    private void SuppressDirectoryRefreshForMetadataWrite(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
            _directoryChangeNotifier?.SuppressRefresh([directory], TimeSpan.FromSeconds(3));
    }

    private static void NotifyFinderToRefresh(string filePath)
    {
        RunCommand("osascript", "-e", $"tell application \"Finder\" to update POSIX file {ToAppleScriptStringLiteral(filePath)}");
    }

    private static string ToAppleScriptStringLiteral(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static List<string> ParseFinderTagsFromMdls(string output)
    {
        var idx = output.IndexOf('=');
        if (idx < 0) return [];

        var value = output[(idx + 1)..].Trim();
        if (value is "(null)" or "null") return [];
        if (value.StartsWith('(') && value.EndsWith(')'))
            value = value[1..^1];

        return SplitMdlsArrayItems(value)
            .Select(NormalizeFinderTagForDisplay)
            .Where(t => !string.IsNullOrEmpty(t) && !string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitMdlsArrayItems(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var previous = '\0';

        foreach (var c in value)
        {
            if (c == '"' && previous != '\\')
                inQuote = !inQuote;

            if (c == ',' && !inQuote)
            {
                AddMdlsArrayItem(items, current);
            }
            else
            {
                current.Append(c);
            }

            previous = c;
        }

        AddMdlsArrayItem(items, current);
        return items;
    }

    private static void AddMdlsArrayItem(List<string> items, StringBuilder current)
    {
        var item = current.ToString().Trim().TrimEnd(',').Trim().Trim('"');
        current.Clear();
        if (!string.IsNullOrWhiteSpace(item))
            items.Add(item);
    }

    private static string NormalizeFinderTagForDisplay(string tag)
    {
        tag = tag.Trim().Replace("\\012", "\n", StringComparison.Ordinal);
        var suffixStart = tag.LastIndexOf('\n');
        if (suffixStart >= 0 && int.TryParse(tag[(suffixStart + 1)..], out _))
            tag = tag[..suffixStart];

        return tag.Trim();
    }

    private static List<string> NormalizeFinderTags(IEnumerable<string> tags)
    {
        return tags
            .Select(NormalizeFinderTagForDisplay)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool FinderTagsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var normalizedLeft = NormalizeFinderTags(left);
        var normalizedRight = NormalizeFinderTags(right);
        return normalizedLeft.Count == normalizedRight.Count
            && normalizedLeft.All(tag => normalizedRight.Contains(tag, StringComparer.OrdinalIgnoreCase));
    }

    private static string CreateFinderTagsBinaryPlistHex(IEnumerable<string> tags)
    {
        var uniqueTags = tags
            .Select(NormalizeFinderTagForDisplay)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ToFinderTagStorageValue)
            .ToArray();

        if (uniqueTags.Length == 0) return string.Empty;

        var xml = BuildFinderTagsPlistXml(uniqueTags);
        var tempBase = Path.Combine(Path.GetTempPath(), $"macexplorer-tags-{Guid.NewGuid():N}");
        var xmlPath = tempBase + ".plist";
        var binaryPath = tempBase + ".bin";

        try
        {
            File.WriteAllText(xmlPath, xml, Encoding.UTF8);
            RunCommand("plutil", "-convert", "binary1", "-o", binaryPath, xmlPath);
            if (!File.Exists(binaryPath)) return string.Empty;

            return Convert.ToHexString(File.ReadAllBytes(binaryPath)).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
        finally
        {
            TryDeleteFile(xmlPath);
            TryDeleteFile(binaryPath);
        }
    }

    private static string ToFinderTagStorageValue(string tag)
    {
        return FinderTagColorIndexes.TryGetValue(tag, out var colorIndex)
            ? $"{tag}\n{colorIndex}"
            : tag;
    }

    private static string BuildFinderTagsPlistXml(IEnumerable<string> tags)
    {
        var builder = new StringBuilder();
        builder.AppendLine("""<?xml version="1.0" encoding="UTF-8"?>""");
        builder.AppendLine("""<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">""");
        builder.AppendLine("""<plist version="1.0">""");
        builder.AppendLine("<array>");
        foreach (var tag in tags)
            builder.Append("  <string>").Append(SecurityElement.Escape(tag)).AppendLine("</string>");
        builder.AppendLine("</array>");
        builder.AppendLine("</plist>");
        return builder.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static string RunCommand(string command, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Trim();
        }
        catch { return string.Empty; }
    }

    private static bool RunCommandSucceeded(string command, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null) return false;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Quick Actions ──

    private async void CopyPath(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedEntries.Count != 1) return;
        var clipboardService = App.Services.GetService<IClipboardService>();
        if (clipboardService != null)
            await clipboardService.CopyTextAsync(ViewModel.SelectedEntries[0].FullPath);
    }

    private void OpenInTerminal(object? sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedEntries.Count != 1) return;
        var entry = ViewModel.SelectedEntries[0];
        var dir = entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath);
        if (dir == null) return;
        try
        {
            Process.Start(new ProcessStartInfo("open", $"-a Terminal \"{dir}\"") { UseShellExecute = true });
        }
        catch { }
    }

    private void CompressFile(object? sender, RoutedEventArgs e)
    {
        ViewModel?.ShowCompressDialog();
    }

    private async void CopyImageText(object? sender, RoutedEventArgs e)
    {
        if (_imageAnalysisService == null || _ocrPreviewBytes == null || _ocrPreviewBytes.Length == 0)
            return;

        _ocrCts?.Cancel();
        _ocrCts?.Dispose();
        _ocrCts = new CancellationTokenSource();
        var token = _ocrCts.Token;
        var generation = _previewGeneration;
        var tempPath = Path.Combine(Path.GetTempPath(), $"macexplorer-ocr-{Guid.NewGuid():N}.png");
        CopyImageTextBtn.IsEnabled = false;
        CopyImageTextBtn.Content = "正在识别…";

        try
        {
            await File.WriteAllBytesAsync(tempPath, _ocrPreviewBytes, token);
            var result = await _imageAnalysisService.AnalyzeImageAsync(tempPath, token);
            if (generation != _previewGeneration || token.IsCancellationRequested) return;

            var text = string.Join(Environment.NewLine,
                result.RecognizedTexts
                    .Select(item => item.Text.Trim())
                    .Where(item => !string.IsNullOrWhiteSpace(item)));
            if (string.IsNullOrWhiteSpace(text))
            {
                CopyImageTextBtn.Content = "未识别到文字";
                return;
            }

            if (_clipboardService == null)
            {
                CopyImageTextBtn.Content = "无法访问剪贴板";
                return;
            }

            await _clipboardService.CopyTextAsync(text);
            CopyImageTextBtn.Content = $"已复制 {result.RecognizedTexts.Count} 行";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            if (generation == _previewGeneration)
                CopyImageTextBtn.Content = "识别失败";
        }
        finally
        {
            try { File.Delete(tempPath); }
            catch { }

            if (generation == _previewGeneration && !token.IsCancellationRequested)
            {
                await Task.Delay(1600);
                if (generation == _previewGeneration)
                {
                    CopyImageTextBtn.IsEnabled = true;
                    CopyImageTextBtn.Content = "复制图片文字";
                }
            }
        }
    }

    private void TogglePreviewExpanded(object? sender, RoutedEventArgs e)
    {
        SetPreviewExpanded(!_isPreviewExpanded, notify: true);
    }

    private void SetPreviewExpanded(bool expanded, bool notify)
    {
        _isPreviewExpanded = expanded;
        DetailsPanel.IsVisible = !expanded;
        PanelTitle.Text = _isPreviewExpanded ? "文件预览" : "信息";
        ExpandPreviewBtn.SetValue(ToolTip.TipProperty, _isPreviewExpanded ? "收起预览" : "展开预览");
        ExpandPreviewIcon.Data = Geometry.Parse(_isPreviewExpanded
            ? "M4 8.5H8.5V4H10V10H4V8.5ZM14 4H15.5V8.5H20V10H14V4ZM4 14H10V20H8.5V15.5H4V14ZM14 14H20V15.5H15.5V20H14V14Z"
            : "M4 4H10V5.5H5.5V10H4V4ZM14 4H20V10H18.5V5.5H14V4ZM4 14H5.5V18.5H10V20H4V14ZM18.5 14H20V20H14V18.5H18.5V14Z");
        PreviewImageArea.MinHeight = expanded ? Math.Max(360, Bounds.Height - 76) : 160;
        if (notify)
            PreviewExpandedChanged?.Invoke(this, _isPreviewExpanded);
    }

    public void CompletePreviewTransition(bool expanded)
    {
        if (_isPreviewExpanded != expanded) return;
        DetailsPanel.IsVisible = !expanded;
    }

    public void SetExpandedChrome(bool expanded)
    {
        InfoPanelRoot.CornerRadius = expanded
            ? new CornerRadius(14, 0, 0, 0)
            : new CornerRadius(0);
    }

    // ── Close ──

    private void ClosePanel(object? sender, RoutedEventArgs e)
    {
        if (_isPreviewExpanded)
            SetPreviewExpanded(false, notify: true);
        if (ViewModel != null)
            ViewModel.IsInfoPanelVisible = false;
        ClearPanel();
    }
}
