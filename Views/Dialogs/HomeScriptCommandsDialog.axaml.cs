using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services.Impl;

namespace MacExplorer.Views.Dialogs;

public partial class HomeScriptCommandsDialog : DialogWindow
{
    private readonly string _path;
    private readonly HomeWorkspaceService _workspace;
    private readonly List<CommandRow> _rows = [];
    private static readonly string[] Shells = ["/bin/zsh", "/bin/bash", "/bin/sh"];

    public HomeScriptCommandsDialog(string path, HomeWorkspaceService workspace)
    {
        InitializeComponent();
        _path = path;
        _workspace = workspace;
        Title = $"脚本命令 · {Path.GetFileName(path)}";
        var commands = workspace.GetCommands(path);
        foreach (var command in commands) AddRow(command);
        if (commands.Count == 0) AddRow(HomeScriptCommand.Create(path));
        Height = Math.Clamp(112 + _rows.Count * 144, MinHeight, 540);
    }

    private void AddRow(HomeScriptCommand command)
    {
        if (_rows.Count >= 32) return;
        var name = new TextBox { Text = command.Name, PlaceholderText = "命令名称", MaxLength = 80,
            Classes = { "settings-inline-input" } };
        var body = new TextBox { Text = command.Command, Classes = { "multiline", "settings-command-editor" },
            MaxLength = 16384 };
        var directory = new TextBox { Text = command.WorkingDirectory, PlaceholderText = "脚本所在文件夹",
            Classes = { "settings-inline-input" } };
        var shell = new ComboBox { ItemsSource = Shells, SelectedItem = command.Shell, MinWidth = 110,
            Classes = { "settings-inline-input" } };
        var menuIcon = new ComboBox
        {
            ItemsSource = Enum.GetValues<HomeScriptMenuIcon>(), SelectedItem = command.MenuIcon,
            Classes = { "icon-picker", "settings-inline-input" },
            SelectionBoxItemTemplate = new FuncDataTemplate<HomeScriptMenuIcon>((kind, _) => CreateMenuIconOption(kind, false)),
            ItemTemplate = new FuncDataTemplate<HomeScriptMenuIcon>((kind, _) => CreateMenuIconOption(kind, true))
        };
        AutomationProperties.SetName(name, "命令名称");
        AutomationProperties.SetName(body, "命令内容");
        AutomationProperties.SetName(directory, "工作目录");
        AutomationProperties.SetName(shell, "执行 Shell");
        AutomationProperties.SetName(menuIcon, "右键菜单图标");
        ToolTip.SetTip(body, "使用 \"$SCRIPT\" 引用脚本，\"$SCRIPT_DIR\" 引用所在文件夹。命令在默认终端中执行。");
        ToolTip.SetTip(directory, "工作目录，留空使用脚本所在文件夹");
        ToolTip.SetTip(shell, "执行 Shell");
        var content = new StackPanel();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 4 };
        header.Children.Add(menuIcon);
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        var remove = new Button
        {
            Content = new PathIcon { Data = Geometry.Parse(Assets.Icons.Subtract), Width = 14, Height = 14 },
            Classes = { "ghost", "compact", "icon" }
        };
        AutomationProperties.SetName(remove, "移除命令");
        ToolTip.SetTip(remove, "移除命令");
        Grid.SetColumn(remove, 2); header.Children.Add(remove);
        content.Children.Add(new Border { Classes = { "settings-compact-row", "settings-divider" }, Child = header });
        content.Children.Add(new Border { Classes = { "settings-divider-row" }, Child = body });
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        footer.Children.Add(shell);
        directory.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(directory, 1); footer.Children.Add(directory);
        content.Children.Add(new Border { Classes = { "settings-compact-row" }, Child = footer });
        var border = new Border { Classes = { "settings-group" }, Child = content };
        var row = new CommandRow(command.Id, border, name, body, shell, directory, menuIcon);
        _rows.Add(row); CommandRows.Children.Add(border);
        remove.Click += (_, _) => { _rows.Remove(row); CommandRows.Children.Remove(border); AddButton.IsEnabled = true; };
        AddButton.IsEnabled = _rows.Count < 32;
    }

    private static Control CreateMenuIconOption(HomeScriptMenuIcon kind, bool showLabel)
    {
        var icon = HomeScriptMenuIcons.Create(kind) ?? new PathIcon
        {
            Data = Geometry.Parse(Assets.Icons.Subtract), Width = 16, Height = 16,
            [!PathIcon.ForegroundProperty] = new DynamicResourceExtension("TextMutedBrush")
        };
        var label = kind switch { HomeScriptMenuIcon.Start => "启动", HomeScriptMenuIcon.Stop => "中断", _ => "不配置" };
        AutomationProperties.SetName(icon, label);
        ToolTip.SetTip(icon, label);
        if (!showLabel) return icon;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } }
        };
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var number = _rows.Count + 1;
        var name = $"命令 {number}";
        while (_rows.Any(r => string.Equals(r.Name.Text, name, StringComparison.OrdinalIgnoreCase))) name = $"命令 {++number}";
        AddRow(HomeScriptCommand.Create(_path) with { Name = name });
    }
    private void OnCancel(object? sender, RoutedEventArgs e) => Close();
    private void OnSave(object? sender, RoutedEventArgs e)
    {
        try
        {
            _workspace.SaveCommands(_path, _rows.Select(r => new HomeScriptCommand(r.Id,
                r.Name.Text ?? "", r.Body.Text ?? "", r.Shell.SelectedItem as string ?? "/bin/zsh", r.Directory.Text ?? "")
                { MenuIcon = r.MenuIcon.SelectedItem is HomeScriptMenuIcon icon ? icon : HomeScriptMenuIcon.None }));
            Close();
        }
        catch (Exception ex) { ErrorText.Text = ex.Message; ErrorText.IsVisible = true; }
    }

    private sealed record CommandRow(string Id, Border Surface, TextBox Name, TextBox Body, ComboBox Shell, TextBox Directory, ComboBox MenuIcon);
}
