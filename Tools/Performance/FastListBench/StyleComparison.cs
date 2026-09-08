using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Views;

internal static class StyleComparison
{
    internal static async Task RunAsync(Window window, string output)
    {
        window.Width = 1280;
        window.Height = 680;
        var entries = new[] { "Readme.txt", "一个比较长的中文文件名称.txt", "项目资料", "a-very-long-file-name.csproj", "短名称.txt", "人物相册" }
            .Select((name, i) => new FileSystemEntry
            {
                FullPath = $"/style-fixture/{name}", Name = name, IsDirectory = i is 2 or 5,
                Extension = i is 2 or 5 ? "" : Path.GetExtension(name), IconKey = i is 2 or 5 ? "folder" : "file-text",
                Size = 1234, LastModified = new DateTime(2026, 9, 8, 10, 20, 0),
                IsSelected = i is 0 or 1 or 5, IsCut = i == 1,
                IsVirtual = i == 5, VirtualItemCount = 123, GitStatus = i == 3 ? GitFileStatus.Modified : GitFileStatus.None
            }).ToArray();
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        foreach (var grid in new[] { false, true })
        foreach (var grouped in new[] { false, true })
        {
            window.RequestedThemeVariant = theme;
            var left = new FileListView();
            var right = new FileListView();
            var pair = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
            pair.Children.Add(left);
            pair.Children.Add(right);
            Grid.SetColumn(right, 1);
            window.Content = pair;
            await Task.Delay(50);
            var groups = grouped ? new[] { new FastFileListGroup("文档", 3), new FastFileListGroup("文件夹", 3) } : [];
            SetLegacy(left, entries, grid, grouped);
            right.FindControl<Border>("ListHeaderPanel")!.IsVisible = !grid;
            right.FindControl<ListBox>("FileItemsList")!.IsVisible = false;
            right.FindControl<ScrollViewer>("FastListHost")!.IsVisible = true;
            var fast = right.FindControl<FastFileList>("FastList")!;
            fast.IsGrid = grid;
            fast.SetRows(entries, groups);
            await Task.Delay(350);
            foreach (var host in left.GetVisualDescendants().OfType<ListBox>())
                foreach (var entry in host.Items.OfType<FileSystemEntry>().Where(e => e.IsSelected))
                    host.SelectedItems!.Add(entry);
            var name = $"{theme}-{(grid ? "grid" : "details")}{(grouped ? "-grouped" : "")}";
            Save(left, Path.Combine(output, name + "-old.png"));
            Save(right, Path.Combine(output, name + "-new.png"));
            var controls = left.GetVisualDescendants().OfType<Control>()
                .Where(c => c is TextBlock || c.Classes.Contains("file-grid-icon-target") || c.Classes.Contains("file-grid-name-target")
                    || c.Classes.Contains("file-grid-content") || c.Classes.Contains("file-list-entry") || c.Classes.Contains("file-group-row"))
                .Select(c => new
                {
                    type = c.GetType().Name, classes = string.Join(" ", c.Classes), entry = (c.DataContext as FileSystemEntry)?.Name,
                    rect = new Rect(c.TranslatePoint(default, left) ?? default, c.Bounds.Size).ToString(),
                    text = (c as TextBlock)?.Text, fontSize = (c as TextBlock)?.FontSize, fontWeight = (c as TextBlock)?.FontWeight.ToString(),
                    foreground = (c as TextBlock)?.Foreground?.ToString()
                });
            var report = new { legacy = controls.ToArray(), fastRows = entries.Select((e, i) => new { entryName = e.Name, row = fast.RowBounds(i).ToString(), nameBounds = fast.NameBounds(i).ToString() }) };
            await File.WriteAllTextAsync(Path.Combine(output, name + ".json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void SetLegacy(FileListView view, FileSystemEntry[] entries, bool grid, bool grouped)
    {
        view.FindControl<Border>("ListHeaderPanel")!.IsVisible = !grid;
        var plain = view.FindControl<ListBox>("FileItemsList")!;
        plain.IsVisible = !grid && !grouped;
        if (plain.IsVisible) { plain.ItemsSource = entries; return; }
        if (grid)
        {
            var rows = new List<FileGridPresentationRow>();
            for (var g = 0; g < (grouped ? 2 : 1); g++)
            {
                var section = grouped ? entries.Skip(g * 3).Take(3).ToArray() : entries;
                if (grouped) rows.Add(new FileGridPresentationRow { GroupName = g == 0 ? "文档" : "文件夹", GroupItemCount = section.Length });
                rows.AddRange(section.Chunk(5).Select(slice => new FileGridPresentationRow { Entries = slice }));
            }
            var host = view.FindControl<ListBox>("GridViewItems")!;
            host.IsVisible = true;
            host.ItemsSource = rows;
        }
        else
        {
            var rows = new List<FileListPresentationRow>();
            for (var g = 0; g < 2; g++)
            {
                rows.Add(new FileListPresentationRow { GroupName = g == 0 ? "文档" : "文件夹", GroupItemCount = 3 });
                rows.AddRange(entries.Skip(g * 3).Take(3).Select(entry => new FileListPresentationRow { Entry = entry }));
            }
            var host = view.FindControl<ListBox>("GroupedListItems")!;
            host.IsVisible = true;
            host.ItemsSource = rows;
        }
    }

    private static void Save(Control control, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)control.Bounds.Width, (int)control.Bounds.Height));
        bitmap.Render(control);
        bitmap.Save(path);
    }
}
