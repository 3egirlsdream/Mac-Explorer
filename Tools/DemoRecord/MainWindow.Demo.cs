#if DEMO_AUTOMATION
using Avalonia.Controls;
using Avalonia.VisualTree;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class MainWindow
{
    internal bool DemoPreviewVisible => SuperPreviewControl.IsVisible;
    internal bool DemoPreviewReady => SuperPreviewControl.DemoContentReady;
    internal bool DemoGlobalSearchVisible => GlobalSearchOverlay.IsVisible;
    internal void DemoOpenGlobalSearch() => OpenGlobalSearch();
    internal string[] DemoGlobalResults => _globalSearchSuggestions.Select(s => s.Title).ToArray();
    internal string DemoEditorText => this.GetVisualDescendants().OfType<MarkdownEditorView>().FirstOrDefault()
        ?.FindControl<global::AvaloniaEdit.TextEditor>("Editor")?.Text ?? "";
    internal Task DemoPreviewAsync(string path) => OpenSuperPreviewAsync(new FileSystemEntry
    { FullPath = path, Name = Path.GetFileName(path), IsDirectory = Directory.Exists(path) });
    internal void DemoClosePreview() => SuperPreviewControl.Close();
}

public partial class FileListView
{
    internal global::Avalonia.Controls.ContextMenu? DemoMenu => _openMenu;
}

public partial class SuperPreviewView
{
    internal bool DemoContentReady => IsVisible && !PreviewPlaceholder.IsVisible &&
        (_documentPreview?.IsEffectivelyVisible == true || PreviewImage.Source != null && PreviewImage.IsVisible
            || PreviewTextScroll.IsVisible || FolderSummary.IsVisible);
}

public partial class AiView
{
    internal Task DemoSearchAsync(string query)
    {
        AiSearchBox.Text = query;
        return SearchAsync(query);
    }
}
#endif
