using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Icons = MacExplorer.Assets.Icons;

namespace MacExplorer.Controls;

// Builds infinite cascading native-style MenuItems for file collection assignment.
internal static class CollectionAssignmentMenu
{
    internal static void Attach(
        MenuItem rootMenuItem,
        FileListViewModel viewModel,
        IReadOnlyList<string> filePaths,
        Visual ownerAnchor,
        Action dismissMenu)
    {
        var membership = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var checkMarks = new Dictionary<string, (PathIcon Full, TextBlock Partial)>(StringComparer.OrdinalIgnoreCase);
        CancellationTokenSource? loadCts = null;
        var busy = false;

        void OpenManager()
        {
            dismissMenu();
            if (viewModel.FileTagService is not { } service) return;
            Dispatcher.UIThread.Post(() =>
                _ = CollectionManagerDialog.OpenAsync(service, ownerAnchor, tag => viewModel.NavigateToTagAsync(tag)));
        }

        void UpdateVisualMarks()
        {
            foreach (var (name, marks) in checkMarks)
            {
                var count = membership.GetValueOrDefault(name);
                marks.Full.IsVisible = filePaths.Count > 0 && count == filePaths.Count;
                marks.Partial.IsVisible = count > 0 && count != filePaths.Count;
            }
        }

        async Task ToggleTagAsync(FileTag tag)
        {
            if (busy || viewModel.FileTagService is not { } service) return;
            busy = true;
            try
            {
                var currentCount = membership.GetValueOrDefault(tag.Name);
                var apply = currentCount != filePaths.Count;
                await service.SetTagAsync(filePaths, tag, apply);
                membership[tag.Name] = apply ? filePaths.Count : 0;
                UpdateVisualMarks();
            }
            catch (Exception ex)
            {
                viewModel.StatusText = LocalizationText.Get("标签操作失败：{0}", ex.Message);
            }
            finally
            {
                busy = false;
            }
        }

        MenuItem BuildNodeItem(FileTagNode node)
        {
            var tag = node.Tag;
            var icon = TagTreePanel.Icon(FileTagTree.IconGeometry(node), 14);
            if (tag.ColorId != 0)
                icon.Foreground = Brush.Parse(tag.ColorHex);

            var label = new TextBlock
            {
                Text = tag.DisplayName,
                MaxWidth = 220,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };

            var count = membership.GetValueOrDefault(tag.Name);
            var check = TagTreePanel.Icon(Icons.Checkmark, 12);
            check.Classes.Add("tag-tree-check");
            check.VerticalAlignment = VerticalAlignment.Center;
            check.IsVisible = filePaths.Count > 0 && count == filePaths.Count;

            var partial = new TextBlock
            {
                Text = "−",
                Classes = { "tag-tree-partial" },
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsVisible = count > 0 && count != filePaths.Count
            };
            checkMarks[tag.Name] = (check, partial);

            var headerGrid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,18"),
                ColumnSpacing = 8
            };
            headerGrid.Children.Add(label);
            Grid.SetColumn(check, 1);
            headerGrid.Children.Add(check);
            Grid.SetColumn(partial, 1);
            headerGrid.Children.Add(partial);

            var item = new MenuItem
            {
                Header = headerGrid,
                Icon = icon,
                Tag = tag,
                StaysOpenOnClick = true
            };
            AutomationProperties.SetName(item, tag.DisplayName);
            ToolTip.SetTip(item, tag.DisplayName);
            ContextMenuPopupStyler.Attach(item);

            foreach (var child in node.Children)
                item.Items.Add(BuildNodeItem(child));

            if (node.Children.Count == 0)
            {
                item.Click += async (_, e) =>
                {
                    if (!ReferenceEquals(e.Source, item)) return;
                    e.Handled = true;
                    await ToggleTagAsync(tag);
                };
            }
            else
            {
                // Parent stays toggleable on direct click while standard MenuItem owns infinite hover cascading.
                item.AddHandler(InputElement.PointerReleasedEvent, async (_, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Left) return;
                    var closest = e.Source as MenuItem ?? (e.Source as Visual)?.FindAncestorOfType<MenuItem>();
                    if (!ReferenceEquals(closest, item)) return;
                    e.Handled = true;
                    await ToggleTagAsync(tag);
                }, RoutingStrategies.Tunnel);

                item.AddHandler(InputElement.KeyDownEvent, async (_, e) =>
                {
                    if (e.Key != Key.Enter || !ReferenceEquals(e.Source, item)) return;
                    e.Handled = true;
                    await ToggleTagAsync(tag);
                }, RoutingStrategies.Tunnel);
            }

            return item;
        }

        void Populate(IReadOnlyList<FileTag> tags)
        {
            checkMarks.Clear();
            rootMenuItem.Items.Clear();

            var header = new TagMenuHeader(LocalizationText.Get("收藏夹"), OpenManager);
            rootMenuItem.Items.Add(new MenuItem
            {
                Focusable = false,
                StaysOpenOnClick = true,
                Template = new FuncControlTemplate<MenuItem>((_, _) => header)
            });
            rootMenuItem.Items.Add(new Separator());

            var roots = FileTagTree.Build(tags);
            if (roots.Count == 0)
            {
                rootMenuItem.Items.Add(new MenuItem
                {
                    Header = LocalizationText.Get("tags.empty"),
                    IsEnabled = false
                });
                return;
            }

            foreach (var node in roots)
                rootMenuItem.Items.Add(BuildNodeItem(node));
        }

        // Seed synchronously so hovering opens immediately with no flicker.
        Populate(viewModel.SidebarTags.ToArray());

        async Task RefreshMembershipAsync(CancellationToken ct)
        {
            var service = viewModel.FileTagService;
            if (service == null) return;
            try
            {
                var selection = await service.GetFileTagSelectionAsync(filePaths, ct);
                if (ct.IsCancellationRequested) return;
                membership.Clear();
                foreach (var pair in selection.Membership)
                    membership[pair.Key] = pair.Value;

                var existingNames = checkMarks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var freshNames = selection.Tags.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (existingNames.SetEquals(freshNames))
                    UpdateVisualMarks();
                else
                    Populate(selection.Tags);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                viewModel.StatusText = LocalizationText.Get("标签操作失败：{0}", ex.Message);
            }
        }

        rootMenuItem.PropertyChanged += (_, e) =>
        {
            if (e.Property != MenuItem.IsSubMenuOpenProperty) return;
            loadCts?.Cancel();
            loadCts?.Dispose();
            loadCts = null;
            if (!rootMenuItem.IsSubMenuOpen) return;
            loadCts = new CancellationTokenSource();
            _ = RefreshMembershipAsync(loadCts.Token);
        };
    }
}
