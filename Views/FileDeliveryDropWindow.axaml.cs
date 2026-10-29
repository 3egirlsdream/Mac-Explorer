using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views;

public partial class FileDeliveryDropWindow : AppWindow
{
    private readonly FileDeliveryService _delivery;
    private readonly ILocalizationService? _localization;

    public FileDeliveryDropWindow(FileDeliveryService delivery)
    {
        InitializeComponent();
        _delivery = delivery;
        _localization = LocalizationService.Current;
        _delivery.StagingChanged += OnStagingChanged;
        if (_localization != null) _localization.LanguageChanged += UpdateCount;
        Closed += (_, _) =>
        {
            _delivery.StagingChanged -= OnStagingChanged;
            if (_localization != null) _localization.LanguageChanged -= UpdateCount;
        };
        UpdateCount();
    }

    private void OnStagingChanged(object? sender, EventArgs e) => UpdateCount();
    private void UpdateCount() => CountText.Text = LocalizationText.Get("delivery.count", _delivery.StagedPaths.Count);
    internal void SetDropHover(bool hover) => DropSurface.Classes.Set("drag-over", hover);
}
