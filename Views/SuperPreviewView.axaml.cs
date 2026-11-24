using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;
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
    private const int MaxListThumbnailCandidates = 48;
    private readonly ObservableCollection<FileSystemEntry> _entries = [];
    private readonly List<PreviewLocation> _history = [];
    private readonly List<string> _temporaryFiles = [];
    private readonly IFileService? _fileService;
    private readonly IArchiveService? _archiveService;
    private readonly IThumbnailService? _thumbnailService;
    private readonly ISettingsService? _settingsService;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _previewCts;
    private Bitmap? _previewBitmap;
    private PreviewLocation? _location;
    private FileSystemEntry? _selectedEntry;
    private bool _ignoreSelection;
    private Control? _previousFocus;
    private int _operationVersion;
    private readonly bool _isCompactPreview;
    private readonly bool _isGalleryPreview;
    private readonly MemoryDiagnosticLifetime? _memoryLifetime;
    internal long DiagnosticId => _memoryLifetime?.Id ?? 0;
    internal void ReleaseDiagnosticLifetime() => _memoryLifetime?.SetState(DiagnosticObjectState.Released);

    public event EventHandler? RequestClose;

    /// <summary>Set by MainWindow so encrypted archives can use the existing password dialog.</summary>
    public Func<Task<string?>>? PasswordPrompt { get; set; }

    public SuperPreviewView() : this(isCompactPreview: false) { }

    internal SuperPreviewView(bool isCompactPreview, bool isGalleryPreview = false)
    {
        _memoryLifetime = MemoryDiagnostics.Track(this, DiagnosticObjectKind.Preview);
        InitializeComponent();
        AddHandler(KeyDownEvent, OnDiffPreviewKeyDown, RoutingStrategies.Tunnel);
        _isCompactPreview = isCompactPreview;
        _isGalleryPreview = isGalleryPreview;
        if (isCompactPreview)
            ConfigureCompactLayout();
        ItemsList.ItemsSource = _entries;
        PreviewContentGrid.SizeChanged += (_, _) => UpdatePreviewColumns();
        if (!isCompactPreview)
        {
            PreviewTree.NameOnly = true;
            ConfigureToolbar();
            PropertyChanged += (_, args) =>
            {
                if (args.Property == IsVisibleProperty) UpdatePreviewToolbarPlacement();
            };
        }
        _fileService = App.Services.GetService<IFileService>();
        _archiveService = App.Services.GetService<IArchiveService>();
        _thumbnailService = App.Services.GetService<IThumbnailService>();
        _settingsService = App.Services.GetService<ISettingsService>();
    }

    private void UpdatePreviewColumns()
    {
        if (_isCompactPreview) return;
        var definitions = PreviewContentGrid.ColumnDefinitions;
        definitions[0].Width = new GridLength(PreviewListCard.IsVisible ? ClampPreviewTreeWidth(_previewTreeWidth) : 0);
        definitions[1].Width = new GridLength(PreviewListCard.IsVisible ? 1 : 0);
    }

    private void ConfigureCompactLayout()
    {
        Focusable = false;
        PreviewBackdrop.Background = Brushes.Transparent;
        PreviewBackdrop.Padding = new Thickness(0);
        Shell.Background = Brushes.Transparent;
        Shell.BorderThickness = new Thickness(0);
        Shell.BoxShadow = new BoxShadows();
        PreviewHeader.IsVisible = false;
        BackButton.IsVisible = false;
        ClosePreviewButton.IsVisible = false;
        HeaderDivider.IsVisible = false;
        ContentDivider.IsVisible = false;
        PreviewTreeScroll.IsVisible = false;
        ItemsList.IsVisible = true;
        PreviewListCard.Background = Brushes.Transparent;
        PreviewCard.Background = Brushes.Transparent;
        PreviewCard.Padding = new Thickness(0);
    }

    internal async Task<bool> OpenGalleryFileAsync(FileSystemEntry entry, CancellationToken token)
    {
        MemoryDiagnostics.Operation(DiagnosticAction.PreviewOpen, DiagnosticId);
        IsVisible = true;
        PreviewListCard.IsVisible = false;
        PreviewCard.IsVisible = true;
        PreviewContentGrid.ColumnDefinitions = ColumnDefinitions.Parse("0,0,*");
        try
        {
            await ShowEntryPreviewAsync(entry, token);
            return !token.IsCancellationRequested && !PreviewPlaceholder.IsVisible;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }

    internal bool HasNativePreview => _documentPreview != null || _videoPreview != null;

    private void AnimatePreviewContentTransition()
    {
        var step = _previewStepDirection;
        _previewStepDirection = 0;
        if (_isCompactPreview || HasNativePreview) return;
        if (step != 0)
            ViewMotion.AnimateEnter(PreviewHost, fromX: step * 18, fromOpacity: 0.25, durationMs: 170);
        else
            ViewMotion.AnimateEnter(PreviewHost, fromOpacity: 0.35, durationMs: 140);
    }

    public async Task OpenAsync(FileSystemEntry entry, IReadOnlyList<FileSystemEntry>? fileOrder = null)
    {
        MemoryDiagnostics.Operation(DiagnosticAction.PreviewOpen, DiagnosticId);
        var wasVisible = IsVisible;
        if (!_isCompactPreview && !wasVisible)
            _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        CloseResources();
        _history.Clear();
        _entries.Clear();
        _location = null;
        _selectedEntry = null;
        _previewStepDirection = 0;
        IsVisible = true;
        if (!_isCompactPreview)
        {
            if (!wasVisible)
            {
                UpdateLayout();
                ViewMotion.AnimateEnter(PreviewBackdrop, fromOpacity: 0, durationMs: 150);
                ViewMotion.AnimateEnter(Shell, fromY: 10, fromOpacity: 0, durationMs: 170);
            }
            Focus();
            await OpenFullPreviewAsync(entry, fileOrder);
            return;
        }

        var showList = entry.IsDirectory || _archiveService?.IsArchiveFile(entry.FullPath) == true;
        PreviewListCard.IsVisible = showList;
        PreviewCard.IsVisible = !showList;
        PreviewContentGrid.ColumnDefinitions = ColumnDefinitions.Parse(showList ? "*,0,0" : "0,0,*");
        if (!showList)
        {
            _selectedEntry = entry;
            await ShowEntryPreviewAsync(entry);
            return;
        }

        var location = BuildInitialLocation(entry);
        if (location != null)
            await LoadLocationAsync(location, ArchivePathHelper.IsArchivePath(entry.FullPath) ? entry : null);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _memoryLifetime?.Link((TopLevel.GetTopLevel(this) as AppWindow)?.DiagnosticId ?? 0);
        _memoryLifetime?.SetState(DiagnosticObjectState.Attached);
        if (!_isCompactPreview)
        {
            _toolbarLanguage = LocalizationService.Current;
            if (_toolbarLanguage != null) _toolbarLanguage.LanguageChanged += RefreshToolbarLanguage;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _memoryLifetime?.SetState(DiagnosticObjectState.Detached);
        if (_toolbarLanguage != null) _toolbarLanguage.LanguageChanged -= RefreshToolbarLanguage;
        _toolbarLanguage = null;
        CloseResources();
        base.OnDetachedFromVisualTree(e);
    }

    public void Close()
    {
        if (!IsVisible && _location == null)
            return;

        MemoryDiagnostics.Operation(DiagnosticAction.PreviewClose, DiagnosticId, count: _isCompactPreview ? _entries.Count : PreviewTree.Rows.Count);
        IsVisible = false;
        CloseResources();
        _history.Clear();
        _entries.Clear();
        _location = null;
        _selectedEntry = null;
        _previousFocus?.Focus();
        _previousFocus = null;
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private PreviewLocation? BuildInitialLocation(FileSystemEntry entry)
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

        return null;
    }

    private async Task LoadLocationAsync(PreviewLocation location, FileSystemEntry? preferred)
    {
        CancelLoad();
        var tokenSource = new CancellationTokenSource();
        _loadCts = tokenSource;
        var token = tokenSource.Token;
        var version = ++_operationVersion;
        _location = location;
        _selectedEntry = null;
        ShowPlaceholder(LocalizationText.Get("preview.tree.loading"));

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
                foreach (var entry in SortPreviewEntries(entries))
                    _entries.Add(entry);

                var match = preferred == null
                    ? null
                    : _entries.FirstOrDefault(e => string.Equals(e.FullPath, preferred.FullPath, StringComparison.Ordinal));
                match ??= _entries.FirstOrDefault();
                ItemsList.SelectedItem = match;
                _selectedEntry = match;
            }
            finally
            {
                _ignoreSelection = false;
            }

            if (PreviewListCard.IsVisible && _entries.Count > 0)
                _ = PopulateListThumbnailsAsync(_entries.ToArray(), location, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _operationVersion)
                ShowPlaceholder(LocalizationText.Get("preview.tree.read-error", ex.Message));
        }
    }

    private async Task<IReadOnlyList<FileSystemEntry>> ReadDirectoryEntriesAsync(string path, CancellationToken token)
        => _fileService == null ? [] : await _fileService.GetDirectoryContentsAsync(path, token);

    private async Task<IReadOnlyList<FileSystemEntry>> ReadArchiveEntriesAsync(PreviewLocation location, CancellationToken token)
    {
        if (_archiveService == null) return [];
        return await ReadArchiveAsync(location.ArchivePath!,
            password => _archiveService.GetArchiveContentsAsync(location.ArchivePath!, location.InternalPath, password), token, browse: true)
            ?? [];
    }

    private IEnumerable<FileSystemEntry> SortPreviewEntries(IReadOnlyList<FileSystemEntry> entries, string? preferredPath = null)
        => entries.Where(entry => entry.FullPath == preferredPath || ShouldShowEntry(entry)).OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase);

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
            var token = _loadCts?.Token ?? CancellationToken.None;
            try
            {
                var extracted = ArchivePathHelper.IsArchivePath(entry.FullPath)
                    ? await ExtractArchiveEntryAsync(entry, token) : entry.FullPath;
                if (extracted != null)
                {
                    if (ArchivePathHelper.IsArchivePath(entry.FullPath)) _temporaryFiles.Add(extracted);
                    next = PreviewLocation.Archive(extracted, string.Empty);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidFormatException)
            {
                ShowPlaceholder(LocalizationText.Get("preview.tree.read-error", ex.Message));
                return;
            }
        }
        else if (!_location.IsArchive && _archiveService?.IsArchiveFile(entry.FullPath) == true)
        {
            next = PreviewLocation.Archive(entry.FullPath, string.Empty);
        }

        if (next == null) return;
        _history.Add(_location);
        await LoadLocationAsync(next, null);
    }

    private async Task<string?> ExtractArchiveEntryAsync(FileSystemEntry entry, CancellationToken token)
    {
        if (_archiveService == null) return null;
        var (archivePath, entryKey) = ArchivePathHelper.Parse(entry.FullPath);
        var extracted = await ReadArchiveAsync(archivePath,
            password => _archiveService is ArchiveService service
                ? service.ExtractEntryToTempAsync(archivePath, entryKey, password, token)
                : _archiveService.ExtractEntryToTempAsync(archivePath, entryKey, password), token);
        if (extracted != null && token.IsCancellationRequested) DeleteTemporaryFile(extracted);
        token.ThrowIfCancellationRequested();
        return extracted;
    }

    private async Task ShowEntryPreviewAsync(FileSystemEntry entry, CancellationToken? inheritedToken = null)
    {
        using var operation = MemoryDiagnostics.BeginOperation(DiagnosticAction.PreviewSelection, DiagnosticId);
        var expectedVersion = _operationVersion + 1;
        try
        {
            await ShowEntryPreviewCoreAsync(entry, inheritedToken);
            operation?.Complete(expectedVersion != _operationVersion || _previewCts?.IsCancellationRequested == true
                ? DiagnosticOutcome.Cancelled : DiagnosticOutcome.Completed);
        }
        catch (OperationCanceledException) { operation?.Complete(DiagnosticOutcome.Cancelled); throw; }
        catch { operation?.Complete(DiagnosticOutcome.Failed); throw; }
    }

    private async Task ShowEntryPreviewCoreAsync(FileSystemEntry entry, CancellationToken? inheritedToken)
    {
        ResetEntryPreview();
        _previewCts = CancellationTokenSource.CreateLinkedTokenSource(inheritedToken ?? CancellationToken.None);
        var token = _previewCts.Token;
        var version = _operationVersion;

        if (_isCompactPreview && entry.IsDirectory)
        {
            ShowFolderSummary(entry.Name, LocalizationText.Get("preview.tree.folder"), entry.FullPath, _entries.Count);
            return;
        }

        ShowPlaceholder(LocalizationText.Get("preview.loading"));
        string? previewPath;
        try { previewPath = await ResolvePreviewPathAsync(entry, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidFormatException)
        {
            if (version == _operationVersion && !token.IsCancellationRequested && IsVisible)
                ShowPlaceholder(LocalizationText.Get(DiffPreviewDocument.IsDiff(entry.Name)
                    ? "preview.text-unreadable" : "preview.unreadable"));
            return;
        }
        if (version != _operationVersion || token.IsCancellationRequested || !IsVisible)
            return;
        if (string.IsNullOrWhiteSpace(previewPath))
        {
            ShowPlaceholder(DiffPreviewDocument.IsDiff(entry.Name)
                ? LocalizationText.Get("preview.text-unreadable") : LocalizationText.Get("preview.unreadable"));
            return;
        }

        if (TryShowDocumentPreview(previewPath, entry.Name))
            return;

        if (TryShowVideoPreview(previewPath, entry, token, version))
            return;

        if (MacExplorer.Services.Markdown.MarkdownDocument.IsMarkdown(entry.Name))
        {
            await ShowMarkdownPreviewAsync(previewPath, entry, token, version);
            AnimatePreviewContentTransition();
            return;
        }

        if (DiffPreviewDocument.IsDiff(entry.Name))
        {
            await ShowDiffPreviewAsync(previewPath, token, version);
            AnimatePreviewContentTransition();
            return;
        }

        if (IsTextFile(entry))
        {
            await ShowTextPreviewAsync(previewPath, token, version);
            AnimatePreviewContentTransition();
            return;
        }

        try
        {
            var maxPixels = _isCompactPreview && !_isGalleryPreview ? 720 : 1800;
            var bytes = _thumbnailService == null
                ? null
                : await _thumbnailService.GetThumbnailAsync(previewPath, maxPixels, token);
            if (version != _operationVersion || token.IsCancellationRequested || !IsVisible)
                return;
            if (bytes != null && bytes.Length > 0)
            {
                var bitmap = await Task.Run(() => MacExplorer.Services.BoundedImageDecoder.Decode(bytes, maxPixels, token), token);
                if (version != _operationVersion || token.IsCancellationRequested || !IsVisible) { bitmap?.Dispose(); return; }
                if (bitmap == null) { ShowPlaceholder(LocalizationText.Get("preview.unavailable")); return; }
                SetPreviewBitmap(bitmap);
                PreviewImage.IsVisible = true;
                PreviewPlaceholder.IsVisible = false;
                PreviewTextScroll.IsVisible = false;
                FolderSummary.IsVisible = false;
                UpdatePreviewToolbar();
                AnimatePreviewContentTransition();
                return;
            }

            ShowPlaceholder(LocalizationText.Get(IsVideoFile(entry) ? "preview.video.unavailable" : "preview.unavailable"));
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (version != _operationVersion || token.IsCancellationRequested || !IsVisible)
                return;
            ShowPlaceholder(LocalizationText.Get("preview.failed"));
        }
    }

    private async Task<string?> ResolvePreviewPathAsync(FileSystemEntry entry, CancellationToken token)
    {
        if (Path.IsPathFullyQualified(entry.FullPath)) DirectoryAccess.Current.EnsureAccess(entry.FullPath);
        token.ThrowIfCancellationRequested();
        if (!ArchivePathHelper.IsArchivePath(entry.FullPath))
            return File.Exists(entry.FullPath) ? entry.FullPath : null;

        var extracted = await ExtractArchiveEntryAsync(entry, token);
        if (extracted != null)
        {
            if (_isCompactPreview) _temporaryFiles.Add(extracted);
            else _selectedTemporaryFile = extracted;
        }
        return extracted;
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
        if (!PreviewListCard.IsVisible || _thumbnailService == null || !Equals(_location, location))
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

    private async Task ShowTextPreviewAsync(string path, CancellationToken token, int version)
    {
        try
        {
            var text = await TextPreviewReader.ReadAsync(path, token);
            if (version != _operationVersion || token.IsCancellationRequested || !IsVisible)
                return;
            PreviewText.Text = text;
            _loadedPreviewText = text;
            PreviewTextScroll.IsVisible = true;
            PreviewText.IsVisible = true;
            PreviewImage.IsVisible = false;
            PreviewPlaceholder.IsVisible = false;
            FolderSummary.IsVisible = false;
            UpdatePreviewToolbar();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _operationVersion || token.IsCancellationRequested || !IsVisible)
                return;
            ShowPlaceholder(LocalizationText.Get(ex is TextPreviewTooLargeException
                ? "preview.text-too-large" : "preview.text-unreadable"));
        }
    }

    private void ShowFolderSummary(string title, string kind, string path, int? count)
    {
        PreviewImage.IsVisible = false;
        PreviewTextScroll.IsVisible = false;
        PreviewPlaceholder.IsVisible = false;
        FolderSummary.IsVisible = true;
        FolderSummaryTitle.Text = title;
        FolderSummaryDetails.Text = count.HasValue
            ? LocalizationText.Get("preview.tree.summary", kind, count.Value, path)
            : $"{kind}\n{path}";
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
        UpdatePreviewToolbar();
    }

    private async void GoBack(object? sender, RoutedEventArgs e)
    {
        if (!_isCompactPreview && _selectedNode?.Parent is { } parent)
        {
            await SelectTreeNodeAsync(parent);
            PreviewTree.Focus();
            return;
        }

        if (_history.Count == 0)
        {
            Close();
            return;
        }

        var previous = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        await LoadLocationAsync(previous, null);
    }

    private void ClosePreview(object? sender, RoutedEventArgs e) => Close();

    private void OnSuperPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (_isGalleryPreview) return;
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

        if (e.Key == Key.Back && (_history.Count > 0 || _selectedNode?.Parent != null))
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
        => TextFileTypes.IsText(entry.Name);

    private static bool IsVideoFile(FileSystemEntry entry)
        => MacExplorer.Services.Impl.FileIconResolver.ResolveIconKey(Path.GetExtension(entry.Name), entry.Name) == "file-video";

    private void SetPreviewBitmap(Bitmap? bitmap)
    {
        ClearDiffPreview();
        ClearVideoPreview();
        ClearDocumentPreview();
        ClearMarkdownPreview();
        _loadedPreviewText = null;
        ZoomedImage.Source = null;
        ZoomedImageScroll.IsVisible = false;
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
        ResetEntryPreview();
        if (!_isCompactPreview)
        {
            ClearPreviewTree();
            _previewFiles = [];
            UpdatePreviewSteps();
        }
        foreach (var path in _temporaryFiles) DeleteTemporaryFile(path);
        _temporaryFiles.Clear();
        _archivePasswords.Clear();
    }

    private void ResetEntryPreview()
    {
        _operationVersion++;
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        SetPreviewBitmap(null);
        DeleteSelectedTemporaryFile();
        PreviewText.Text = string.Empty;
    }

    private void DeleteSelectedTemporaryFile()
    {
        if (_selectedTemporaryFile == null) return;
        DeleteTemporaryFile(_selectedTemporaryFile);
        _selectedTemporaryFile = null;
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (directory != null && Path.GetFileName(Path.GetDirectoryName(directory)) == "MacExplorer-archive-temp")
                Directory.Delete(directory, recursive: true);
            else
                File.Delete(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PreviewLocation(
        bool IsArchive,
        string? DirectoryPath,
        string? ArchivePath,
        string InternalPath)
    {
        public static PreviewLocation Directory(string path) => new(false, path, null, string.Empty);
        public static PreviewLocation Archive(string path, string internalPath)
            => new(true, null, path, internalPath.Trim('/'));
    }
}
