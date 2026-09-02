using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using Microsoft.Extensions.DependencyInjection;
using SharpCompress.Common;

namespace MacExplorer.Views;

/// <summary>
/// In-window Finder-style preview. It owns a small navigation stack instead of
/// changing the main FileListView navigation, so previewing a folder/archive
/// never changes the user's current tab or selection.
/// </summary>
public partial class SuperPreviewView : UserControl
{
    private const long MaxTextPreviewBytes = 4L * 1024 * 1024;
    private const int MaxListThumbnailCandidates = 48;
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".md", ".markdown", ".log", ".csv", ".tsv", ".json", ".jsonl", ".xml",
        ".yaml", ".yml", ".ini", ".conf", ".config", ".toml", ".properties", ".env", ".editorconfig",
        ".gitignore", ".gitattributes", ".dockerignore", ".npmignore", ".nfo", ".readme", ".rst", ".tex",
        ".adoc", ".asciidoc", ".org", ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".cue", ".sh", ".zsh",
        ".bash", ".fish", ".ps1", ".cs", ".fs", ".vb", ".js", ".jsx", ".ts", ".tsx",
        ".html", ".htm", ".css", ".scss", ".less", ".py", ".rb", ".php", ".java", ".kt",
        ".kts", ".swift", ".m", ".mm", ".h", ".hpp", ".c", ".cpp", ".go", ".rs", ".sql",
        ".graphql", ".gql", ".ipynb", ".lua", ".r", ".rmd", ".scala", ".sc", ".clj", ".cljs",
        ".groovy", ".gradle", ".dart", ".ex", ".exs", ".erl", ".hrl", ".pl", ".pm", ".t", ".vim",
        ".asm", ".s", ".f", ".f90", ".pas", ".d", ".zig", ".sol", ".vue", ".svelte", ".astro",
        ".make", ".mk", ".cmake", ".dockerfile"
    };
    private static readonly HashSet<string> TextFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "README", "README.md", "README.txt", "CHANGELOG", "CHANGES", "NEWS", "LICENSE", "COPYING",
        "NOTICE", "AUTHORS", "CONTRIBUTORS", "Makefile", "GNUmakefile", "Dockerfile", "Containerfile",
        "Gemfile", "Rakefile", "Podfile", "Brewfile", "Procfile", "Vagrantfile"
    };

    private readonly ObservableCollection<FileSystemEntry> _entries = [];
    private readonly List<PreviewLocation> _history = [];
    private readonly List<string> _temporaryFiles = [];
    private readonly IFileService? _fileService;
    private readonly IArchiveService? _archiveService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly IQuickLookService? _quickLookService;
    private readonly ISettingsService? _settingsService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _previewCts;
    private Bitmap? _previewBitmap;
    private PreviewLocation? _location;
    private FileSystemEntry? _selectedEntry;
    private bool _ignoreSelection;
    private int _operationVersion;

    public event EventHandler? RequestClose;

    /// <summary>Set by MainWindow so encrypted archives can use the existing password dialog.</summary>
    public Func<Task<string?>>? PasswordPrompt { get; set; }

    public SuperPreviewView()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _entries;
        _fileService = App.Services.GetService<IFileService>();
        _archiveService = App.Services.GetService<IArchiveService>();
        _thumbnailService = App.Services.GetService<IThumbnailService>();
        _quickLookService = App.Services.GetService<IQuickLookService>();
        _settingsService = App.Services.GetService<ISettingsService>();
    }

    public async Task OpenAsync(FileSystemEntry entry)
    {
        CloseResources();
        _history.Clear();
        _entries.Clear();
        _selectedEntry = null;
        IsVisible = true;
        Focus();

        var location = await BuildInitialLocationAsync(entry);
        if (location == null)
        {
            ShowPlaceholder("此项目无法在超级预览中显示");
            return;
        }

        var preferred = GetPreferredEntry(entry, location);
        // A standalone file preview is not a request to browse its parent
        // directory. Keep the left-side context focused on that file; folder
        // and archive previews still expose their real immediate contents.
        var isolatePreferredEntry = preferred != null
                                   && !location.IsArchive
                                   && !entry.IsDirectory
                                   && _archiveService?.IsArchiveFile(entry.FullPath) != true;
        await LoadLocationAsync(location, preferred, isolatePreferredEntry);
    }

    public void Close()
    {
        if (!IsVisible && _location == null)
            return;

        IsVisible = false;
        CloseResources();
        _history.Clear();
        _entries.Clear();
        _location = null;
        _selectedEntry = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async Task<PreviewLocation?> BuildInitialLocationAsync(FileSystemEntry entry)
    {
        var fullPath = entry.FullPath;
        if (ArchivePathHelper.IsArchivePath(fullPath))
        {
            var (archivePath, internalPath) = ArchivePathHelper.Parse(fullPath);
            if (entry.IsDirectory)
                return PreviewLocation.Archive(archivePath, internalPath);

            var parent = Path.GetDirectoryName(internalPath.TrimEnd('/'))?.Replace('\\', '/') ?? string.Empty;
            return PreviewLocation.Archive(archivePath, parent);
        }

        if (entry.IsDirectory)
            return PreviewLocation.Directory(fullPath);

        if (_archiveService?.IsArchiveFile(fullPath) == true)
            return PreviewLocation.Archive(fullPath, string.Empty);

        var parentPath = Path.GetDirectoryName(fullPath);
        return !string.IsNullOrWhiteSpace(parentPath) && Directory.Exists(parentPath)
            ? PreviewLocation.Directory(parentPath)
            : null;
    }

    private static FileSystemEntry? GetPreferredEntry(FileSystemEntry original, PreviewLocation location)
    {
        if (original.IsDirectory && !ArchivePathHelper.IsArchivePath(original.FullPath))
            return null;
        if (location.IsArchive && ArchivePathHelper.IsArchivePath(original.FullPath))
            return original;
        if (!location.IsArchive && !original.IsDirectory && !string.IsNullOrWhiteSpace(location.DirectoryPath))
            return original;
        return null;
    }

    private async Task LoadLocationAsync(
        PreviewLocation location,
        FileSystemEntry? preferred,
        bool isolatePreferredEntry = false)
    {
        CancelLoad();
        var tokenSource = new CancellationTokenSource();
        _loadCts = tokenSource;
        var token = tokenSource.Token;
        var version = ++_operationVersion;
        _location = location;
        _selectedEntry = null;
        UpdateLocationChrome();
        ShowPlaceholder(location.IsArchive ? "正在读取压缩包目录…" : "正在读取文件夹内容…");

        try
        {
            var entries = location.IsArchive
                ? await ReadArchiveEntriesAsync(location, token)
                : await ReadDirectoryEntriesAsync(location.DirectoryPath!, token);
            token.ThrowIfCancellationRequested();
            if (version != _operationVersion) return;

            _ignoreSelection = true;
            try
            {
                _entries.Clear();
                IEnumerable<FileSystemEntry> visibleEntries = entries
                    .Where(ShouldShowEntry)
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase);
                if (isolatePreferredEntry && preferred != null)
                {
                    visibleEntries = visibleEntries.Where(entry =>
                        string.Equals(entry.FullPath, preferred.FullPath, StringComparison.Ordinal));
                }

                foreach (var entry in visibleEntries)
                    _entries.Add(entry);

                var match = preferred == null
                    ? null
                    : _entries.FirstOrDefault(e => string.Equals(e.FullPath, preferred.FullPath, StringComparison.Ordinal));
                ItemsList.SelectedItem = match;
                _selectedEntry = match;
            }
            finally
            {
                _ignoreSelection = false;
            }

            ItemCountText.Text = _entries.Count == 0 ? "空" : $"{_entries.Count} 项";
            if (_selectedEntry != null)
                await ShowEntryPreviewAsync(_selectedEntry, token);
            else
                ShowLocationSummary();

            // The normal file list owns an async image loader, but this preview
            // has its own list. Populate thumbnails for local folders as well as
            // archives so PDFs, text documents and images do not fall back to a
            // generic file-type icon until the user opens them.
            if (_entries.Count > 0)
                _ = PopulateListThumbnailsAsync(_entries.ToArray(), location, token);

            // Keep keyboard navigation in the preview list after a folder/archive
            // transition; the overlay itself remains the command surface for
            // Escape/Space and receives bubbled key events from the ListBox.
            ItemsList.Focus();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _operationVersion)
            {
                ItemCountText.Text = "读取失败";
                ShowPlaceholder($"无法读取内容\n{ex.Message}");
            }
        }
    }

    private async Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryEntriesAsync(string path, CancellationToken token)
        => _fileService == null ? [] : await _fileService.GetDirectoryContentsAsync(path, token);

    private async Task<IReadOnlyList<FileSystemEntry>> ReadArchiveEntriesAsync(PreviewLocation location, CancellationToken token)
    {
        if (_archiveService == null) return [];
        try
        {
            return await _archiveService.GetArchiveContentsAsync(location.ArchivePath!, location.InternalPath);
        }
        catch (CryptographicException) when (PasswordPrompt != null)
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return [];
            return await _archiveService.GetArchiveContentsAsync(location.ArchivePath!, location.InternalPath, password);
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) && PasswordPrompt != null)
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return [];
            return await _archiveService.GetArchiveContentsAsync(location.ArchivePath!, location.InternalPath, password);
        }
    }

    private bool ShouldShowEntry(FileSystemEntry entry)
    {
        if (_settingsService?.Get("HideSystemFiles", true) == true
            && (entry.Name.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
                || entry.Name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_settingsService?.Get("HideDotFiles", true) == true
            && !entry.IsDirectory && entry.Name.StartsWith('.'))
            return false;
        if (_settingsService?.Get("HideDotFolders", true) == true
            && entry.IsDirectory && entry.Name.StartsWith('.'))
            return false;
        return true;
    }

    private async void OnItemSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_ignoreSelection || ItemsList.SelectedItem is not FileSystemEntry entry)
            return;
        _selectedEntry = entry;
        await ShowEntryPreviewAsync(entry);
    }

    private async void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ItemsList.SelectedItem is FileSystemEntry entry)
            await EnterEntryAsync(entry);
        e.Handled = true;
    }

    private async Task EnterEntryAsync(FileSystemEntry entry)
    {
        if (_location == null) return;

        PreviewLocation? next = null;
        if (entry.IsDirectory)
        {
            if (_location.IsArchive)
            {
                var (_, internalPath) = ArchivePathHelper.Parse(entry.FullPath);
                next = PreviewLocation.Archive(_location.ArchivePath!, internalPath);
            }
            else
            {
                next = PreviewLocation.Directory(entry.FullPath);
            }
        }
        else if (_archiveService?.IsArchiveFile(entry.Name) == true)
        {
            var extracted = await ExtractArchiveEntryAsync(entry);
            if (!string.IsNullOrWhiteSpace(extracted))
                next = PreviewLocation.Archive(extracted, string.Empty, Path.GetFileName(entry.Name));
        }
        else if (!_location.IsArchive && _archiveService?.IsArchiveFile(entry.FullPath) == true)
        {
            next = PreviewLocation.Archive(entry.FullPath, string.Empty);
        }

        if (next == null) return;
        _history.Add(_location);
        await LoadLocationAsync(next, null);
    }

    private async Task<string?> ExtractArchiveEntryAsync(FileSystemEntry entry)
    {
        if (_archiveService == null) return null;
        try
        {
            if (ArchivePathHelper.IsArchivePath(entry.FullPath))
            {
                var (archivePath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
                var extracted = await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey);
                _temporaryFiles.Add(extracted);
                return extracted;
            }

            return entry.FullPath;
        }
        catch (CryptographicException) when (PasswordPrompt != null && ArchivePathHelper.IsArchivePath(entry.FullPath))
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return null;
            var (archivePath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
            var extracted = await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password);
            _temporaryFiles.Add(extracted);
            return extracted;
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                                 && PasswordPrompt != null && ArchivePathHelper.IsArchivePath(entry.FullPath))
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return null;
            var (archivePath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
            var extracted = await _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password);
            _temporaryFiles.Add(extracted);
            return extracted;
        }
        catch
        {
            return null;
        }
    }

    private async Task ShowEntryPreviewAsync(FileSystemEntry entry, CancellationToken? inheritedToken = null)
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(inheritedToken ?? CancellationToken.None);
        var token = _previewCts.Token;
        var version = ++_operationVersion;
        QuickLookButton.IsVisible = false;
        PreviewMetaText.Text = entry.FullPath;

        if (entry.IsDirectory)
        {
            ShowFolderSummary(entry.Name, "文件夹", entry.FullPath);
            return;
        }

        ShowPlaceholder("正在生成预览…");
        var previewPath = await ResolvePreviewPathAsync(entry, token);
        if (string.IsNullOrWhiteSpace(previewPath) || version != _operationVersion)
        {
            ShowPlaceholder("无法读取此文件");
            return;
        }

        if (IsTextFile(entry))
        {
            await ShowTextPreviewAsync(previewPath, entry, token, version);
            return;
        }

        try
        {
            var bytes = _thumbnailService == null
                ? null
                : await _thumbnailService.GetThumbnailAsync(previewPath, 1800, token);
            if (bytes != null && bytes.Length > 0)
            {
                using var stream = new MemoryStream(bytes, writable: false);
                SetPreviewBitmap(new Bitmap(stream));
                PreviewImage.IsVisible = true;
                PreviewPlaceholder.IsVisible = false;
                PreviewTextScroll.IsVisible = false;
                FolderSummary.IsVisible = false;
                PreviewMetaText.Text = $"{entry.KindText} · {entry.FormattedSize}";
                QuickLookButton.IsVisible = _quickLookService != null;
                return;
            }

            ShowPlaceholder(IsVideoFile(entry)
                ? "此视频暂时无法生成缩略图，可用系统 Quick Look 查看"
                : "系统无法为此文件生成预览");
            QuickLookButton.IsVisible = _quickLookService != null;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ShowPlaceholder("预览生成失败");
            QuickLookButton.IsVisible = _quickLookService != null;
        }
    }

    private async Task<string?> ResolvePreviewPathAsync(FileSystemEntry entry, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!ArchivePathHelper.IsArchivePath(entry.FullPath))
            return File.Exists(entry.FullPath) ? entry.FullPath : null;

        var (archivePath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
        try
        {
            var extracted = await _archiveService!.ExtractEntryToTempAsync(archivePath, entryKey);
            _temporaryFiles.Add(extracted);
            return extracted;
        }
        catch (CryptographicException) when (PasswordPrompt != null)
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return null;
            var extracted = await _archiveService!.ExtractEntryToTempAsync(archivePath, entryKey, password);
            _temporaryFiles.Add(extracted);
            return extracted;
        }
        catch (InvalidFormatException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase)
                                                 && PasswordPrompt != null)
        {
            var password = await PasswordPrompt!();
            if (string.IsNullOrEmpty(password)) return null;
            var extracted = await _archiveService!.ExtractEntryToTempAsync(archivePath, entryKey, password);
            _temporaryFiles.Add(extracted);
            return extracted;
        }
        catch
        {
            return null;
        }
    }

    private async Task PopulateListThumbnailsAsync(
        IReadOnlyList<FileSystemEntry> entries,
        PreviewLocation location,
        CancellationToken token)
    {
        if (_thumbnailService == null) return;

        // This is deliberately bounded and sequential: the thumbnail service
        // serializes generator access, and extracting every file from a large
        // archive would make the preview feel unresponsive.
        var candidates = entries
            .Where(entry => !entry.IsDirectory && !entry.IsVirtual)
            .Take(MaxListThumbnailCandidates)
            .ToArray();

        foreach (var entry in candidates)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                if (!Equals(_location, location))
                    return;
                if (!string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
                    continue;
                var bitmap = await LoadListEntryThumbnailAsync(entry, location, token);
                if (bitmap != null)
                    ApplyListThumbnail(entry, bitmap);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // One unsupported/corrupt item must not prevent other archive
                // entries from receiving their previews.
            }
        }
    }

    private async void OnPreviewEntryImageLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Image image || image.DataContext is not FileSystemEntry entry)
            return;

        var location = _location;
        if (location == null || entry.IsDirectory || entry.IsVirtual)
            return;

        try
        {
            var token = _loadCts?.Token ?? CancellationToken.None;
            var bitmap = await LoadListEntryThumbnailAsync(entry, location, token);
            if (bitmap != null && ReferenceEquals(image.DataContext, entry) && Equals(_location, location))
            {
                image.Source = bitmap;
                ApplyListThumbnail(entry, bitmap);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Leave the normal file-type icon in place when a particular entry
            // cannot be extracted or macOS has no generator for it.
        }
    }

    private async Task<Bitmap?> LoadListEntryThumbnailAsync(
        FileSystemEntry entry,
        PreviewLocation location,
        CancellationToken token)
    {
        if (_thumbnailService == null || !Equals(_location, location))
            return null;

        if (string.IsNullOrWhiteSpace(entry.ThumbnailUrl))
        {
            var previewPath = await ResolvePreviewPathAsync(entry, token);
            if (string.IsNullOrWhiteSpace(previewPath))
                return null;

            var thumbnail = await _thumbnailService.GetThumbnailResultAsync(previewPath, 96, token);
            if (thumbnail is not { Bytes.Length: > 0 } || !Equals(_location, location))
                return null;

            entry.ThumbnailUrl = thumbnail.CachePath;
            entry.RaiseIconBindingChanged();
        }

        return string.IsNullOrWhiteSpace(entry.ThumbnailUrl)
            ? null
            : await FileListView.GetEntryBitmapAsync(entry.ThumbnailUrl, token);
    }

    private void ApplyListThumbnail(FileSystemEntry entry, Bitmap bitmap)
    {
        foreach (var image in ItemsList.GetVisualDescendants().OfType<Image>())
        {
            if (image.DataContext is FileSystemEntry current
                && string.Equals(current.FullPath, entry.FullPath, StringComparison.OrdinalIgnoreCase))
                image.Source = bitmap;
        }
    }

    private async Task ShowTextPreviewAsync(string path, FileSystemEntry entry, CancellationToken token, int version)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextPreviewBytes)
            {
                ShowPlaceholder("文本文件过大（仅支持 4 MB 以内）");
                return;
            }

            var bytes = await File.ReadAllBytesAsync(path, token);
            if (version != _operationVersion)
                return;
            PreviewText.Text = DecodeText(bytes);
            PreviewTextScroll.IsVisible = true;
            PreviewText.IsVisible = true;
            PreviewImage.IsVisible = false;
            PreviewPlaceholder.IsVisible = false;
            FolderSummary.IsVisible = false;
            PreviewMetaText.Text = $"{entry.KindText} · {entry.FormattedSize}";
            QuickLookButton.IsVisible = _quickLookService != null;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            ShowPlaceholder("无法读取文本内容");
        }
    }

    private void ShowLocationSummary()
    {
        if (_location == null) return;
        ShowFolderSummary(_location.Title, _location.IsArchive ? "压缩包目录" : "文件夹内容", _location.DisplayPath);
    }

    private void ShowFolderSummary(string title, string kind, string path)
    {
        SetPreviewBitmap(null);
        PreviewImage.IsVisible = false;
        PreviewTextScroll.IsVisible = false;
        PreviewPlaceholder.IsVisible = false;
        FolderSummary.IsVisible = true;
        FolderSummaryTitle.Text = title;
        FolderSummaryDetails.Text = $"{kind} · {_entries.Count} 项\n{path}";
        PreviewMetaText.Text = path;
        QuickLookButton.IsVisible = false;
    }

    private void ShowPlaceholder(string text)
    {
        SetPreviewBitmap(null);
        PreviewImage.IsVisible = false;
        PreviewTextScroll.IsVisible = false;
        PreviewText.IsVisible = false;
        FolderSummary.IsVisible = false;
        PreviewPlaceholder.Text = text;
        PreviewPlaceholder.IsVisible = true;
        PreviewMetaText.Text = string.Empty;
        QuickLookButton.IsVisible = false;
    }

    private void UpdateLocationChrome()
    {
        if (_location == null) return;
        TitleText.Text = _location.Title;
        BreadcrumbText.Text = string.Join("  ›  ", _history.Select(h => h.Title).Append(_location.Title));
        BackButton.IsEnabled = _history.Count > 0;
        ListHintText.Text = _location.IsArchive ? "双击进入目录" : "双击进入";
    }

    private async void GoBack(object? sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            Close();
            return;
        }

        var previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        await LoadLocationAsync(previous, null);
    }

    private async void OpenWithQuickLook(object? sender, RoutedEventArgs e)
    {
        if (_selectedEntry == null || _quickLookService == null) return;
        var path = await ResolvePreviewPathAsync(_selectedEntry, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(path))
            await _quickLookService.PreviewFileAsync(path);
    }

    private void ClosePreview(object? sender, RoutedEventArgs e) => Close();

    private void OnSuperPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && _history.Count > 0)
        {
            e.Handled = true;
            GoBack(sender, new RoutedEventArgs());
        }
        else if (e.Key == Key.Enter && ItemsList.SelectedItem is FileSystemEntry entry)
        {
            e.Handled = true;
            _ = EnterEntryAsync(entry);
        }
    }

    private static bool IsTextFile(FileSystemEntry entry)
        => TextExtensions.Contains(entry.Extension)
           || TextExtensions.Contains(Path.GetExtension(entry.Name))
           || TextFileNames.Contains(entry.Name);

    private static bool IsVideoFile(FileSystemEntry entry)
        => entry.Extension is ".mp4" or ".mov" or ".m4v" or ".avi" or ".mkv" or ".webm"
            or ".wmv" or ".flv" or ".3gp" or ".mpeg" or ".mpg" or ".mts" or ".m2ts" or ".mxf"
            or ".mpv" or ".ogv" or ".dv" or ".asf" or ".rm" or ".rmvb" or ".vob" or ".m2v";

    private void SetPreviewBitmap(Bitmap? bitmap)
    {
        var previous = _previewBitmap;
        _previewBitmap = bitmap;
        PreviewImage.Source = bitmap;
        previous?.Dispose();
    }

    private void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
    }

    private void CloseResources()
    {
        CancelLoad();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        SetPreviewBitmap(null);

        foreach (var path in _temporaryFiles.Distinct(StringComparer.Ordinal).ToArray())
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                    && directory.Contains("MacExplorer-archive-temp", StringComparison.Ordinal))
                    Directory.Delete(directory, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }
        _temporaryFiles.Clear();
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }

    private sealed record PreviewLocation(
        bool IsArchive,
        string? DirectoryPath,
        string? ArchivePath,
        string InternalPath,
        string? CustomTitle = null)
    {
        public string Title => CustomTitle
            ?? (IsArchive
                ? Path.GetFileName(ArchivePath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? "压缩包"
                : Path.GetFileName(DirectoryPath?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) ?? DirectoryPath ?? "文件夹");

        public string DisplayPath => IsArchive
            ? $"{ArchivePath}!/{InternalPath}".TrimEnd('/')
            : DirectoryPath ?? string.Empty;

        public static PreviewLocation Directory(string path) => new(false, path, null, string.Empty);
        public static PreviewLocation Archive(string path, string internalPath, string? title = null)
            => new(true, null, path, internalPath.Trim('/'), title);
    }
}
