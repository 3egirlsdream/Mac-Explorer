#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class FileConflictSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "file-conflict");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var dimensions = new List<object>();
        string? error = null;
        FileConflictDialog? dialog = null;
        var language = App.Services.GetRequiredService<ILocalizationService>();
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            foreach (var id in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            foreach (var dark in new[] { false, true })
            {
                language.SetLanguage(id);
                var name = dark ? new string('长', 80) + ".png" : "图片 2026-11-10 13.52.05.png";
                var source = new FileSystemEntry { Name = name, FullPath = Path.Combine(RuntimePaths.TestRoot!, "Desktop", "待整理", name), Size = 1667236, LastModified = new DateTime(2026, 11, 10, 13, 52, 0) };
                var target = new FileSystemEntry { Name = name, FullPath = Path.Combine(RuntimePaths.TestRoot!, "Downloads", name), Size = source.Size, LastModified = source.LastModified };
                dialog = new FileConflictDialog(new(source, target, false, false))
                { RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light, Width = dark ? 400 : FileConflictDialog.DefaultWidth };
                var pending = dialog.ShowDialog<FileConflictDecision?>(owner);
                dialog.UpdateLayout();
                await Task.Delay(250);
                var visual = ElementComposition.GetElementVisual(dialog)!;
                await visual.Compositor.RequestCommitAsync();
                using (var bitmap = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1))
                    bitmap.Save(Path.Combine(output, $"{id}-{(dark ? "dark-narrow-long" : "light")}.png"));
                var replaceMenu = dialog.GetVisualDescendants().OfType<SplitButton>().Single();
                var flyout = (MenuFlyout)replaceMenu.Flyout!;
                replaceMenu.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_SecondaryButton")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(100);
                var menuItem = (MenuItem)flyout.Items[0]!;
                var menuRoot = TopLevel.GetTopLevel(menuItem)!;
                await ButtonComponentsSmoke.CaptureAsync(menuRoot, Path.Combine(output, $"{id}-{(dark ? "dark" : "light")}-dropdown.png"));
                if (!menuRoot.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("context-submenu-surface") && PopupGlass.GetIsEnabled(b)))
                    throw new InvalidOperationException("Conflict dropdown does not use the shared menu surface.");
                flyout.Hide();
                if (dialog.TitleBarContent is not TextBlock { IsEffectivelyVisible: true, Bounds.Width: > 0 })
                    throw new InvalidOperationException("Conflict title is missing.");
                if (!dialog.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == source.KindText && text.Bounds.Width > 0))
                    throw new InvalidOperationException("File kind is clipped.");
                var buttons = dialog.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible).ToArray();
                foreach (var button in buttons)
                {
                    var point = button.TranslatePoint(default, dialog)!.Value;
                    if (point.X < 0 || point.X + button.Bounds.Width > dialog.Bounds.Width + 1 || point.Y + button.Bounds.Height > dialog.Bounds.Height + 1)
                        throw new InvalidOperationException($"Action clipped: {id}, {dark}, {button.Content}");
                }
                var skipButton = buttons.Single(b => Equals(b.Content, LocalizationText.Get("conflict.Skip")));
                var replaceButton = buttons.Single(b => b.Name == "PART_PrimaryButton");
                var cancelButton = buttons.Single(b => Equals(b.Content, LocalizationText.Get("conflict.cancel")));
                if (!replaceButton.Classes.Contains("secondary")
                    || !Equals(skipButton.Background, replaceButton.Background)
                    || !Equals(skipButton.BorderBrush, replaceButton.BorderBrush)
                    || skipButton.CornerRadius != replaceButton.CornerRadius
                    || skipButton.Padding != replaceButton.Padding
                    || skipButton.FontSize != replaceButton.FontSize
                    || !Equals(skipButton.FontFamily, replaceButton.FontFamily))
                    throw new InvalidOperationException("Replace does not reuse the standard secondary button.");
                if (cancelButton.GetDiagnostic(Button.PaddingProperty).Priority == BindingPriority.LocalValue
                    || cancelButton.GetDiagnostic(Button.ForegroundProperty).Priority == BindingPriority.LocalValue)
                    throw new InvalidOperationException("Cancel overrides the shared ghost button style.");
                var checkbox = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
                checkbox.IsChecked = true;
                var keep = buttons.Single(b => Equals(b.Content, LocalizationText.Get("conflict.KeepBoth")));
                dimensions.Add(new { language = id, dark, panel = dialog.Bounds.Size.ToString(), button = keep.Bounds.Size.ToString(), fontSize = keep.FontSize });
                if (id == "zh-CN" && !dark)
                {
                    if (Math.Abs(dialog.Bounds.Width / keep.Bounds.Height - 1462d / 76) > 0.1
                        || Math.Abs(dialog.Bounds.Height / keep.Bounds.Height - 902d / 76) > 0.1)
                        throw new InvalidOperationException("Panel-to-action proportions differ from the reference.");
                }
                keep.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (await pending != new FileConflictDecision(FileConflictAction.KeepBoth, true)) throw new InvalidOperationException("Keep both result changed.");
                checks.Add($"{id}/{dark}: visible actions and keep both with apply-all");
            }
            foreach (var action in new[] { FileConflictAction.Skip, FileConflictAction.Replace, FileConflictAction.ReplaceOlder })
            {
                var file = new FileSystemEntry { Name = "test.txt", FullPath = Path.Combine(RuntimePaths.TestRoot!, "test.txt") };
                dialog = new FileConflictDialog(new(file, file, false, false));
                var pending = dialog.ShowDialog<FileConflictDecision?>(owner);
                dialog.UpdateLayout();
                if (action == FileConflictAction.ReplaceOlder)
                {
                    var split = dialog.GetVisualDescendants().OfType<SplitButton>().Single();
                    var menu = (MenuFlyout)split.Flyout!;
                    split.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_SecondaryButton")
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Task.Delay(100);
                    if (!menu.IsOpen) throw new InvalidOperationException("Replacement menu did not open.");
                    ((MenuItem)menu.Items[0]!).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                }
                else if (action == FileConflictAction.Replace)
                    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_PrimaryButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else
                    dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalizationText.Get("conflict.Skip"))).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (await pending != new FileConflictDecision(action)) throw new InvalidOperationException($"Wrong decision: {action}");
                checks.Add(action.ToString());
            }
            var folder = new FileSystemEntry { Name = "folder", FullPath = Path.Combine(RuntimePaths.TestRoot!, "folder"), IsDirectory = true };
            dialog = new FileConflictDialog(new(folder, folder, false, false));
            var mergePending = dialog.ShowDialog<FileConflictDecision?>(owner);
            dialog.UpdateLayout();
            if (!Equals(dialog.GetVisualDescendants().OfType<SplitButton>().Single().Content, LocalizationText.Get("conflict.merge")))
                throw new InvalidOperationException("Folder merge label changed.");
            dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, LocalizationText.Get("conflict.cancel")))
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (await mergePending != null) throw new InvalidOperationException("Cancel returned a file decision.");
            checks.Add("Folder merge label and cancellation");
            dialog = new FileConflictDialog(new(folder, new FileSystemEntry { Name = "folder" }, false, false));
            var mismatchPending = dialog.ShowDialog<FileConflictDecision?>(owner);
            dialog.UpdateLayout();
            if (((MenuItem)((MenuFlyout)dialog.GetVisualDescendants().OfType<SplitButton>().Single().Flyout!).Items[0]!).IsEnabled)
                throw new InvalidOperationException("Replace older allowed mismatched types.");
            dialog.Close(); await mismatchPending;
            checks.Add("Mismatched types disable replace older");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            dialog?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, dimensions, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("File conflict smoke: " + output + (error == null ? " PASS" : " FAIL " + error));
            (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(error == null ? 0 : 1);
        }
    }
}
#endif
