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
    private readonly Button _manage;

    public TagMenuHeader(string title, Action manage)
    {
        ColumnDefinitions = new ColumnDefinitions("*,28");
        Margin = new Thickness(8, 0, 4, 0);
        Height = 32;
        _title = AppTypography.BindFontSize(new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeight.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Classes = { "tag-tree-title" }
        }, AppTypography.Body);
        _manage = new AppButton
        {
            Name = "ManageCollectionsButton",
            Classes = { "icon-action", "tag-tree-create" },
            Content = TagTreePanel.Icon(Icons.Settings, 15),
            VerticalAlignment = VerticalAlignment.Center
        };
        _manage.Click += (_, _) => manage();
        SetColumn(_manage, 1);
        Children.Add(_title);
        Children.Add(_manage);
        Update(title);
    }

    public void Update(string title)
    {
        _title.Text = title;
        var tip = LocalizationText.Get("tags.manage");
        AutomationProperties.SetName(_manage, tip);
        ToolTip.SetTip(_manage, tip);
    }
}
