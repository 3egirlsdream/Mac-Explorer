using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Services.Impl;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Controls;

internal sealed class TagMenuHeader : Grid
{
    private readonly TextBlock _title;
    private readonly Button _create;

    public TagMenuHeader(string title, Action create)
    {
        ColumnDefinitions = new ColumnDefinitions("*,28");
        Margin = new Thickness(8, 0, 4, 0); Height = 32;
        _title = AppTypography.BindFontSize(new TextBlock { VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "tag-tree-title" } }, AppTypography.Body);
        _create = new Button { Name = "CreateTagButton", Classes = { "icon-action", "tag-tree-create" },
            Content = TagTreePanel.Icon(Icons.StarAdd, 16), VerticalAlignment = VerticalAlignment.Center };
        _create.Click += (_, _) => create();
        SetColumn(_create, 1); Children.Add(_title); Children.Add(_create);
        Update(title);
    }

    public void Update(string title)
    {
        _title.Text = title;
        AutomationProperties.SetName(_create, LocalizationText.Get("新建标签…"));
        ToolTip.SetTip(_create, LocalizationText.Get("新建标签…"));
    }
}
