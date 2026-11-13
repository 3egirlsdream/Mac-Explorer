#if DEBUG
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class GitMenuSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "git-menu")).FullName;
        var checks = new List<string>(); string? error = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication(); owner.ShowAndActivate(); owner.Width = 1280; owner.Height = 800;
            var root = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "Git Sample")).FullName;
            await Run("/usr/bin/git", ["init", "-q"], root);
            foreach (var setting in new[] { ("user.name", "QA"), ("user.email", "qa@example.invalid"), ("commit.gpgsign", "false"), ("core.hooksPath", "/dev/null") })
                await Run("/usr/bin/git", ["config", setting.Item1, setting.Item2], root);
            var path = Path.Combine(root, "示例文件.txt");
            await File.WriteAllTextAsync(path, "first line\nsecond line\n");
            await Run("/usr/bin/git", ["add", "."], root); await Run("/usr/bin/git", ["commit", "-qm", "首次提交 Initial commit"], root);
            await File.WriteAllTextAsync(path, "first line\nstaged line\n");
            await Run("/usr/bin/git", ["add", "."], root); await Run("/usr/bin/git", ["commit", "-qm", "更新示例 Update sample"], root);
            await File.WriteAllTextAsync(path, "first line\nstaged change\n"); await Run("/usr/bin/git", ["add", "."], root);
            await File.WriteAllTextAsync(path, "first line\nworking change\n");
            var services = App.Services!; var git = services.GetRequiredService<IGitStatusService>();
            var vm = ((MainWindowViewModel)owner.DataContext!).FileList;
            await vm.NavigateToAsync(root);
            await Wait(() => vm.Entries.Any(e => e.FullPath == path && e.GitRepoRoot != null));
            var entry = vm.Entries.Single(e => e.FullPath == path); vm.SelectEntry(entry);
            var fileView = owner.GetVisualDescendants().OfType<FileListView>().First(v => v.IsVisible);
            var showMenu = typeof(FileListView).GetMethod("ShowMenuAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var menuField = typeof(FileListView).GetField("_openMenu", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var localization = services.GetRequiredService<ILocalizationService>();
            foreach (var language in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                localization.SetLanguage(language);
                foreach (var dark in new[] { false, true })
                {
                    Avalonia.Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    owner.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await (Task)showMenu.Invoke(fileView, [fileView, entry])!;
                    var nativeMenu = (ContextMenu)menuField.GetValue(fileView)!;
                    if (!nativeMenu.Items.OfType<MenuItem>().Any(i => i.Header?.ToString() == "GIT")) throw new InvalidOperationException("Git missing from initial menu");
                    var group = nativeMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "GIT");
                    var initialItems = nativeMenu.Items.OfType<MenuItem>().ToArray();
                    if (initialItems[Array.IndexOf(initialItems, group) - 1].Header?.ToString() != LocalizationText.Get("标签")) throw new InvalidOperationException("Git is not below Tags");
                    await Task.Delay(300);
                    if (!ReferenceEquals(group, nativeMenu.Items.OfType<MenuItem>().Single(i => i.Header?.ToString() == "GIT"))) throw new InvalidOperationException("Git item replaced after menu opened");
                    group.IsSubMenuOpen = true;
                    await Wait(() => group.Items.OfType<MenuItem>().Count(i => i.IsEnabled) >= 5);
                    if (!group.IsSubMenuOpen) throw new InvalidOperationException("Git submenu did not stay open.");
                    checks.Add($"{language}/{dark}: native submenu loaded");
                    await Task.Delay(200); nativeMenu.Close();
                    var actions = await vm.LoadCompleteFileContextMenuAsync(entry);
                    var gitAction = actions.Single(a => a.Tag == "git-menu");
                    var gitCommands = await gitAction.LoadSubItemsAsync!(CancellationToken.None, _ => { });
                    var discard = gitCommands[6].Execute!();
                    await Wait(() => owner.OwnedWindows.OfType<ConfirmDialog>().Any());
                    var confirmation = owner.OwnedWindows.OfType<ConfirmDialog>().Single();
                    await Task.Delay(150);
                    if (confirmation.ActualThemeVariant != (dark ? ThemeVariant.Dark : ThemeVariant.Light)) throw new InvalidOperationException("Confirmation theme mismatch");
                    var confirmationHandle = (IMacOSTopLevelPlatformHandle)confirmation.TryGetPlatformHandle()!;
                    await Run("/usr/sbin/screencapture", ["-x", "-l", SendInt(confirmationHandle.NSWindow, Selector("windowNumber")).ToString(), Path.Combine(output, $"confirm-{language}-{(dark ? "dark" : "light")}.png")]);
                    confirmation.Close(false); await discard;
                    if (await File.ReadAllTextAsync(path) != "first line\nworking change\n") throw new IOException("Cancel changed working content");
                    checks.Add($"{language}/{dark}: cancel kept working changes");
                    var context = await git.GetContextAsync([path]) ?? throw new IOException("Missing Git context");
                    foreach (var narrow in new[] { false, true })
                    {
                        var history = new GitHistoryDialog(git, context) { Width = narrow ? 480 : 1080, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
                        var pending = history.ShowDialog(owner);
                        try
                        {
                            await Wait(() => history.GetVisualDescendants().OfType<AvaloniaEdit.TextEditor>().Any(e => e.Document.Text.Contains("staged line")));
                            history.Activate(); history.UpdateLayout(); await Task.Delay(250);
                            var handle = (IMacOSTopLevelPlatformHandle)history.TryGetPlatformHandle()!;
                            await Run("/usr/sbin/screencapture", ["-x", "-l", SendInt(handle.NSWindow, Selector("windowNumber")).ToString(), Path.Combine(output, $"history-{language}-{(dark ? "dark" : "light")}-{(narrow ? "narrow" : "wide")}.png")]);
                            checks.Add($"{language}/{dark}/{narrow}: history and diff rendered");
                        }
                        finally { history.Close(); await pending; }
                    }
                }
            }
        }
        catch (Exception ex) { error = ex.ToString(); Console.WriteLine(error); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime) lifetime.Shutdown(error == null ? 0 : 1);
        }
    }
    private static async Task Wait(Func<bool> ready)
    { var timer = Stopwatch.StartNew(); while (!ready()) { if (timer.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("Git smoke did not settle."); await Task.Delay(30); } }
    private static async Task Run(string executable, string[] args, string? directory = null)
    {
        var start = new ProcessStartInfo(executable) { WorkingDirectory = directory ?? "", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (executable.EndsWith("/git", StringComparison.Ordinal)) { start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; start.Environment["GIT_CONFIG_NOSYSTEM"] = "1"; }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) process.Kill(true); }
        if (process.ExitCode != 0) throw new IOException(await error + await output);
    }
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")] private static extern IntPtr Selector(string name);
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")] private static extern nint SendInt(IntPtr target, IntPtr selector);
}
#endif
