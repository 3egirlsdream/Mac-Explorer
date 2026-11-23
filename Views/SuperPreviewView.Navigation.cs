using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private FileSystemEntry[] _previewFiles = [];
    private int _previousPreviewIndex = -1;
    private int _nextPreviewIndex = -1;

    private async Task SelectStandaloneFileAsync(FileSystemEntry entry)
    {
        _selectedEntry = entry;
        UpdatePreviewHeader(entry.Name);
        UpdatePreviewSteps();
        await ShowEntryPreviewAsync(entry);
    }

    private void UpdatePreviewSteps()
    {
        if (_selectedNode is { } node)
        {
            var index = _visiblePreviewNodes.IndexOf(node);
            _previousPreviewIndex = FindAdjacentTreeFile(index, -1);
            _nextPreviewIndex = FindAdjacentTreeFile(index, 1);
        }
        else
        {
            var index = Array.FindIndex(_previewFiles, file => file.FullPath == _selectedEntry?.FullPath);
            _previousPreviewIndex = index > 0 ? index - 1 : -1;
            _nextPreviewIndex = index >= 0 && index + 1 < _previewFiles.Length ? index + 1 : -1;
        }
        PreviewPreviousSlot.IsVisible = _previousPreviewIndex >= 0;
        PreviewNextSlot.IsVisible = _nextPreviewIndex >= 0;
        UpdatePreviewToolbarPlacement();
    }

    private int FindAdjacentTreeFile(int index, int step)
    {
        if (index < 0) return -1;
        for (var i = index + step; i >= 0 && i < _visiblePreviewNodes.Count; i += step)
            if (!_visiblePreviewNodes[i].Entry.IsFolder) return i;
        return -1;
    }

    private async void OnPreviewStepClick(object? sender, RoutedEventArgs e)
    {
        var index = sender == PreviewPreviousButton ? _previousPreviewIndex : _nextPreviewIndex;
        if (index < 0) return;
        await RunToolbarActionAsync(() => _selectedNode != null
            ? SelectTreeNodeAsync(_visiblePreviewNodes[index])
            : SelectStandaloneFileAsync(_previewFiles[index]));
    }

    private void UpdatePreviewStepPlacement(bool native, bool show)
    {
        PlacePreviewStep(PreviewPreviousSlot, PreviewPreviousZone, NativePreviewPreviousSlot, NativePreviewPreviousPopup);
        PlacePreviewStep(PreviewNextSlot, PreviewNextZone, NativePreviewNextSlot, NativePreviewNextPopup);

        void PlacePreviewStep(Border slot, Border zone, Border nativeSlot, Popup popup)
        {
            if (native && slot.Child == zone)
            {
                slot.Child = null;
                nativeSlot.Child = zone;
            }
            else if (!native && nativeSlot.Child == zone)
            {
                popup.IsOpen = false;
                nativeSlot.Child = null;
                slot.Child = zone;
            }
            popup.IsOpen = native && show && slot.IsVisible;
        }
    }
}
