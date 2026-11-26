using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Preview;

namespace MacExplorer.Views;

internal sealed partial class MindMapPreviewView : UserControl
{
    private MindMapDocument? _document;
    private ILocalizationService? _language;

    internal MindMapPreviewView() => InitializeComponent();

    internal bool IsSheetPickerOpen => SheetPicker.IsDropDownOpen;

    internal bool TryCloseSheetPicker()
    {
        if (!SheetPicker.IsDropDownOpen) return false;
        SheetPicker.IsDropDownOpen = false;
        return true;
    }

    internal void SetDocument(MindMapDocument document)
    {
        _document = document;
        Canvas.SetDocument(document);
        SheetPicker.IsVisible = document.Sheets.Count > 1;
        if (SheetPicker.IsVisible) RefreshSheetLabels();
        else Canvas.SetSheet(0);
    }

    internal void Clear()
    {
        _document = null;
        Canvas.Clear();
        SheetPicker.IsDropDownOpen = false;
        SheetPicker.ItemsSource = null;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _language = LocalizationService.Current;
        if (_language != null) _language.LanguageChanged += RefreshSheetLabels;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_language != null) _language.LanguageChanged -= RefreshSheetLabels;
        _language = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void RefreshSheetLabels()
    {
        if (_document == null || !SheetPicker.IsVisible) return;
        var index = Math.Max(0, SheetPicker.SelectedIndex);
        SheetPicker.ItemsSource = _document.Sheets.Select((sheet, i) => string.IsNullOrWhiteSpace(sheet.Title)
            ? LocalizationText.Get("preview.mind-map.sheet", i + 1) : sheet.Title).ToArray();
        SheetPicker.SelectedIndex = index;
    }

    private void OnSheetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SheetPicker.SelectedIndex >= 0) Canvas.SetSheet(SheetPicker.SelectedIndex);
    }

    private void OnZoomOut(object? sender, RoutedEventArgs e) => Canvas.Zoom(false);
    private void OnZoomIn(object? sender, RoutedEventArgs e) => Canvas.Zoom(true);
    private void OnFit(object? sender, RoutedEventArgs e) => Canvas.Fit();
}
