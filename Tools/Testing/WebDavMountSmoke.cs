#if DEBUG
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Models;
using MacExplorer.Platforms.MacOS;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MacExplorer.Tools.Testing;

internal static class WebDavMountSmoke
{
    public static async Task RunAsync(MainWindow window)
    {
        var root = RuntimePaths.TestRoot ?? throw new InvalidOperationException("An isolated profile is required.");
        var results = new List<string>();
        void Passed(string stage)
        {
            results.Add(stage);
            File.WriteAllText(Path.Combine(root, "webdav-progress.json"), JsonSerializer.Serialize(results));
        }
        string? error = null;
        var mounts = App.Services.GetRequiredService<INetworkMountService>();
        var coordinator = App.Services.GetRequiredService<IServerConnectionCoordinator>();
        var remote = App.Services.GetRequiredService<IRemoteConnectionService>();
        var locations = App.Services.GetRequiredService<INetworkLocationService>();
        var endpoint = new Uri(Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_TEST_URL")!);
        var slow = new Uri(Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_TEST_SLOW_URL") ?? endpoint.AbsoluteUri);
        var tls = new Uri(Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_TEST_TLS_URL")!);
        var fixture = Environment.GetEnvironmentVariable("MACEXPLORER_WEBDAV_TEST_DATA")!;
        if (!endpoint.IsLoopback || !tls.IsLoopback || !slow.IsLoopback) throw new InvalidOperationException("Only loopback fixtures are allowed.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var server = Connection(new Uri(endpoint, "dav/"));
        MainWindow? secondWindow = null;
        try
        {
            // Known credentials and NoUI exercise native failures without prompting the user.
            var badLogin = Connection(new Uri(endpoint, "dav/")); badLogin.Password = "wrong-fixture-password";
            var authentication = await FailureAsync(() => mounts.MountAsync(badLogin, timeout.Token));
            Require(authentication is NetworkMountException { Status: 80 or 13 }, "Invalid credentials failed for a reason other than authentication.");
            Passed("authentication-rejected");
            var certificate = await FailureAsync(() => mounts.MountAsync(Connection(new Uri(tls, "dav/")), timeout.Token));
            Require(certificate.InnerException is HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError },
                "The untrusted certificate failed for a reason other than TLS validation.");
            Passed("untrusted-tls-rejected");

            using (var cancelled = new CancellationTokenSource())
            {
                var pending = mounts.MountAsync(Connection(new Uri(slow, "slow/")), cancelled.Token);
                while (!File.Exists(Path.Combine(fixture, "slow-started"))) await Task.Delay(30, timeout.Token);
                cancelled.Cancel();
                try { await pending; throw new InvalidOperationException("The cancelled mount succeeded."); }
                catch (OperationCanceledException) { Passed("in-flight-mount-cancelled"); }
            }

            var path = await coordinator.ConnectAsync(server, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(root, "webdav-mount-state.json"), JsonSerializer.Serialize(new
            {
                path,
                volumes = MacNetworkNative.GetVolumes().Where(v => DirectoryAccess.IsWithin(path, v.Path))
                    .Select(v => new { v.Path, v.DisplayName, v.IsNetwork, v.SourceUrl, v.MountId })
            }), timeout.Token);
            remote.SaveServer(server);
            Require(coordinator.IsConnected(server), "Native mount state is missing.");
            Require(locations.GetLocations().Count(r => r.Server?.Id == server.Id && r.IsConnected) == 1, "Connection row was duplicated.");
            Require(await coordinator.ConnectAsync(server, timeout.Token) == path, "Repeated connect changed the mount path.");
            Passed("mounted-and-reused");
            var vm = ((MainWindowViewModel)window.DataContext!).FileList;
            await vm.NavigateToAsync(path);
            Require(vm.Entries.Any(e => e.Name == "seed.txt"), "Mounted files did not load in the application.");
            Require(await File.ReadAllTextAsync(Path.Combine(path, "seed.txt"), timeout.Token) == "fixture seed", "Download contents differ.");
            Passed("browsed-and-downloaded");
            await Task.Run(() =>
            {
                var folder = Path.Combine(path, "新增 Folder");
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "上传 file.txt");
                File.WriteAllText(file, "fixture upload");
                File.Move(file, Path.Combine(folder, "renamed.txt"));
                Require(File.ReadAllText(Path.Combine(fixture, "data", "新增 Folder", "renamed.txt")) == "fixture upload", "Upload did not reach the server.");
                File.Delete(Path.Combine(folder, "renamed.txt"));
                Directory.Delete(folder);
            }, timeout.Token);
            Passed("uploaded-renamed-deleted");
            secondWindow = App.OpenNewWindow(VirtualPath.Network);
            await Task.Delay(150, timeout.Token);
            foreach (var owner in new[] { window, secondWindow })
            {
                var sidebar = owner.GetVisualDescendants().OfType<FinderSidebarView>().First();
                Require(sidebar.FindControl<ItemsControl>("NetworkLocationsList")!.Items.Cast<NetworkLocationRow>().Count(r => r.Server?.Id == server.Id) == 1,
                    "Sidebar did not synchronize the connected mount.");
                Require(!sidebar.FindControl<ItemsControl>("ExternalVolumesControl")!.Items.Cast<VolumeInfo>().Any(v => v.Path == path), "Network mount remained under local locations.");
            }
            Passed("two-window-sidebar-synchronized");
            await vm.NavigateToAsync(VirtualPath.Network);
            using (var busy = File.OpenRead(Path.Combine(path, "seed.txt")))
            {
                Require(!await coordinator.DisconnectAsync(server), "The busy volume was unexpectedly ejected.");
                Require(coordinator.IsConnected(server), "A failed eject removed the connection.");
            }
            Passed("busy-eject-preserves-connection");
            Require(await coordinator.DisconnectAsync(server), "The test volume could not be ejected.");
            Require(!coordinator.IsConnected(server), "Connection remained active after eject.");
            Dispatcher.UIThread.RunJobs();
            Passed("ejected");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            secondWindow?.Close();
            // Only remove this fixture's mounts; never eject another application's volumes.
            foreach (var volume in mounts.GetMountedVolumes().Where(v => Uri.TryCreate(v.SourceUrl, UriKind.Absolute, out var uri)
                && uri.IsLoopback && (uri.Port == endpoint.Port || uri.Port == tls.Port || uri.Port == slow.Port)))
                if (!await mounts.UnmountAsync(volume.Path)) error ??= "Could not clean up the test mount.";
            var report = Path.Combine(root, "webdav-smoke.json");
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { results, error }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"WebDAV smoke: {report}; error={error}");
            ((IClassicDesktopStyleApplicationLifetime)Avalonia.Application.Current!.ApplicationLifetime!).Shutdown(error == null ? 0 : 1);
        }
    }

    private static RemoteServerInfo Connection(Uri uri) => new()
    { Name = "WebDAV fixture", ServiceType = RemoteServiceType.WebDav, WebDavUrl = uri.AbsoluteUri, Host = uri.Host, Port = uri.Port, Username = "fixture", Password = "fixture-secret" };
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task<IOException> FailureAsync(Func<Task<string>> action)
    {
        try { await action(); }
        catch (IOException error) { return error; }
        throw new InvalidOperationException("Native mounting unexpectedly accepted invalid credentials or an untrusted certificate.");
    }
}
#endif
