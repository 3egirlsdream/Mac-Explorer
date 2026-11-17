#if DEBUG
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class DiskManagementSmoke
{
    internal static async Task RunAsync(MainWindow owner)
    {
        var output = Path.Combine(RuntimePaths.TestRoot!, "disk-management");
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        string? error = null;
        AppWindow? preview = null;
        try
        {
            FolderTabRestoreSmoke.ActivateApplication();
            owner.ShowAndActivate();
            owner.Width = 1280;
            owner.Height = 800;
            var model = (MainWindowViewModel)owner.DataContext!;
            var before = model.Tabs.Count;
            var initialSidebar = owner.GetVisualDescendants().OfType<FinderSidebarView>().First(view => view.IsEffectivelyVisible);
            var fixtureDirectory = Directory.CreateDirectory(Path.Combine(RuntimePaths.TestRoot!, "Documents", "DiskFixture")).FullName;
            model.FileList.ExternalVolumes.Add(new VolumeInfo { Path = fixtureDirectory, DisplayName = "Fixture", IsExternal = true });
            await Task.Delay(100);
            var header = initialSidebar.FindControl<Grid>("MountedLocationsHeader")!;
            Check(header.IsVisible, "Mounted group is visible with a fixture shortcut");
            var gear = initialSidebar.FindControl<Button>("DiskManagementButton")!;
            Check(gear.Opacity == 0, "Gear is hidden by default");
            gear.Focus();
            Check(gear.Opacity == 1, "Keyboard focus reveals the gear");
            gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitUntil(() => model.FileList.IsDiskManagementPage);
            await owner.OpenDiskManagementAsync();
            Check(model.Tabs.Count == before + 1, "Repeated entry reuses the current workspace disk tab");
            await WaitUntil(() => owner.GetVisualDescendants().OfType<DiskManagementView>().Any(page => page.IsEffectivelyVisible));
            var livePage = owner.GetVisualDescendants().OfType<DiskManagementView>().Single(page => page.IsEffectivelyVisible);
            await livePage.RefreshAsync();
            var workspace = livePage.GetVisualAncestors().OfType<ExplorerWorkspaceView>().Single();
            Check(!workspace.FindControl<Border>("ToolbarSurface")!.IsVisible && !workspace.FindControl<Border>("StatusBar")!.IsVisible,
                "File toolbar and status bar are hidden on the management page");
            Check(livePage.ViewModel.Disks.Any(row => row.Info.Path == "/" && row.Info.TotalBytes > 0 && !row.Info.CanEject),
                "Native metadata reports the startup volume and prevents eject");
            await Capture(owner, "native-main-window");
            var diskTab = model.SelectedTab;
            await owner.OpenNewTabAsync(fixtureDirectory);
            await WaitUntil(() => !livePage.IsEffectivelyVisible);
            Check(!livePage.IsEffectivelyVisible, "Disk page hides when switching to a folder tab");
            model.SelectedTab = diskTab;

            var fixture = new Information(fixtureDirectory);
            using var page = new DiskManagementView(fixture, new Volumes());
            preview = new AppWindow { Title = "Disk Management", Width = 1000, Height = 760, Content = page };
            preview.Show();
            await WaitUntil(() => page.ViewModel.Disks.Count == 3);
            preview.Height = 460;
            page.ViewModel.SelectedDisk = page.ViewModel.Disks[1];
            var details = page.FindControl<Expander>("TechnicalDetails")!;
            details.IsExpanded = true;
            await Capture(preview, "refresh-before-scroll");
            var scroll = page.FindControl<ScrollViewer>("DiskDetailsScroll")!;
            scroll.ScrollToEnd();
            await Capture(preview, "refresh-before");
            var layout = page.FindControl<Grid>("DiskLayout")!;
            var bounds = layout.Bounds;
            var offset = scroll.Offset;
            Check(offset.Y > 0, "Refresh fixture has a nonzero scroll offset");
            var completion = new TaskCompletionSource<IReadOnlyList<DiskInfo>>();
            fixture.Pending = completion.Task;
            var refresh = page.RefreshAsync(showProgress: false);
            await Capture(preview, "refresh-pending");
            Check(!page.ViewModel.IsLoading && bounds == layout.Bounds && offset == scroll.Offset,
                "Pending background refresh keeps content position and scroll without a loading bar");
            fixture.Disks = fixture.Disks.Select(disk => disk.Identity == "external" ? disk with { AvailableBytes = 830_000_000_000 } : disk).ToArray();
            completion.SetResult(fixture.Disks);
            await refresh;
            fixture.Pending = null;
            await Capture(preview, "refresh-after");
            Check(bounds == layout.Bounds && offset == scroll.Offset && details.IsExpanded,
                "Capacity updates preserve scroll and expanded details");
            preview.Height = 760;
            var language = App.Services.GetRequiredService<ILocalizationService>();
            foreach (var code in new[] { "zh-CN", "zh-Hant", "en", "fr" })
            {
                language.SetLanguage(code);
                foreach (var dark in new[] { false, true })
                {
                    preview.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    preview.Width = 1000;
                    page.ViewModel.SelectedDisk = page.ViewModel.Disks[1];
                    page.FindControl<Expander>("TechnicalDetails")!.IsExpanded = true;
                    await Capture(preview, $"{code}-{(dark ? "dark" : "light")}-wide");
                    preview.Width = 600;
                    await Capture(preview, $"{code}-{(dark ? "dark" : "light")}-list");
                    var list = page.FindControl<ListBox>("DiskList")!;
                    list.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
                    await Capture(preview, $"{code}-{(dark ? "dark" : "light")}-detail");
                    Check(page.FindControl<Grid>("DiskDetailPane")!.IsVisible, $"{code}/{dark}: compact details are reachable");
                }
            }
            fixture.Disks = fixture.Disks.Where(disk => disk.Identity != "external").ToArray();
            await page.RefreshAsync();
            Check(page.ViewModel.IsDisconnected && !page.ViewModel.CanEject, "Unplug leaves explicit disconnected state");
            await Capture(preview, "disconnected");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            preview?.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "report.json"), JsonSerializer.Serialize(
                new { success = error == null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Disk management smoke: {output}; success={error == null}");
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
            await Task.Delay(180);
            var visual = ElementComposition.GetElementVisual(control)!;
            await visual.Compositor.RequestCommitAsync();
            using var snapshot = await visual.Compositor.CreateCompositionVisualSnapshot(visual, 1);
            snapshot.Save(Path.Combine(output, name + ".png"));
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Disk management did not become ready.");
            await Task.Delay(20);
        }
    }

    private sealed class Information : IDiskInformationService
    {
        public IReadOnlyList<DiskInfo> Disks { get; set; }
        public Task<IReadOnlyList<DiskInfo>>? Pending { get; set; }
        public Information(string root) => Disks =
        [
            new() { Identity = "boot", Path = root, Name = "Macintosh HD", Format = "apfs", TotalBytes = 1_000_000_000_000,
                FreeBytes = 180_000_000_000, AvailableBytes = 240_000_000_000, IsReadOnly = true, Connection = "PCI-Express" },
            new() { Identity = "external", Path = root, Name = "项目资料与照片备份 · Project archive and photo backups", Format = "exfat",
                IsExternal = true, CanEject = true, TotalBytes = 2_000_000_000_000, FreeBytes = 820_000_000_000,
                AvailableBytes = 820_000_000_000, IsReadOnly = false, Connection = "USB", DeviceIdentifier = "disk9s1",
                VolumeUuid = "67A8FD18-0000-0000-0000-887766554433", DeviceName = "Portable SSD" },
            new() { Identity = "image", Path = root, Name = "安装映像 · Installer", Format = "hfs", IsDiskImage = true,
                CanEject = true, IsReadOnly = true, TotalBytes = 4_000_000_000, FreeBytes = 0, AvailableBytes = 0, Connection = "Disk Image" }
        ];
        public Task<IReadOnlyList<DiskInfo>> GetMountedDisksAsync(CancellationToken cancellationToken) => Pending ?? Task.FromResult(Disks);
    }

    private sealed class Volumes : IVolumeMonitorService
    {
        public IReadOnlyList<VolumeInfo> ExternalVolumes => [];
        public bool IsExternalVolume(string path) => false;
        public Task<bool> EjectVolumeAsync(string volumePath) => Task.FromResult(false);
        public event Action? VolumesChanged { add { } remove { } }
        public void Dispose() { }
    }
}
#endif
