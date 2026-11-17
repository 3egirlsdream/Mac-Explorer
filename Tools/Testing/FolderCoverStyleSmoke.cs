#if DEBUG
using System.Text.Json;
using System.Runtime.InteropServices;
using Avalonia.VisualTree;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Input;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FolderCoverStyleSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "folder-cover-style");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        SettingsDialog? dialog = null;
        MainWindow? secondWindow = null;
        var interactive = Environment.GetEnvironmentVariable("MACEXPLORER_FOLDER_COVER_STYLE_SMOKE") == "interactive";
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            owner.Width = 1100; owner.Height = 750;
            var model = (MainWindowViewModel)owner.DataContext!;
            var store = App.Services.GetRequiredService<ISettingsService>();
            var language = App.Services.GetRequiredService<ILocalizationService>();
            language.SetLanguage("zh-CN");
            Check(!model.FileList.ShowFolderPhotoCovers && model.FileList.PhotoCoverStyle == FolderPhotoCoverStyle.Default,
                "Fresh profile preserves the existing disabled cover toggle and default style");
            var albums = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "照片文件夹")).FullName;
            Directory.CreateDirectory(Path.Combine(albums, "资料"));
            foreach (var album in new[] { "旅行", "摄影", "灵感" })
            {
                var folder = Directory.CreateDirectory(Path.Combine(albums, album)).FullName;
                var assets = new[] { "FolderCoverSamples/alpine-lake.jpg", "BackgroundThemes/Previews/nature-trellis.png", "BackgroundThemes/Previews/nature-marigold.png" };
                for (var i = 0; i < assets.Length; i++)
                {
                    using var input = AssetLoader.Open(new Uri("avares://MacExplorer/Assets/" + assets[i]));
                    using var file = File.Create(Path.Combine(folder, $"{i + 1}" + Path.GetExtension(assets[i])));
                    input.CopyTo(file);
                }
            }
            await model.FileList.NavigateToAsync(albums);
            model.FileList.SetViewMode(ViewMode.Grid);
            secondWindow = App.OpenNewWindow(albums);
            var secondModel = (MainWindowViewModel)secondWindow.DataContext!;
            await Task.Delay(400);
            await secondModel.FileList.NavigateToAsync(albums);
            secondModel.FileList.SetViewMode(ViewMode.Grid);
            dialog = OpenSettings();
            var page = DisplayPage(dialog);
            Check(!page.IsContentLoaded, "Display settings stay lazy before selection");
            dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem = page;
            await dialog.FolderCoverPreviewsReady.WaitAsync(TimeSpan.FromSeconds(20));
            var picker = page.FindContentControl<ListBox>("FolderCoverStylePicker")!;
            var toggle = page.FindContentControl<ToggleSwitch>("FolderPhotoCoversToggle")!;
            Check(picker.SelectedIndex == 0 && store.Get<string?>(FolderPhotoCoverStyles.SettingKey, null) == null,
                "Opening the settings selects default without writing a preference");
            Check(new[] { "DefaultFolderIconPreview", "DefaultFolderCoverPreview", "FrostedFolderIconPreview", "FrostedFolderCoverPreview", "SkeuomorphicFolderIconPreview", "SkeuomorphicFolderCoverPreview" }
                .All(name => page.FindContentControl<Image>(name)!.Source != null),
                "Each style shows a paired folder icon and photo cover from production renderers");
            await Capture(dialog, "settings-default");
            picker.SelectedIndex = 1;
            Check(model.FileList.PhotoCoverStyle == FolderPhotoCoverStyle.Frosted && !model.FileList.ShowFolderPhotoCovers,
                "Selecting frosted persists the style without enabling covers");
            Check(ReferenceEquals(SvgIconCache.GetFolderIcon(128), SvgIconCache.GetFolderIcon(128, FolderPhotoCoverStyle.Frosted)),
                "The shared folder icon cache switches to the matching frosted icon");
            await Task.Delay(500);
            await Capture(owner, "folders-frosted-no-covers");
            await Capture(dialog, "settings-frosted");
            dialog.RequestedThemeVariant = ThemeVariant.Dark; dialog.Width = 780;
            await Capture(dialog, "settings-frosted-dark-narrow");
            dialog.RequestedThemeVariant = ThemeVariant.Light; dialog.Width = 940;
            toggle.IsChecked = true;
            Check(model.FileList.PhotoCoverStyle == FolderPhotoCoverStyle.Frosted && model.FileList.ShowFolderPhotoCovers,
                "Frosted photo folders use the same landscape style while keeping the cover toggle independent");
            await Task.Delay(1000);
            await Capture(owner, "folders-frosted");
            toggle.IsChecked = false;
            await Click((Control)picker.Items[2]!);
            Check(picker.SelectedIndex == 2 && model.FileList.PhotoCoverStyle == FolderPhotoCoverStyle.Skeuomorphic
                && !model.FileList.ShowFolderPhotoCovers, "Mouse selects paper without enabling photo covers");
            Check(secondModel.FileList.PhotoCoverStyle == FolderPhotoCoverStyle.Skeuomorphic,
                "The other open window receives the paper style immediately");
            Check(secondWindow.GetVisualDescendants().OfType<FastFileList>().Any(list => list.FolderIconStyle == FolderPhotoCoverStyle.Skeuomorphic),
                "The second window's file list uses the matching plain paper icon");
            Check(ReferenceEquals(SvgIconCache.GetFolderIcon(128), SvgIconCache.GetFolderIcon(128, FolderPhotoCoverStyle.Skeuomorphic)),
                "Shared ordinary folder artwork is cached by paper style and size");
            await Capture(owner, "folders-paper-no-covers");
            toggle.IsChecked = true;
            Check(model.FileList.ShowFolderPhotoCovers, "The original cover toggle still controls cover visibility");
            dialog.UpdateLayout();
            ((Control)picker.Items[2]!).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Left });
            Check(picker.SelectedIndex == 1, "Left arrow selects the previous preview option");
            ((Control)picker.Items[1]!).RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Right });
            Check(picker.SelectedIndex == 2, "Right arrow selects the next preview option");
            foreach (var code in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(code);
                foreach (var dark in new[] { false, true })
                {
                    dialog.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    dialog.Width = dark ? 780 : 940;
                    dialog.Height = 740;
                    await Capture(dialog, $"settings-{code}-{(dark ? "dark-narrow" : "light")}");
                    Check(picker.SelectedIndex == 2 && Avalonia.Automation.AutomationProperties.GetName(picker) == language.Get("settings.folder-cover-style"),
                        $"{code}/{dark}: localized selector preserves the selected style");
                    Check(picker.Bounds.Width <= ((Control)picker.Parent!).Bounds.Width, $"{code}/{dark}: preview options fit available width");
                    Check(((Control)picker.Items[2]!).Bounds.Right <= picker.Bounds.Width,
                        $"{code}/{dark}: all three paired options stay inside the selector");
                }
            }
            language.SetLanguage("zh-CN");
            dialog.Close(); dialog = null;
            await Task.Delay(1500);
            await Capture(owner, "folders-paper");
            await Capture(secondWindow, "folders-paper-second-window");
            var entry = model.FileList.Entries.First(item => item.Name == "旅行");
            var paper = await model.FileList.GetFolderPhotoCoverAsync(entry, 256, CancellationToken.None);
            store.Set(FolderPhotoCoverStyles.SettingKey, "default");
            Check(ReferenceEquals(SvgIconCache.GetFolderIcon(128), SvgIconCache.GetFolderIcon(128, FolderPhotoCoverStyle.Default)),
                "Switching back restores both the system folder icon and default photo covers");
            var original = await model.FileList.GetFolderPhotoCoverAsync(entry, 256, CancellationToken.None);
            Check(paper != null && original != null && !paper.Bytes.SequenceEqual(original.Bytes),
                "The same folder produces different cover artwork after changing style");
            await Task.Delay(1500);
            await Capture(owner, "folders-default");
            store.Set(FolderPhotoCoverStyles.SettingKey, "skeuomorphic");
            model.FileList.IsInfoPanelVisible = true;
            model.FileList.SelectedEntries.Clear();
            model.FileList.SelectedEntries.Add(model.FileList.Entries.Single(item => item.Name == "资料"));
            await Task.Delay(800);
            var panel = owner.GetVisualDescendants().OfType<InfoPanelView>().Single(view => view.IsEffectivelyVisible);
            var infoIcon = panel.FindControl<Image>("PreviewFileIcon")!;
            Check(ReferenceEquals(infoIcon.Source, SvgIconCache.GetFolderIcon(128)), "Information panel borrows the shared paper folder icon");
            await Capture(owner, "folders-paper-info");
            store.Set(FolderPhotoCoverStyles.SettingKey, "default");
            Check(ReferenceEquals(infoIcon.Source, SvgIconCache.GetFolderIcon(128)), "Information panel refreshes when the folder style changes");
            store.Set(FolderPhotoCoverStyles.SettingKey, "skeuomorphic");
            dialog = OpenSettings();
            page = DisplayPage(dialog);
            dialog.FindControl<TabControl>("SettingsTabs")!.SelectedItem = page;
            await dialog.FolderCoverPreviewsReady;
            Check(page.FindContentControl<ListBox>("FolderCoverStylePicker")!.SelectedIndex == 2,
                "Reopening settings restores the saved paper selection");
            dialog.Width = 940; dialog.Height = 740; dialog.RequestedThemeVariant = ThemeVariant.Light;
            await Capture(dialog, "settings-final");

            SettingsDialog OpenSettings()
            {
                var result = new SettingsDialog { DataContext = model.FileList };
                _ = result.ShowDialog(owner);
                return result;
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Folder cover style smoke: {output}; success={error == null}");
            if (!interactive || error != null)
            {
                dialog?.Close();
                secondWindow?.Close();
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                    lifetime.Shutdown(error == null ? 0 : 1);
            }
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

    private static async Task Click(Control option)
    {
        option.UpdateLayout();
        var point = option.PointToScreen(new Point(option.Bounds.Width / 2, option.Bounds.Height / 2));
        PostMouse(5, point); PostMouse(1, point);
        await Task.Delay(50);
        PostMouse(2, point);
        await Task.Delay(150);
    }

    private static void PostMouse(int type, PixelPoint point)
    {
        var ev = CGEventCreateMouseEvent(IntPtr.Zero, type, new NativePoint(point.X, point.Y), 0);
        try { CGEventPost(0, ev); } finally { CFRelease(ev); }
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(double X, double Y);
    private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(Graphics)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, NativePoint position, int button);
    [DllImport(Graphics)] private static extern void CGEventPost(int tap, IntPtr ev);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr value);

    private static LazyTabItem DisplayPage(SettingsDialog dialog) => dialog.FindControl<TabControl>("SettingsTabs")!
        .Items.OfType<LazyTabItem>().Single(item => item.PageKey == "display");
}
#endif
