#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

/// <summary>Isolated native rendering checks and an opt-in fixture for real Finder drags.</summary>
internal static class FileDeliverySmoke
{
    public static async Task RunAsync(MainWindow owner)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated test profile is required.");
        var output = Path.Combine(root, "file-delivery");
        Directory.CreateDirectory(output);
        var delivery = App.Services.GetRequiredService<FileDeliveryService>();
        var localization = App.Services.GetRequiredService<ILocalizationService>();
        var lifetime = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
        var interactive = Environment.GetEnvironmentVariable("MACEXPLORER_DELIVERY_SMOKE") == "interactive";
        string? error = null;
        try
        {
            var fixture = Directory.CreateDirectory(Path.Combine(root, "Documents", "DeliveryFixture")).FullName;
            var file = Path.Combine(fixture, "暂存演示文件.txt");
            var longFile = Path.Combine(fixture, "Long filename for staging preview and clipping verification — 暂存长文件名称.txt");
            var folder = Directory.CreateDirectory(Path.Combine(fixture, "示例文件夹")).FullName;
            await File.WriteAllTextAsync(file, "Original file must remain unchanged.");
            await File.WriteAllTextAsync(longFile, "Long filename fixture.");
            await File.WriteAllTextAsync(Path.Combine(folder, "child.txt"), "Folder child.");
            delivery.StagePaths([file, longFile, folder, Path.Combine(fixture, "unavailable.txt")]);
            delivery.SelectEntry(FileDeliveryService.StagingId);
            var controller = App.Services.GetRequiredService<FileDeliveryController>();
            await controller.ShowPanelAsync();
            var panel = lifetime.Windows.OfType<FileDeliveryWindow>().Single();
            var vm = (FileListViewModel)panel.FileList.DataContext!;
            if (vm.Entries.Count != 3) throw new InvalidOperationException("Staging did not load its path references.");
            foreach (var language in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                localization.SetLanguage(language);
                await vm.RefreshAsync();
                foreach (var dark in new[] { false, true })
                {
                    panel.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await CaptureAsync(panel, $"{language}-{(dark ? "dark" : "light")}");
                }
            }
            panel.Width = 360;
            await CaptureAsync(panel, "narrow");
            panel.FindControl<Button>("ClearStagingButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await vm.RefreshAsync();
            if (delivery.StagedPaths.Count != 0 || vm.Entries.Count != 0
                || await File.ReadAllTextAsync(file) != "Original file must remain unchanged.")
                throw new InvalidOperationException("Clearing staging changed the source or retained entries.");
            localization.SetLanguage("zh-CN");
            panel.Suspend(); panel.Hide();
            await ((MainWindowViewModel)owner.DataContext!).FileList.NavigateToAsync(fixture);
            delivery.StagingChanged += (_, _) => RecordState();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) => RecordState();
            if (interactive) timer.Start();
            RecordState();
            void RecordState() => File.WriteAllText(Path.Combine(output, "state.json"), JsonSerializer.Serialize(new
            {
                native = Platforms.MacOS.MacFileDeliveryStatusItem.GetDiagnosticState(),
                staged = delivery.StagedPaths,
                mainActive = owner.IsActive,
                windows = lifetime.Windows.Select(w => new { type = w.GetType().Name, w.IsVisible, w.IsActive,
                    x = w.Position.X, y = w.Position.Y, w.Width, w.Height, handle = w.TryGetPlatformHandle()?.HandleDescriptor, handleType = w.TryGetPlatformHandle()?.GetType().Name }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { error = ex.ToString(); }
        await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { success = error == null, error }));
        Console.WriteLine($"File delivery smoke: {output}; success={error == null}");
        if (!interactive || error != null) lifetime.Shutdown(error == null ? 0 : 1);

        async Task CaptureAsync(Visual visual, string name)
        {
            if (visual is Control control) control.UpdateLayout();
            var composition = ElementComposition.GetElementVisual(visual)!;
            await composition.Compositor.RequestCommitAsync();
            await Task.Delay(120);
            using var bitmap = await composition.Compositor.CreateCompositionVisualSnapshot(composition, 1);
            bitmap.Save(Path.Combine(output, name + ".png"));
        }
    }
}
#endif
