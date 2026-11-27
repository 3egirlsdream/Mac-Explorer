using MacExplorer.Serialization;
using MacExplorer.Services.Subscriptions;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>Owns the selected palette/texture source; windows compose it independently at their own size.</summary>
public sealed class BackgroundThemeService : IDisposable, IMemoryDiagnosticSource, IIdleMemoryCache
{
    internal const string SettingKey = "background_theme";
    private readonly ISettingsService _settings;
    private readonly string _directory;
    private readonly HashSet<object> _consumers = [];
    private BackgroundThemeArtwork? _artwork;
    private Task? _initialization;
    private bool _initializationFull;
    private DateTime _lastUsedUtc = DateTime.UtcNow;
    private int _generation;
    private bool _disposed;
    public event Action? Changed;
    public BackgroundThemeOptions Options { get; private set; }
    public Bitmap? Image => _artwork?.Preview;
    public Bitmap? GetPreview(bool dark) => dark ? _artwork?.DarkPreview : Image;
    internal bool HasFullArtwork => _artwork is { IsPreviewOnly: false };
    internal int ActiveConsumerCount => _consumers.Count;
    private bool HasConfiguredSource => Options.ImageFile.Length > 0 || Options.PresetId.Length > 0;
    private bool NeedsFullArtwork() => Options.Enabled && HasConfiguredSource && _artwork is not { IsPreviewOnly: false };
    internal BackgroundThemeArtwork? AcquireArtwork()
    {
        if (_artwork == null) return null;
        _lastUsedUtc = DateTime.UtcNow;
        return _artwork.Retain();
    }
    internal void RegisterConsumer(object consumer)
    {
        _consumers.Add(consumer);
        _lastUsedUtc = DateTime.UtcNow;
    }
    internal void UnregisterConsumer(object consumer)
    {
        if (_consumers.Remove(consumer))
            _lastUsedUtc = DateTime.UtcNow;
    }
    public bool IsActive => Options.Enabled && _artwork?.Preview != null && Volatile.Read(ref _hasThemeAccess);
    private readonly FeatureAccessService? _access;
    private bool _hasThemeAccess;
    private void OnAccessChanged()
    {
        Volatile.Write(ref _hasThemeAccess, _access?.CanUse(PremiumFeature.BackgroundThemes) ?? !DistributionChannel.IsAppStore);
        Dispatcher.UIThread.Post(() =>
        {
            if (_disposed) return;
            if (Volatile.Read(ref _hasThemeAccess) && NeedsFullArtwork())
            {
                _initialization = null;
                _initializationFull = false;
                _ = InitializeAsync();
            }
            Changed?.Invoke();
        });
    }
    public string? ErrorKey { get; private set; }

    public BackgroundThemeService(ISettingsService settings, FeatureAccessService? access = null)
        : this(settings, Path.Combine(RuntimePaths.DataDirectory, "Themes"), access) { }

    internal BackgroundThemeService(ISettingsService settings, string directory, FeatureAccessService? access = null)
    {
        _access = access;
        _hasThemeAccess = access?.CanUse(PremiumFeature.BackgroundThemes) ?? !DistributionChannel.IsAppStore;
        if (_access != null) _access.Changed += OnAccessChanged;
        _settings = settings;
        _directory = directory;
        MemoryDiagnostics.Register(this);
        MemoryCacheMaintenance.Register(this);
        try { Options = (JsonSerializer.Deserialize(settings.Get(SettingKey) ?? "{}", AppJsonContext.Default.BackgroundThemeOptions) ?? new()).Normalize(); }
        catch (JsonException) { Options = new(); }
    }

    MemoryDiagnosticState IMemoryDiagnosticSource.GetMemoryState() => new("background-source", _artwork == null ? 0 : 1,
        (_artwork?.Texture is { } texture ? (long)texture.RowBytes * texture.Height : 0)
        + (_artwork?.Preview is { } preview ? (long)preview.PixelSize.Width * preview.PixelSize.Height * (_artwork.HasDistinctDarkPreview ? 8 : 4) : 0));

    public Task InitializeAsync()
    {
        if (_disposed || !HasConfiguredSource) return Task.CompletedTask;
        var needFull = Options.Enabled;
        if (_initialization != null && (_initializationFull || !needFull))
            return _initialization;
        _initializationFull = needFull;
        return _initialization = LoadAsync(needFull);
    }

    internal async Task InitializeForDisplayAsync()
    {
        if (_access != null) await _access.StartAsync();
        await InitializeAsync();
    }

