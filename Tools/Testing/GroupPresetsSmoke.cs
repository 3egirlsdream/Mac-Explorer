#if DEBUG
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Services;
using MacExplorer.Controls;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class GroupPresetsSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "group-presets");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            var model = (MainWindowViewModel)owner.DataContext!;
            var settings = App.Services.GetRequiredService<ISettingsService>();
            var language = App.Services.GetRequiredService<ILocalizationService>();
            settings.Set(FolderTabSession.EnabledKey, false);
            language.SetLanguage("zh-CN");
            var folders = new[] { "资料", "项目", "文档", "下载" }.Select(name =>
                Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", name)).FullName).ToArray();
            foreach (var folder in folders)
            {
                Directory.CreateDirectory(Path.Combine(folder, "产品设计"));
                await File.WriteAllTextAsync(Path.Combine(folder, "需求说明.md"), "# 工作组预设\n保存常用目录与布局。");
                await File.WriteAllTextAsync(Path.Combine(folder, "README.md"), "# Project\nExample workspace.");
            }
            await model.FileList.NavigateToAsync(folders[0]);
            var original = model.SelectedTab!;
            foreach (var path in folders.Skip(1)) await owner.OpenNewTabAsync(path);
            var group = model.CreateGroup(model.Tabs.Skip(1).ToArray());
            group.Name = "开发环境";
            model.ActivateGroup(group);
            await owner.SetPaneLayoutAsync(PaneLayout.TwoColumns);
            model.SelectedTab = model.Members(group).ElementAt(1);
            var button = owner.FindControl<Button>("GroupPresetsButton")!;
            var add = owner.FindControl<Button>("NewTabButton")!;
            var layout = owner.FindControl<Button>("PaneLayoutButton")!;
            var strip = owner.FindControl<ListBox>("TabList")!;
            owner.UpdateLayout();
            owner.ShowGroupContextMenu((Control)strip.ContainerFromItem(group)!, group);
            await Capture(Menu(), "01-save-group-menu");
            Click(Item(Menu(), "tabs.preset.save"));
            var dialog = await NameDialog();
            await Capture(dialog, "02-save-name");
            Submit(dialog, "开发环境");
            await WaitUntil(() => TabGroupPresets.Read(settings).Length == 1);
            var preset = TabGroupPresets.Read(settings)[0];
            Check(preset.Session.Paths.SequenceEqual(folders.Skip(1)), "Saving from the group menu records ordered folder tabs");
            TabGroupPresets.Save(settings, preset with { Id = Guid.NewGuid(), Name = "素材整理" });
            TabGroupPresets.Save(settings, preset with { Id = Guid.NewGuid(), Name = "财务资料" });

            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(id);
                foreach (var dark in new[] { false, true })
                {
                    owner.MinWidth = 680;
                    owner.Width = dark ? 800 : 1280;
                    owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    model.SelectedTab = original;
                    OpenMenu();
                    Check(button.Classes.Contains("active") && !Item(Menu(), "tabs.preset.save").IsEnabled,
                        $"{id}/{dark}: active toolbar entry and disabled save outside a group");
                    await Capture(Menu(), $"{id}-{(dark ? "dark-narrow" : "light-wide")}-menu");
                    if (id == "zh-CN")
                    {
                        var manage = Item(Menu(), "tabs.preset.manage");
                        manage.IsSubMenuOpen = true;
                        await Task.Delay(150);
                        await Capture(manage.GetVisualDescendants().OfType<Popup>().Single().Child!, $"{id}-{dark}-manage-list");
                        var entry = (MenuItem)manage.Items[0]!;
                        entry.IsSubMenuOpen = true;
                        await Task.Delay(150);
                        await Capture(entry.GetVisualDescendants().OfType<Popup>().Single().Child!, $"{id}-{dark}-manage-actions");
                    }
                    Menu().Close();
                }
            }
            language.SetLanguage("zh-CN");
            owner.RequestedThemeVariant = ThemeVariant.Light;
            owner.Width = 1280;
            await owner.OpenGroupPresetAsync(preset);
            Check(model.Groups.Count == 1 && model.SelectedTab!.Group == group && model.Tabs.Count == 4,
                "Opening a newly saved preset activates its existing source group without duplicating tabs");
            owner.UpdateLayout();
            Check(new[] { add, button, layout }.All(item => item.Bounds.Size == new Size(32, 32)
                && item.Margin == new Thickness(item == add ? 6 : 2, 6, 2, 6)
                && item.TranslatePoint(default, item.FindAncestorOfType<WindowTitleBar>()!)!.Value.Y == 6
                && item.TranslatePoint(default, owner)!.Value.Y == layout.TranslatePoint(default, owner)!.Value.Y),
                "New tab, group presets and split buttons share dimensions and vertical position with extra leading space for the new-tab button");
            await Capture(owner, "03-matching-title-buttons");
            owner.Width = 800;
            foreach (var dark in new[] { false, true })
            {
                owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                await Capture(owner.GetVisualDescendants().OfType<WindowTitleBar>().Single(),
                    $"03-title-controls-{(dark ? "dark" : "light")}");
            }
            owner.RequestedThemeVariant = ThemeVariant.Light;
            owner.Width = 1280;
            await owner.CloseGroupAsync(group);
            OpenMenu();
            Click((MenuItem)Menu().Items[0]!);
            await WaitUntil(() => model.Groups.Count == 1 && !owner.IsRestoringFolderTabs);
            var opened = model.SelectedTab!.Group!;
            Check(opened.Id != group.Id && !opened.IsCollapsed && model.PaneLayout == PaneLayout.TwoColumns
                && model.FileList.CurrentPath == preset.Session.Paths[preset.Session.SelectedIndex],
                "Opening appends an independent expanded group with its saved split and active folder");
            await Capture(owner, "03-opened-workspace");
            model.ToggleGroup(opened);
            model.SelectedTab = original;
            await owner.OpenGroupPresetAsync(preset);
            Check(model.Groups.Count == 1 && model.Tabs.Count == 4 && model.SelectedTab!.Group == opened && !opened.IsCollapsed,
                "Repeated opening expands and activates the existing group without adding tabs");
            OpenMenu();
            await Capture(owner, "03-active-toolbar");
            Menu().Close();

            OpenMenu();
            Click(Item(Menu(), "tabs.preset.save"));
            Submit(await NameDialog(), "开发环境");
            await WaitUntil(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any());
            var confirm = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Capture(confirm, "04-replace-confirmation");
            confirm.Close(true);
            await WaitUntil(() => !owner.OwnedWindows.Any());
            Check(TabGroupPresets.Read(settings).Length == 3, "Same-name replacement keeps a single preset");

            OpenMenu();
            Click(Item((MenuItem)Item(Menu(), "tabs.preset.manage").Items[0]!, "tabs.preset.rename"));
            dialog = await NameDialog();
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "开发环境 · 常用项目与文档";
            await Capture(dialog, "05-rename-preset");
            Submit(dialog, "开发环境 · 常用项目与文档");
            await WaitUntil(() => TabGroupPresets.Read(settings)[0].Name != "开发环境");
            Check(opened.Name == "开发环境", "Renaming a preset leaves opened groups unchanged");
            OpenMenu();
            Click(Item((MenuItem)Item(Menu(), "tabs.preset.manage").Items[0]!, "tabs.preset.delete"));
            await WaitUntil(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any());
            confirm = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
            await Capture(confirm, "06-delete-confirmation");
            confirm.Close(true);
            await WaitUntil(() => TabGroupPresets.Read(settings).Length == 2);
            Check(model.Groups.Contains(opened) && folders.All(Directory.Exists)
                && !settings.Get(FolderTabSession.EnabledKey, true),
                "Deleting preserves open groups, source folders and the disabled automatic restore setting");

            ContextMenu Menu() => (ContextMenu)typeof(MainWindow).GetField("_tabContextMenu",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
            void OpenMenu() => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            foreach (var dialog in owner.OwnedWindows.ToArray()) dialog.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Group presets smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        async Task<TabGroupNameDialog> NameDialog()
        {
            await WaitUntil(() => owner.OwnedWindows.OfType<TabGroupNameDialog>().Any());
            return owner.OwnedWindows.OfType<TabGroupNameDialog>().Single();
        }
        async Task Capture(Control control, string name)
        {
            control.UpdateLayout();
            await Task.Delay(200);
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static MenuItem Item(ItemsControl parent, string key) => parent.Items.OfType<MenuItem>()
        .Single(item => Equals(item.Header, LocalizationText.Get(key)));
    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    private static void Submit(TabGroupNameDialog dialog, string name)
    {
        dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = name;
        dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
#endif
