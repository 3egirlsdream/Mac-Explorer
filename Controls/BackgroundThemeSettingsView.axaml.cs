using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Automation;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Controls;

public partial class BackgroundThemeSettingsView : UserControl
{
    private BackgroundThemeService? _service;
    private ILocalizationService? _language;
    private CancellationTokenSource? _importCancellation;
    private bool _updating, _busy;
    private string? _errorKey;
    private string? _layoutLanguage;

    public BackgroundThemeSettingsView()
    {
        InitializeComponent();
        ActualThemeVariantChanged += (_, _) => Refresh();
        StrengthSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty) SaveOptions();
        };
        Refresh();
    }

    internal BackgroundThemeSettingsView(BackgroundThemeService service) : this() => _service = service;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _service ??= App.Services?.GetService<BackgroundThemeService>();
        _language = LocalizationService.Current;
        if (_language != null) _language.LanguageChanged += Refresh;
        if (_service != null)
        {
            _service.Changed += Refresh;
            _ = _service.InitializeAsync();
        }
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_service != null) _service.Changed -= Refresh;
        if (_language != null) _language.LanguageChanged -= Refresh;
        _language = null;
        _importCancellation?.Cancel();
        PreviewImage.Source = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Refresh()
    {
        _updating = true;
        try
        {
            var options = _service?.Options ?? new();
            var preset = BackgroundThemePresets.Find(options.PresetId);
            var language = LocalizationService.Current?.Culture.Name ?? "zh-CN";
            if (_layoutLanguage != language)
            {
                LayoutCombo.ItemsSource = new[] { LocalizationText.Get("background.height_tile"), LocalizationText.Get("background.cover") };
                PresetCombo.ItemsSource = new[] { LocalizationText.Get("background.custom") }
                    .Concat(BackgroundThemePresets.All.Select(PresetLabel)).ToArray();
                _layoutLanguage = language;
            }
            PreviewImage.Source = _service?.GetPreview(ActualThemeVariant == ThemeVariant.Dark);
            PlaceholderIcon.IsVisible = PreviewImage.Source == null;
            ImageName.Text = preset != null ? PresetLabel(preset)
                : options.DisplayName.Length > 0 ? options.DisplayName : LocalizationText.Get("background.none");
            ToolTip.SetTip(ImageName, ImageName.Text);
            EnabledToggle.IsChecked = options.Enabled;
            LayoutCombo.SelectedIndex = options.Layout == BackgroundImageLayout.Cover ? 1 : 0;
            PresetCombo.SelectedIndex = preset == null ? 0 : BackgroundThemePresets.All.ToList().IndexOf(preset) + 1;
            StrengthSlider.Value = preset == null ? options.Strength : options.TextureStrength;
            StrengthValue.Text = StrengthSlider.Value + "%";
            StrengthLabel.Text = LocalizationText.Get(preset == null ? "background.strength" : "background.texture_strength");
            LayoutLabel.Text = LocalizationText.Get(preset == null ? "background.layout" : "background.texture_layout");
            AutomationProperties.SetName(StrengthSlider, StrengthLabel.Text);
            AutomationProperties.SetName(LayoutCombo, LayoutLabel.Text);
            LayoutRow.IsVisible = StrengthRow.IsVisible = preset == null || preset.TextureAsset != null;
            ChooseButton.IsEnabled = _service != null && !_busy;
            PresetCombo.IsEnabled = _service != null && !_busy;
            ResetButton.IsEnabled = (options.ImageFile.Length > 0 || options.PresetId.Length > 0) && !_busy;
            EnabledToggle.IsEnabled = _service?.Image != null && !_busy;
            LayoutCombo.IsEnabled = StrengthSlider.IsEnabled = _service?.IsActive == true && !_busy;
            var error = _errorKey ?? _service?.ErrorKey;
            ErrorText.IsVisible = error != null;
            ErrorText.Text = error == null ? "" : LocalizationText.Get(error);
        }
        finally { _updating = false; }
    }

    private void OptionsChanged(object? sender, RoutedEventArgs e) => SaveOptions();

    private static string PresetLabel(BackgroundThemePreset preset) =>
        $"{LocalizationText.Get(preset.FamilyKey)} · {LocalizationText.Get(preset.NameKey)}";

    private void SaveOptions()
    {
        if (_updating || _busy || _service == null) return;
        _service.Update(EnabledToggle.IsChecked == true,
            LayoutCombo.SelectedIndex == 1 ? BackgroundImageLayout.Cover : BackgroundImageLayout.HeightTile,
            (int)Math.Round(StrengthSlider.Value));
    }

    private void ResetTheme(object? sender, RoutedEventArgs e)
    {
        _errorKey = null;
        _service?.Reset();
    }

    private async void SelectPreset(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _busy || _service == null || PresetCombo.SelectedIndex < 0) return;
        var id = PresetCombo.SelectedIndex == 0 ? "" : BackgroundThemePresets.All[PresetCombo.SelectedIndex - 1].Id;
        if (id == _service.Options.PresetId) return;
        if (id.Length == 0 && _service.Options.ImageFile.Length == 0)
        {
            ChooseImage(sender, e);
            return;
        }
        _busy = true; _errorKey = null;
        _importCancellation = new CancellationTokenSource();
        Refresh();
        try { await _service.SelectPresetAsync(id, _importCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _errorKey = "background.preset_failed";
        }
        finally
        {
            _importCancellation.Dispose(); _importCancellation = null;
            _busy = false;
            if (this.IsAttachedToVisualTree()) Refresh();
        }
    }

    private async void ChooseImage(object? sender, RoutedEventArgs e)
    {
        if (_busy || _service == null || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        _busy = true; _errorKey = null;
        _importCancellation = new CancellationTokenSource();
        var token = _importCancellation.Token;
        Refresh();
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationText.Get("background.choose"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("PNG / JPEG / WebP")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp"],
                    MimeTypes = ["image/png", "image/jpeg", "image/webp"]
                }]
            });
            token.ThrowIfCancellationRequested();
            if (files.Count == 0) return;
            using var file = files[0];
            await using var input = await file.OpenReadAsync();
            await _service.ImportAsync(input, file.Name, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _errorKey = "background.import_failed";
        }
        finally
        {
            _importCancellation.Dispose(); _importCancellation = null;
            _busy = false;
            if (this.IsAttachedToVisualTree()) Refresh();
        }
    }
}