    private async Task LoadAsync(bool fullTexture)
    {
        if (!HasConfiguredSource) return;
        var generation = ++_generation;
        var options = Options;
        BackgroundThemeArtwork? artwork = null;
        try
        {
            artwork = await LoadArtworkAsync(options, previewOnly: !fullTexture, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                artwork.ReleaseEncoded();
                var old = _artwork;
                _artwork = artwork; artwork = null;
                _lastUsedUtc = DateTime.UtcNow;
                ErrorKey = null;
                Changed?.Invoke();
                old?.Dispose();
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                ErrorKey = "background.unavailable";
                Changed?.Invoke();
            });
        }
        finally { artwork?.Dispose(); }
    }

    public async Task ImportAsync(Stream source, string displayName, CancellationToken token = default)
    {
        FeatureAccess.Require(PremiumFeature.BackgroundThemes);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialization is { IsCompleted: false } pending) await pending;
        token.ThrowIfCancellationRequested();
        var generation = ++_generation;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + count > BoundedImageDecoder.MaxEncodedBytes) throw new InvalidDataException();
            buffer.Write(chunk, 0, count);
        }
        BackgroundThemeArtwork? artwork = await Task.Run(() => BackgroundThemeArtwork.Decode(buffer.ToArray(), token), token);
        var file = Guid.NewGuid().ToString("N") + ".png";
        var path = Path.Combine(_directory, file);
        try
        {
            Directory.CreateDirectory(_directory);
            await File.WriteAllBytesAsync(path, artwork.Png, token);
            artwork.ReleaseEncoded();
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            var previous = Options.ImageFile;
            var old = _artwork;
            _artwork = artwork; artwork = null;
            _initialization = Task.CompletedTask;
            _initializationFull = true;
            _lastUsedUtc = DateTime.UtcNow;
            Options = (Options with { Enabled = true, PresetId = "", ImageFile = file, DisplayName = displayName }).Normalize();
            ErrorKey = null;
            Save();
            old?.Dispose();
            DeleteManagedFile(previous);
        }
        finally
        {
            artwork?.Dispose();
            if (Options.ImageFile != file) DeleteManagedFile(file);
        }
    }

    public async Task SelectPresetAsync(string presetId, CancellationToken token = default, BackgroundThemeColorSelection? colors = null)
    {
        FeatureAccess.Require(PremiumFeature.BackgroundThemes);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialization is { IsCompleted: false } pending) await pending;
        var preset = BackgroundThemePresets.Find(presetId);
        if (presetId.Length > 0 && preset == null) throw new ArgumentException("Unknown background preset.", nameof(presetId));
        var options = (Options with { PresetId = presetId, Enabled = true,
            Colors = colors == null || preset == null ? Options.Colors : Options.Colors.SetItem(presetId, colors.Normalize()),
            TextureStrength = presetId == Options.PresetId ? Options.TextureStrength : preset?.TextureStrength ?? Options.TextureStrength }).Normalize();
        var generation = ++_generation;
        if (presetId == Options.PresetId && preset != null && _artwork is { IsPreviewOnly: false })
        {
            token.ThrowIfCancellationRequested();
            _artwork.UpdatePreviews(options);
            _lastUsedUtc = DateTime.UtcNow;
            Options = options;
            Save();
            return;
        }
        BackgroundThemeArtwork? artwork = await LoadArtworkAsync(options, previewOnly: false, token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            artwork.ReleaseEncoded();
            var old = _artwork;
            _artwork = artwork; artwork = null;
            _initialization = Task.CompletedTask;
            _initializationFull = true;
            _lastUsedUtc = DateTime.UtcNow;
            Options = options;
            ErrorKey = null;
            Save();
            old?.Dispose();
        }
        finally { artwork?.Dispose(); }
    }

    private async Task<BackgroundThemeArtwork> LoadArtworkAsync(BackgroundThemeOptions options, bool previewOnly, CancellationToken token)
    {
        if (options.PresetId.Length == 0)
        {
            var bytes = await BoundedImageDecoder.ReadSourceAsync(Path.Combine(_directory, options.ImageFile));
            if (bytes == null) throw new InvalidDataException();
            return await Task.Run(() => previewOnly
                ? BackgroundThemeArtwork.DecodePreviewOnly(bytes, token)
                : BackgroundThemeArtwork.Decode(bytes, token), token);
        }
        var preset = BackgroundThemePresets.Find(options.PresetId) ?? throw new InvalidDataException();
        return await Task.Run(() =>
        {
            if (previewOnly)
            {
                using var previewTexture = preset.TextureAsset == null ? null : BackgroundThemeArtwork.DecodePresetPreview(preset.TextureAsset, token);
                token.ThrowIfCancellationRequested();
                using var preview = BackgroundThemeComposition.Render(previewTexture, options, 360, 240, false, token);
                using var darkPreview = BackgroundThemeComposition.Render(previewTexture, options, 360, 240, true, token);
                var light = BackgroundThemeArtwork.ToBitmap(preview);
                try { return new BackgroundThemeArtwork(null, light, [], BackgroundThemeArtwork.ToBitmap(darkPreview), isPreviewOnly: true); }
                catch { light.Dispose(); throw; }
            }
            var texture = preset.TextureAsset == null ? null : BackgroundThemeArtwork.DecodePresetTexture(preset.TextureAsset, token);
            try
            {
                token.ThrowIfCancellationRequested();
                using var preview = BackgroundThemeComposition.Render(texture, options, 360, 240, false, token);
                using var darkPreview = BackgroundThemeComposition.Render(texture, options, 360, 240, true, token);
                var light = BackgroundThemeArtwork.ToBitmap(preview);
                try { return new BackgroundThemeArtwork(texture, light, [], BackgroundThemeArtwork.ToBitmap(darkPreview)); }
                catch { light.Dispose(); throw; }
            }
            catch { texture?.Dispose(); throw; }
        }, token);
    }

    public void Update(bool enabled, int strength)
    {
        if (enabled || strength != (Options.PresetId.Length == 0 ? Options.Strength : Options.TextureStrength)) FeatureAccess.Require(PremiumFeature.BackgroundThemes);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = (Options.PresetId.Length == 0
            ? Options with { Enabled = enabled, Strength = strength }
            : Options with { Enabled = enabled, TextureStrength = strength }).Normalize();
        if (options == Options) return;
        var wasEnabled = Options.Enabled;
        Options = options;
        _lastUsedUtc = DateTime.UtcNow;
        if (!enabled)
        {
            ReleaseTexture();
        }
        else if (!wasEnabled && NeedsFullArtwork())
        {
            _initialization = null;
            _initializationFull = false;
            _ = InitializeAsync();
        }
        Save(deferred: true);
    }

    public void UpdateCustomColor(string presetId, string color) => UpdateColors(presetId, new("custom", color));

    internal void RestorePresetColors(string presetId) => UpdateColors(presetId, new());

    private void UpdateColors(string presetId, BackgroundThemeColorSelection colors)
    {
        FeatureAccess.Require(PremiumFeature.BackgroundThemes);
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (presetId != Options.PresetId || BackgroundThemePresets.Find(presetId) == null) return;
        var selection = colors.Normalize();
        if (Options.GetColors(presetId) == selection) return;
        var options = Options with { Colors = Options.Colors.SetItem(presetId, selection) };
        _artwork?.UpdatePreviews(options);
        _lastUsedUtc = DateTime.UtcNow;
        Options = options;
        Save(deferred: true);
    }

    public void Reset()
    {
        Dispatcher.UIThread.VerifyAccess();
        ++_generation;
        var previous = Options.ImageFile;
        var old = _artwork;
        _artwork = null;
        _initialization = null;
        _initializationFull = false;
        Options = new();
        ErrorKey = null;
        Save();
        old?.Dispose();
        DeleteManagedFile(previous);
    }

    internal void TrimMemory(DateTime unusedBefore)
    {
        if (_disposed || _artwork == null) return;
        if (_lastUsedUtc > unusedBefore) return;
        var hasAccess = Volatile.Read(ref _hasThemeAccess);
        if (Options.Enabled && hasAccess && _consumers.Count > 0) return;
        if (HasSettingsViewListener())
        {
            if (!Options.Enabled || !hasAccess) ReleaseTexture();
            return;
        }
        ReleaseArtwork();
    }

    void IIdleMemoryCache.TrimMemory(DateTime unusedBefore) => TrimMemory(unusedBefore);

    private void ReleaseTexture()
    {
        ++_generation;
        _initialization = null;
        _initializationFull = false;
        if (_artwork is not { Texture: not null } current) return;
        var previewOnly = current.DetachPreviewOnly();
        _artwork = previewOnly;
        current.Dispose();
    }

    private void ReleaseArtwork()
    {
        ++_generation;
        _initialization = null;
        _initializationFull = false;
        var old = _artwork;
        if (old == null) return;
        _artwork = null;
        Changed?.Invoke();
        old.Dispose();
    }

    private bool HasSettingsViewListener()
    {
        var handlers = Changed;
        if (handlers == null) return false;
        foreach (var handler in handlers.GetInvocationList())
            if (handler.Target is MacExplorer.Controls.BackgroundThemeSettingsView) return true;
        return false;
    }

    private void Save(bool deferred = false)
    {
        var json = JsonSerializer.Serialize(Options, AppJsonContext.Default.BackgroundThemeOptions);
        if (deferred) _settings.SetDeferred(SettingKey, json);
        else _settings.Set(SettingKey, json);
        Changed?.Invoke();
    }

    private void DeleteManagedFile(string file)
    {
        // Only files created by this service may be removed, never the imported source.
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out _) || Path.GetExtension(file) != ".png") return;
        try { File.Delete(Path.Combine(_directory, Path.GetFileName(file))); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _disposed = true; ++_generation;
        _consumers.Clear();
        _initialization = null;
        _initializationFull = false;
        var old = _artwork; _artwork = null;
        Changed?.Invoke();
        if (_access != null) _access.Changed -= OnAccessChanged;
        Changed = null;
        old?.Dispose();
    }

}
