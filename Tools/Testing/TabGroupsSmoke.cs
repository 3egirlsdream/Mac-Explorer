#if DEBUG
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class TabGroupsSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot!;
        var output = Path.Combine(root, "tab-groups");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        MainWindow? restoredWindow = null;
        try
        {
            if (OperatingSystem.IsMacOS()) FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            var folders = Enumerable.Range(0, 8).Select(i => Directory.CreateDirectory(
                Path.Combine(root, "Documents", "Groups", $"{i + 1:00} Project")).FullName).ToArray();
            foreach (var folder in folders)
                await File.WriteAllTextAsync(Path.Combine(folder, "Example.txt"), "Tab group fixture");
            var model = (MainWindowViewModel)owner.DataContext!;
            await model.FileList.NavigateToAsync(folders[0]);
            for (var i = 1; i < 8; i++) await owner.OpenNewTabAsync(i == 4 ? folders[0] : folders[i]);
            var tabs = model.Tabs.ToArray();
            var first = model.CreateGroup(tabs.Take(5));
            first.Name = "开发";
            model.ActivateGroup(first);
            await owner.SetPaneLayoutAsync(PaneLayout.TwoColumns);
            model.ActivePaneSlotIndex = 1;
            model.SelectedTab = tabs[4];
            var panes = model.VisiblePanes.ToArray();
            Require(model.Members(first).Count() == 5 && panes.Length == 2, "More members than visible panes");
            var second = model.CreateGroup(tabs.Skip(5).Take(2));
            second.Name = "素材";
            model.ActivateGroup(second);
            await owner.SetPaneLayoutAsync(PaneLayout.TwoRows);
            model.ActivateGroup(first);
            Require(model.VisiblePanes.SequenceEqual(panes) && model.ActivePaneSlotIndex == 1, "Group switching restores layout and active slot");
            model.MoveStripItem(second, first, false);
            Require(model.VisiblePanes.SequenceEqual(panes) && model.SelectedTab == tabs[4]
                && tabs.Take(5).SequenceEqual(model.Members(first)), "Whole-group reorder preserves members, panes and selection");
            model.ToggleGroup(first);
            Require(model.VisiblePanes.SequenceEqual(panes) && model.SelectedTab == tabs[4]
                && model.SelectedStripItem == first, "Collapse retains the visible split and selection");
            await CaptureAsync(owner, "collapsed-light");
            model.ToggleGroup(first);
            var selectedBeforeCapture = model.SelectedTab;
            model.SelectedTab = tabs[0];
            await CaptureAsync(owner, "first-tab-active");
            model.SelectedTab = selectedBeforeCapture;
            owner.UpdateLayout();
            var strip = owner.FindControl<ListBox>("TabList")!;
            var groupHeader = (Control)strip.ContainerFromItem(first)!;
            var previousHeader = (Control)strip.ContainerFromIndex(strip.Items.IndexOf(first) - 1)!;
            Require(groupHeader.Margin.Left == 8
                && Math.Abs(groupHeader.Bounds.Left - previousHeader.Bounds.Right - 8) <= 1,
                "The native strip keeps an eight-pixel gap between colored groups");
            var accents = strip.GetVisualDescendants().OfType<TabGroupLine>().ToArray();
            Require(accents.All(line => line.Height == 2), "Group accent lines use the shared two-pixel style");
            Require(accents.Where(line => line.PreviewGroup != null).All(line => ReferenceEquals(line.Background, line.PreviewGroup!.ColorBrush)),
                "Settled group accent colors are applied without a pending transition");
            var panel = strip.GetVisualDescendants().OfType<AdaptiveTabPanel>().Single();
            var draggedHeader = (Control)strip.ContainerFromItem(tabs[3])!;
            var draggedLine = draggedHeader.GetVisualDescendants().OfType<TabGroupLine>().Single();
            var dragSurface = draggedHeader.GetVisualDescendants().OfType<TabDragSurface>().Single();
            var outsideHeader = (Control)strip.ContainerFromItem(tabs[7])!;
            var targetHeader = (Control)strip.ContainerFromItem(second)!;
            if (!MacWindowChrome.PrefersReducedMotion)
            {
                var layoutBefore = panel.ArrangeVersion;
                var motion = owner.TabGroupMotion;
                var framesBefore = motion.RenderedFrames;
                motion.Toggle(first);
                Require(motion.IsRunning && first.IsCollapsed && model.VisiblePanes.SequenceEqual(panes),
                    "Group collapse commits the header state without switching panes");
                var layoutDuring = panel.ArrangeVersion;
                await Task.Delay(70);
                Require(panel.ArrangeVersion == layoutDuring && layoutDuring <= layoutBefore + 2,
                    "The group slide does not run layout on each frame");
                Require(motion.RenderedFrames > framesBefore + 1, "The compositor draws successive group slide frames");
                motion.Toggle(first);
                Require(motion.IsRunning && !first.IsCollapsed, "An in-flight slide can reverse");
                using var motionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                while (motion.IsRunning) await Task.Delay(10, motionTimeout.Token);
                Require(model.VisiblePanes.SequenceEqual(panes), "Reversing the group slide preserves visible panes");

                motion.Toggle(first);
                await Task.Delay(35, motionTimeout.Token);
                await CaptureAsync(owner, "group-motion-middle", waitForTheme: false);
                while (motion.IsRunning) await Task.Delay(10, motionTimeout.Token);
                motion.Toggle(first);
                while (motion.IsRunning) await Task.Delay(10, motionTimeout.Token);
                owner.UpdateLayout();
                draggedHeader = (Control)strip.ContainerFromItem(tabs[3])!;
                draggedLine = draggedHeader.GetVisualDescendants().OfType<TabGroupLine>().Single();
                dragSurface = draggedHeader.GetVisualDescendants().OfType<TabDragSurface>().Single();
                outsideHeader = (Control)strip.ContainerFromItem(tabs[7])!;
                targetHeader = (Control)strip.ContainerFromItem(second)!;
            }
            var outsideX = outsideHeader.TranslatePoint(new Point(outsideHeader.Bounds.Width / 2, 0), panel)!.Value.X;
            var targetX = targetHeader.TranslatePoint(new Point(targetHeader.Bounds.Width / 2, 0), panel)!.Value.X;
            dragSurface.IsVisible = true;
            try
            {
                panel.PreviewDrag(strip.Items.IndexOf(tabs[3]), Math.Max(0, outsideX - 35), outsideX, false);
                await CaptureAsync(owner, "drag-outside-preview");
                Require(draggedLine.PreviewGroup == null && tabs[3].Group == first, "Drag-out preview clears the accent without changing membership");
                panel.PreviewDrag(strip.Items.IndexOf(tabs[3]), Math.Max(0, targetX - 35), targetX, false);
                await CaptureAsync(owner, "drag-into-group-preview");
                Require(draggedLine.PreviewGroup == second && tabs[3].Group == first, "Cross-group preview follows the target color without switching content");
            }
            finally { dragSurface.IsVisible = false; panel.EndDrag(); owner.UpdateLayout(); }
            Require(model.VisiblePanes.SequenceEqual(panes), "Cancelling the drag preview preserves the visible panes");
            var language = App.Services.GetRequiredService<ILocalizationService>();
            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(id);
                foreach (var dark in new[] { false, true })
                {
                    owner.MinWidth = 680;
                    owner.Width = dark ? 800 : 1280;
                    owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await CaptureAsync(owner, $"{id}-{(dark ? "dark-narrow" : "light-wide")}");
                    if (dark && id == "zh-CN" && !MacWindowChrome.PrefersReducedMotion)
                    {
                        var motion = owner.TabGroupMotion;
                        motion.Toggle(first);
                        if (motion.IsRunning)
                        {
                            await Task.Delay(35);
                            await CaptureAsync(owner, "group-motion-dark-narrow", waitForTheme: false);
                            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                            while (motion.IsRunning) await Task.Delay(10, timeout.Token);
                        }
                        model.ToggleGroup(first);
                    }
                }
                owner.ShowGroupContextMenu((Control)owner.FindControl<ListBox>("TabList")!.ContainerFromItem(first)!, first);
                var menu = (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;
                await CaptureAsync(menu, $"{id}-group-menu");
                Require(menu.Items.OfType<MenuItem>().All(item => !Equals(item.Header, "颜色") && !Equals(item.Header, "Color")), "Group menu omits color editing: " + id);
                menu.Close();
            }
            language.SetLanguage("zh-CN");
            owner.Width = 1280;
            owner.RequestedThemeVariant = ThemeVariant.Light;
            second.Name = "A long group name / 很长的组名，用于检查标题栏裁切和完整名称提示";
            await CaptureAsync(owner, "long-group-name");
            second.Name = "素材";
            var settings = App.Services.GetRequiredService<ISettingsService>();
            settings.Set(FolderTabSession.EnabledKey, true);
            var saved = owner.CaptureFolderTabs();
            FolderWindowSession.Capture([saved], 0).Save(settings);
            var filtered = FolderWindowSession.Read(settings, MainWindow.CanRestoreFolder)!.Windows[0];
            restoredWindow = App.OpenNewWindow();
            await Task.Delay(250);
            await restoredWindow.RestoreFolderTabSessionAsync(filtered);
            var captured = restoredWindow.CaptureFolderTabs();
            Require(JsonSerializer.Serialize(saved) == JsonSerializer.Serialize(captured), "Native window roundtrip preserves duplicate folders, group colors and pane mapping");
            await CaptureAsync(restoredWindow, "restored");
            restoredWindow.Close(); restoredWindow = null;
            using (owner.BlockModalParentInteraction())
            {
                var dialog = new TabGroupNameDialog(first.Name);
                var closed = dialog.ShowDialog<string?>(owner);
                await CaptureAsync(dialog, "rename-group");
                dialog.Close();
                await closed;
            }
            var theme = App.Services.GetRequiredService<BackgroundThemeService>();
            await theme.SelectPresetAsync("animal-panda");
            await CaptureAsync(owner, "background-theme");
            Require(first.ColorIndex != second.ColorIndex, "Adjacent groups receive different stable colors");
            await File.WriteAllTextAsync(Path.Combine(output, "session.json"), JsonSerializer.Serialize(saved));
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            restoredWindow?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Tab groups smoke: {output}; success={error == null}");
            if (Environment.GetEnvironmentVariable("MACEXPLORER_TAB_GROUPS_SMOKE") != "interactive"
                && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }

        void Require(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }

        async Task CaptureAsync(Control control, string name, bool waitForTheme = true)
        {
            if (waitForTheme)
            {
                control.UpdateLayout();
                await Task.Delay(180);
                var theme = WindowBackgroundTheme.GetController(control);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while (theme?.IsRendering == true) await Task.Delay(20, timeout.Token);
            }
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }
}
#endif
