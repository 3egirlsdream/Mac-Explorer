using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>Owns the selected palette/texture source; windows compose it independently at their own size.</summary>
public sealed class BackgroundThemeService : IDisposable, IMemoryDiagnosticSource
{
    internal const string SettingKey = "background_theme";
    private readonly ISettingsService _settings;
    private readonly string _directory;
    private BackgroundThemeArtwork? _artwork;
    private Task? _initialization;
    private int _generation;
    private bool _disposed;
    public event Action? Changed;
    public BackgroundThemeOptions Options { get; private set; }
    public Bitmap? Image => _artwork?.Preview;
    public Bitmap? GetPreview(bool dark) => dark ? _artwork?.DarkPreview : Image;
    internal BackgroundThemeArtwork? AcquireArtwork() => _artwork?.Retain();
    public bool IsActive => Options.Enabled && Image != null;
    public string? ErrorKey { get; private set; }

    public BackgroundThemeService(ISettingsService settings)
        : this(settings, Path.Combine(RuntimePaths.DataDirectory, "Themes")) { }

    internal BackgroundThemeService(ISettingsService settings, string directory)
    {
        _settings = settings;
        _directory = directory;
        MemoryDiagnostics.Register(this);
        try { Options = (JsonSerializer.Deserialize<BackgroundThemeOptions>(settings.Get(SettingKey) ?? "{}") ?? new()).Normalize(); }
        catch (JsonException) { Options = new(); }
    }

    MemoryDiagnosticState IMemoryDiagnosticSource.GetMemoryState() => new("background-source", _artwork == null ? 0 : 1,
        (_artwork?.Texture is { } texture ? (long)texture.RowBytes * texture.Height : 0)
        + (_artwork?.Preview is { } preview ? (long)preview.PixelSize.Width * preview.PixelSize.Height * 8 : 0));

    public Task InitializeAsync() => _initialization ??= LoadAsync();

    private async Task LoadAsync()
    {
        if (Options.ImageFile.Length == 0 && Options.PresetId.Length == 0) return;
        var generation = _generation;
        BackgroundThemeArtwork? artwork = null;
        try
        {
            artwork = await LoadArtworkAsync(Options, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || generation != _generation) return;
                artwork.ReleaseEncoded();
                _artwork = artwork; artwork = null;
                Changed?.Invoke();
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
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
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

    public async Task SelectPresetAsync(string presetId, CancellationToken token = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        await InitializeAsync();
        var preset = BackgroundThemePresets.Find(presetId);
        if (presetId.Length > 0 && preset == null) throw new ArgumentException("Unknown background preset.", nameof(presetId));
        var options = (Options with { PresetId = presetId, Enabled = true,
            TextureStrength = preset?.TextureStrength ?? Options.TextureStrength }).Normalize();
        var generation = ++_generation;
        BackgroundThemeArtwork? artwork = await LoadArtworkAsync(options, token);
        try
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || generation != _generation) return;
            artwork.ReleaseEncoded();
            var old = _artwork;
            _artwork = artwork; artwork = null;
            Options = options;
            ErrorKey = null;
            Save();
            old?.Dispose();
        }
        finally { artwork?.Dispose(); }
    }

    private async Task<BackgroundThemeArtwork> LoadArtworkAsync(BackgroundThemeOptions options, CancellationToken token)
    {
        if (options.PresetId.Length == 0)
        {
            var bytes = await BoundedImageDecoder.ReadSourceAsync(Path.Combine(_directory, options.ImageFile));
            if (bytes == null) throw new InvalidDataException();
            return await Task.Run(() => BackgroundThemeArtwork.Decode(bytes, token), token);
        }
        var preset = BackgroundThemePresets.Find(options.PresetId) ?? throw new InvalidDataException();
        return await Task.Run(() =>
        {
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

    public void Update(bool enabled, BackgroundImageLayout layout, int strength)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        var options = (Options.PresetId.Length == 0
            ? Options with { Enabled = enabled, Layout = layout, Strength = strength }
            : Options with { Enabled = enabled, Layout = layout, TextureStrength = strength }).Normalize();
        if (options == Options) return;
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
        Options = new();
        ErrorKey = null;
        Save();
        old?.Dispose();
        DeleteManagedFile(previous);
    }

    private void Save(bool deferred = false)
    {
        var json = JsonSerializer.Serialize(Options);
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
        var old = _artwork; _artwork = null;
        Changed?.Invoke();
        Changed = null;
        old?.Dispose();
    }

}
