using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;

namespace MacExplorer.Controls;

public partial class BackgroundThemeSettingsView
{
    private readonly Dictionary<string, (Button Button, Image Image)> _swatches = [];
    private readonly Dictionary<string, SKBitmap> _previewTextures = [];
    private readonly Dictionary<string, (string Key, Bitmap Image)> _previewBitmaps = [];
    private CancellationTokenSource? _thumbnailCancellation;
    private Flyout? _colorFlyout;
    private ColorPalette? _paletteView;
    internal string? OpenPalettePattern { get; private set; }
    internal bool ArePreviewsReady => _previewTextures.Count == BackgroundThemePresets.All.Count;
    internal Button GetSwatch(string pattern) => _swatches[pattern].Button;
    internal ColorPalette? OpenPalette => _colorFlyout?.IsOpen == true ? _paletteView : null;

    private void RefreshPatternRows(BackgroundThemeOptions options)
    {
        if (_swatches.Count == 0)
        {
            foreach (var preset in BackgroundThemePresets.All)
            {
                var image = new Image { Stretch = Stretch.UniformToFill };
                var button = new Button { Classes = { "ghost", "theme-swatch" }, Name = "ThemeSwatch_" + preset.Id,
                    Content = new Border { Classes = { "theme-swatch-image" }, Child = image } };
                PatternRows.Children.Add(button);
                _swatches[preset.Id] = (button, image);
                button.Click += async (_, _) =>
                {
                    ClosePalette();
                    var current = _service?.Options.GetColors(preset.Id) ?? new();
                    if (await SelectSwatchAsync(preset.Id, current) && this.IsAttachedToVisualTree()) ShowColorPalette(button, preset);
                };
            }
        }
        foreach (var preset in BackgroundThemePresets.All)
        {
            var button = _swatches[preset.Id].Button;
            var label = PresetLabel(preset);
            ToolTip.SetTip(button, label); AutomationProperties.SetName(button, label);
            var active = options.PresetId == preset.Id;
            button.Classes.Set("active", active);
            AutomationProperties.SetHelpText(button, active ? LocalizationText.Get("background.selected") : "");
            UpdateSwatchPreview(preset, options.GetColors(preset.Id));
        }
        if (_thumbnailCancellation == null && this.IsAttachedToVisualTree())
        {
            _thumbnailCancellation = new();
            _ = LoadPatternPreviewsAsync(_thumbnailCancellation.Token);
        }
    }

    private async Task LoadPatternPreviewsAsync(CancellationToken token)
    {
        try
        {
            foreach (var preset in BackgroundThemePresets.All)
            {
                var texture = await Task.Run(() => BackgroundThemeArtwork.DecodePresetTexture(preset.TextureAsset!, token, 384, 128), token);
                if (token.IsCancellationRequested) { texture.Dispose(); return; }
                _previewTextures.Add(preset.Id, texture);
                UpdateSwatchPreview(preset, _service?.Options.GetColors(preset.Id) ?? new());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            _errorKey = "background.preset_failed";
            Refresh();
        }
    }

    private void UpdateSwatchPreview(BackgroundThemePreset preset, BackgroundThemeColorSelection selection)
    {
        if (!_previewTextures.TryGetValue(preset.Id, out var texture)) return;
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var key = dark + ":" + selection.PaletteId + ":" + selection.CustomColor;
        if (_previewBitmaps.TryGetValue(preset.Id, out var previous) && previous.Key == key) return;
        var options = new BackgroundThemeOptions { PresetId = preset.Id, TextureStrength = preset.TextureStrength };
        options = options with { Colors = options.Colors.SetItem(preset.Id, selection) };
        using var frame = BackgroundThemeComposition.Render(texture, options, 180, 100, dark, CancellationToken.None);
        var bitmap = BackgroundThemeArtwork.ToBitmap(frame);
        _swatches[preset.Id].Image.Source = bitmap;
        _previewBitmaps[preset.Id] = (key, bitmap);
        previous.Image?.Dispose();
    }

    internal async Task<bool> SelectSwatchAsync(string presetId, BackgroundThemeColorSelection colors)
    {
        if (_busy || _service == null) return false;
        _busy = true; _errorKey = null; _importCancellation = new();
        Refresh();
        try { await _service.SelectPresetAsync(presetId, _importCancellation.Token, colors); return true; }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _errorKey = "background.preset_failed"; return false;
        }
        finally
        {
            _importCancellation.Dispose(); _importCancellation = null; _busy = false;
            if (this.IsAttachedToVisualTree()) Refresh();
        }
    }

    private void ShowColorPalette(Button button, BackgroundThemePreset preset)
    {
        var selected = _service!.Options.GetColors(preset.Id);
        var color = selected.PaletteId.Length == 0 ? preset.DefaultColor : selected.CustomColor;
        var picker = new ColorPalette { Width = 292 };
        picker.Configure(preset.ColorOptions, color, allowAlpha: false);
        picker.ColorChanged += value =>
        {
            if (value == preset.DefaultColor) _service?.RestorePresetColors(preset.Id);
            else _service?.UpdateCustomColor(preset.Id, value);
        };
        picker.ResetRequested += () =>
        {
            _service?.RestorePresetColors(preset.Id);
            picker.SetSelection(preset.DefaultColor);
        };
        OpenPalettePattern = preset.Id;
        _paletteView = picker;
        _colorFlyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight,
            Content = new Border { Classes = { "context-submenu-surface" }, Child = picker } };
        _colorFlyout.ShowAt(button);
    }

    internal void ClosePalette() { _colorFlyout?.Hide(); OpenPalettePattern = null; _paletteView = null; }

    private void ClearPatternPreviews()
    {
        ClosePalette(); _colorFlyout = null;
        _thumbnailCancellation?.Cancel(); _thumbnailCancellation?.Dispose(); _thumbnailCancellation = null;
        foreach (var swatch in _swatches.Values) swatch.Image.Source = null;
        foreach (var bitmap in _previewBitmaps.Values) bitmap.Image.Dispose();
        foreach (var texture in _previewTextures.Values) texture.Dispose();
        _previewBitmaps.Clear(); _previewTextures.Clear();
    }
}
