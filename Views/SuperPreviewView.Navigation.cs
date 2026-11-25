using Avalonia.Interactivity;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class SuperPreviewView
{
    private FileSystemEntry[] _previewFiles = [];
    private int _previousPreviewIndex = -1;
    private int _nextPreviewIndex = -1;
    private int _previewStepDirection;

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
        if (HasNativePreview) UpdatePreviewToolbar();
        else UpdatePreviewToolbarPlacement();
    }

    private int FindAdjacentTreeFile(int index, int step)
    {
        if (index < 0) return -1;
        for (var i = index + step; i >= 0 && i < _visiblePreviewNodes.Count; i += step)
            if (!_visiblePreviewNodes[i].Entry.IsFolder) return i;
        return -1;
    }

    private async void OnPreviewStepClick(object? sender, RoutedEventArgs e)
        => await StepPreviewAsync(sender == PreviewPreviousButton);

    private Task StepPreviewAsync(bool isPrevious)
    {
        var index = isPrevious ? _previousPreviewIndex : _nextPreviewIndex;
        if (index < 0) return Task.CompletedTask;
        _previewStepDirection = isPrevious ? -1 : 1;
        return RunToolbarActionAsync(() => _selectedNode != null
            ? SelectTreeNodeAsync(_visiblePreviewNodes[index])
            : SelectStandaloneFileAsync(_previewFiles[index]));
    }
}
