using Avalonia.Controls;

namespace MacExplorer.Views;

public partial class CopilotMarkdownView : UserControl
{
    public CopilotMarkdownView() => InitializeComponent();

    public void SetMarkdown(string text) => Viewer.Markdown = text;
}
