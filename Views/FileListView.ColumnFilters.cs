using MacExplorer.ViewModels;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MacExplorer.Assets;
using MacExplorer.Models;

namespace MacExplorer.Views;

public partial class FileListView
{
    private SortField _openFilterColumn;
    private Button? _filterAnchor;
    private bool _updatingFilterOptions;

    private void UpdateSortHeaders()
    {
        UpdateHeader(SortField.Name, "名称", NameSortButton, NameSortIcon, NameFilterButton, NameFilterIcon);
        UpdateHeader(SortField.Modified, "修改日期", ModifiedSortButton, ModifiedSortIcon, ModifiedFilterButton, ModifiedFilterIcon);
        UpdateHeader(SortField.Size, "大小", SizeSortButton, SizeSortIcon, SizeFilterButton, SizeFilterIcon);
        UpdateHeader(SortField.Type, "类型", TypeSortButton, TypeSortIcon, TypeFilterButton, TypeFilterIcon);
    }

    private void UpdateHeader(SortField field, string label, Button sort, PathIcon direction, Button filter, PathIcon filterIcon)
    {
        var active = ViewModel?.SortField == field;
        var ascending = ViewModel?.SortAscending != false;
        sort.Classes.Set("sorted", active);
        direction.Data = active ? ascending ? FileListHeaderIcons.SortUp : FileListHeaderIcons.SortDown : FileListHeaderIcons.Sort;
        var sortTip = active ? $"{label} · {(ascending ? "正序" : "倒序")}，点击切换" : $"按{label}正序排列";
        ToolTip.SetTip(sort, sortTip);
        AutomationProperties.SetName(sort, sortTip);

        var count = ViewModel?.ColumnFilters.Selection(field).Count ?? 0;
        var hasName = field == SortField.Name && !string.IsNullOrWhiteSpace(ViewModel?.FileNameFilter);
        var filtered = count > 0 || hasName;
        filter.Classes.Set("filtered", filtered);
        filterIcon.Data = filtered ? FileListHeaderIcons.Filter : FileListHeaderIcons.ChevronDown;
        var tip = filtered ? $"筛选{label} · 已选 {count} 项{(hasName ? "，含文件名条件" : "")}" : $"筛选{label}";
        ToolTip.SetTip(filter, tip);
        AutomationProperties.SetName(filter, tip);
    }

    private void OnHeaderSortClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SortField field }) ViewModel?.SetSort(field);
        e.Handled = true;
    }

    private void OnColumnFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SortField field } anchor || ViewModel == null) return;
        if (ColumnFilterPopup.IsOpen && _filterAnchor == anchor)
        {
            ColumnFilterPopup.IsOpen = false;
            return;
        }

        ColumnFilterPopup.IsOpen = false;
        ViewModel.NotifyTransientInteractionStarted();
        _openFilterColumn = field;
        _filterAnchor = anchor;
        ColumnFilterTitle.Text = field switch
        {
            SortField.Name => "名称 · 首字母范围",
            SortField.Modified => "修改日期 · 日期分组",
            SortField.Size => "大小 · 大小范围",
            _ => "类型 · 文件后缀"
        };
        RefreshColumnFilterOptions(rebuild: true);
        ColumnFilterPopup.PlacementTarget = anchor;
        ColumnFilterPopup.IsOpen = true;
        anchor.Classes.Set("filter-open", true);
        ColumnFilterOptions.Children.OfType<CheckBox>().FirstOrDefault()?.Focus();
        e.Handled = true;
    }

    private void RefreshColumnFilterOptions(bool rebuild = false)
    {
        if (ViewModel == null || !rebuild && !ColumnFilterPopup.IsOpen) return;
        var options = ViewModel.GetColumnFilterOptions(_openFilterColumn);
        _updatingFilterOptions = true;
        try
        {
            var checks = ColumnFilterOptions.Children.OfType<CheckBox>().ToArray();
            if (rebuild || !checks.Select(check => (string)check.Tag!).SequenceEqual(options.Select(option => option.Key)))
            {
                ColumnFilterOptions.Children.Clear();
                foreach (var option in options)
                {
                    var label = new TextBlock { Text = option.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                    var count = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, Margin = new Thickness(12, 0, 0, 0) };
                    var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
                    content.Children.Add(label);
                    Grid.SetColumn(count, 1);
                    content.Children.Add(count);
                    var check = new CheckBox { Tag = option.Key, Content = content, IsChecked = option.IsSelected };
                    check.Classes.Add("file-filter-option");
                    check.IsCheckedChanged += OnColumnFilterOptionChanged;
                    ColumnFilterOptions.Children.Add(check);
                }
                checks = ColumnFilterOptions.Children.OfType<CheckBox>().ToArray();
            }
            for (var index = 0; index < options.Count; index++)
            {
                var option = options[index];
                var check = checks[index];
                check.IsChecked = option.IsSelected;
                ((TextBlock)((Grid)check.Content!).Children[1]).Text = option.Count.ToString("N0");
                AutomationProperties.SetName(check, $"{option.Label}，{option.Count} 项");
            }
            ColumnFilterResult.Text = $"显示 {ViewModel.Entries.Count:N0} 项";
        }
        finally { _updatingFilterOptions = false; }
    }

    private void OnColumnFilterOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (!_updatingFilterOptions && sender is CheckBox { Tag: string key } check)
            ViewModel?.SetColumnFilter(_openFilterColumn, key, check.IsChecked == true);
    }

    private void OnClearColumnFilter(object? sender, RoutedEventArgs e) => ViewModel?.ClearColumnFilter(_openFilterColumn);
    private void OnClearAllColumnFilters(object? sender, RoutedEventArgs e) => ViewModel?.ClearFileListFilters();

    private void OnColumnFilterDone(object? sender, RoutedEventArgs e)
    {
        ColumnFilterPopup.IsOpen = false;
        _filterAnchor?.Focus();
    }

    private void OnColumnFilterClosed(object? sender, EventArgs e) => _filterAnchor?.Classes.Set("filter-open", false);

    private void OnColumnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ColumnFilterPopup.IsOpen = false;
            _filterAnchor?.Focus();
        }
        // Keep file operations and FastList navigation out of the checkbox popup.
        e.Handled = e.Key != Key.Tab;
    }
}
