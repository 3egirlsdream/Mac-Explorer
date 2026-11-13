#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class SidebarGroupsSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "sidebar-groups");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        SettingsDialog? settings = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            owner.Width = 1280;
            owner.Height = 900;
            var model = (MainWindowViewModel)owner.DataContext!;
            var language = App.Services.GetRequiredService<ILocalizationService>();
            language.SetLanguage("zh-CN");
            var store = App.Services.GetRequiredService<IPinnedFolderService>();
            var folder = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "产品设计")).FullName;
            var reference = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "参考资料")).FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, "需求说明.md"), "# 自定义分组\n各组独立固定文件夹。");
            await model.FileList.NavigateToAsync(folder);
            settings = new SettingsDialog { DataContext = model.FileList };
            _ = settings.ShowDialog(owner);
            var tabs = settings.FindControl<TabControl>("SettingsTabs")!;
            var page = tabs.Items.OfType<LazyTabItem>().Single(item => item.PageKey == "sidebar");
            Check(!page.IsContentLoaded, "Sidebar settings remain lazy before selection");
            tabs.SelectedItem = page;
            settings.UpdateLayout();
            var add = page.FindContentControl<Button>("AddSidebarGroupButton")!;
            Click(add);
            var naming = settings.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            Input(naming).Text = "工作项目";
            await Capture(naming, "01-add-name");
            Save(naming);
            await WaitUntil(() => model.FileList.CustomGroups.Count == 1);
            var groupId = model.FileList.CustomGroups.Single().Id;
            await store.PinAsync(folder, "产品设计");
            await store.PinAsync(folder, "产品设计", groupId);
            await store.PinAsync(reference, "参考资料", groupId);
            await store.CreateGroupAsync("长名称分组 Long name for narrow sidebar layout");
            await WaitUntil(() => model.FileList.CustomGroups.Count == 2 && model.FileList.CustomGroups[0].Folders.Count == 2);
            settings.UpdateLayout();
            var list = page.FindContentControl<ItemsControl>("SidebarGroupsList")!;
            Check(list.ItemCount == 2, "Created groups appear immediately in settings");
            foreach (var code in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(code);
                foreach (var dark in new[] { false, true })
                {
                    settings.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    settings.Width = dark ? 780 : 940;
                    settings.UpdateLayout();
                    list.BringIntoView();
                    await Capture(settings, $"settings-{code}-{(dark ? "dark" : "light")}");
                    Check(Avalonia.Automation.AutomationProperties.GetName(add) == language.Get("sidebar.groups.add"),
                        $"{code}/{dark}: localized add action");
                }
            }
            language.SetLanguage("zh-CN");
            var rename = list.GetVisualDescendants().OfType<Button>().First();
            Click(rename);
            naming = settings.OwnedWindows.OfType<TabGroupNameDialog>().Single();
            Input(naming).Text = "开发资料";
            Save(naming);
            await WaitUntil(() => model.FileList.CustomGroups[0].Name == "开发资料");
            settings.Close();
            settings = null;
            await owner.OpenNewTabAsync(folder);
            await WaitUntil(() => model.FileList.CustomGroups.Count == 2);
            Check(await store.IsPinnedAsync(folder) && await store.IsPinnedAsync(folder, groupId),
                "The same folder is pinned independently in common locations and a custom group");
            var sidebar = owner.GetVisualDescendants().OfType<FinderSidebarView>().First(item => item.IsEffectivelyVisible);
            foreach (var dark in new[] { false, true })
            {
                owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                sidebar.Width = dark ? 180 : 240;
                await Capture(owner, $"sidebar-{(dark ? "dark-narrow" : "light")}");
            }
            await store.UnpinAsync(folder, groupId);
            await WaitUntil(() => model.FileList.CustomGroups[0].Folders.Count == 1);
            Check(await store.IsPinnedAsync(folder), "Removing one shortcut preserves common locations");
            await store.DeleteGroupAsync(groupId);
            await WaitUntil(() => model.FileList.CustomGroups.Count == 1);
            Check(Directory.Exists(folder) && File.Exists(Path.Combine(folder, "需求说明.md")), "Deleting a group preserves original folders and content");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            settings?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Sidebar groups smoke: {output}; success={error == null}");
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                lifetime.Shutdown(error == null ? 0 : 1);
        }
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        async Task Capture(Control control, string name)
        {
            control.UpdateLayout();
            await Task.Delay(200);
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            snapshot.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static TextBox Input(TabGroupNameDialog dialog) => dialog.GetVisualDescendants().OfType<TextBox>().Single();
    private static void Save(TabGroupNameDialog dialog) => Click(dialog.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SaveButton"));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Sidebar group update did not complete.");
            await Task.Delay(20);
        }
    }
}
#endif
