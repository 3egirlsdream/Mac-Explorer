using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MacExplorer.Models;
using MacExplorer.Controls;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using MacExplorer.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "Network")]
public sealed class NetworkNavigationTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public void SavedConnectionsToTheSameShareKeepSeparateSelectionIdentities()
    {
        var config = Path.Combine(RuntimePaths.DataDirectory, "selection-" + Guid.NewGuid().ToString("N") + ".json");
        using var connections = new RemoteConnectionService(config, new PrivacySecurityFollowupTests.MemoryCredentials());
        connections.SaveServer(new RemoteServerInfo { Id = "first", ServiceType = RemoteServiceType.Smb, Host = "nas", ShareName = "Files", Username = "alice" });
        connections.SaveServer(new RemoteServerInfo { Id = "second", ServiceType = RemoteServiceType.Smb, Host = "nas", ShareName = "Files", Username = "bob" });
        var view = new NetworkView();
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        typeof(NetworkView).GetField("_connections", flags)!.SetValue(view, connections);
        var refresh = typeof(NetworkView).GetMethod("RefreshRows", flags)!;
        refresh.Invoke(view, null);
        view.SelectedLocation = view.Locations.Single(row => row.Server?.Id == "second");
        refresh.Invoke(view, null);
        Assert.Equal("second", Assert.IsType<NetworkLocationRow>(view.SelectedLocation).Server!.Id);
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedShareOpensWithDoubleClickOrEnter(bool keyboard)
    {
        var path = Path.Combine(RuntimePaths.DataDirectory, "MountedShareFixture");
        var files = new FakeFileService(path);
        using var vm = CreateViewModel(files);
        await vm.NavigateToAsync(VirtualPath.Network);
        var theme = new FluentTheme();
        Application.Current!.Styles.Insert(0, theme);
        var styles = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml"));
        var components = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"));
        Application.Current.Styles.Add(styles);
        Application.Current.Styles.Add(components);
        var view = new NetworkView { DataContext = vm };
        var window = new Window { Width = 800, Height = 500, Content = view };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("ServersList")!;
            view.SetLocations(new[] { new NetworkLocationRow("TestShare", "Mounted", Volume: new VolumeInfo { Path = path, DisplayName = "TestShare", IsNetwork = true }) });
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.Single(list.Rows);
            Assert.True(list.Bounds.Height > 0, $"List bounds {list.Bounds}, view {view.Bounds}, window {window.Bounds}");
            if (keyboard)
            {
                view.SelectedLocation = view.Locations[0];
                Assert.True(list.Focus());
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            }
            else
            {
                var point = list.TranslatePoint(new Point(60, 15), window)!.Value;
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(path, vm.CurrentPath);
        }
        finally { window.Close(); Application.Current!.Styles.Remove(theme); Application.Current.Styles.Remove(styles); Application.Current.Styles.Remove(components); }
    }

    [AvaloniaFact(Explicit = true)]
    public void SidebarShowsFirstMountedVolumeAndHidesAfterLastUnmount()
    {
        using var vm = CreateViewModel(new FakeFileService(Path.Combine(RuntimePaths.DataDirectory, "NetworkSidebarFixture")));
        var sidebar = new FinderSidebarView { DataContext = vm };
        var window = new Window { Content = sidebar };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var volumes = sidebar.FindControl<ItemsControl>("ExternalVolumesControl")!;
            Assert.False(volumes.IsVisible);
            vm.ExternalVolumes.Add(new VolumeInfo { Path = "/Volumes/TestShare", DisplayName = "TestShare", IsNetwork = true });
            Dispatcher.UIThread.RunJobs();
            Assert.True(volumes.IsVisible);
            Assert.Single(volumes.Items);
            vm.ExternalVolumes.Clear();
            Dispatcher.UIThread.RunJobs();
            Assert.False(volumes.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public void NetworkOperationErrorsUseWindowToast()
    {
        using var vm = CreateViewModel(new FakeFileService(Path.Combine(RuntimePaths.DataDirectory, "NetworkErrorFixture")));
        var theme = new FluentTheme();
        Application.Current!.Styles.Add(theme);
        var window = new Window { Content = new NetworkView { DataContext = vm } };
        try
        {
            window.Show(); vm.SetOwnerWindow(window);
            vm.NetworkError = "Busy test share";
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Busy test share");
            Assert.Null(((NetworkView)window.Content).FindControl<TextBlock>("OperationError"));
        }
        finally { vm.SetOwnerWindow(null); window.Close(); Application.Current.Styles.Remove(theme); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(640, false)]
    [InlineData(640, true)]
    [InlineData(1000, false)]
    [InlineData(1000, true)]
    public void NetworkTableKeepsColumnsAlignedAndActionsReachable(int width, bool dark)
    {
        var theme = new FluentTheme();
        var styles = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/Styles.axaml"));
        var components = (Styles)AvaloniaXamlLoader.Load(new Uri("avares://MacExplorer/Assets/ComponentStyles.axaml"));
        Application.Current!.Styles.Add(theme); Application.Current.Styles.Add(styles); Application.Current.Styles.Add(components);
        var view = new NetworkView();
        var list = view.FindControl<FastFileList>("ServersList")!;
        view.SetLocations(new[] { new NetworkLocationRow("Long English Network Server Name", "nas", new RemoteServerInfo { Host = "files.long-company-name.example", Port = 445 }) });
        var window = new Window { Width = width, Height = 400, Content = view, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(FastFileList.RowHeight, list.RowBounds(0).Height);
            Assert.Equal(6, view.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Classes.Contains("file-list-column-caption")));
            var actions = view.FindControl<Canvas>("RowActions")!.Children.OfType<Button>().ToArray();
            Assert.Equal(2, actions.Length);
            var status = view.FindControl<Canvas>("RowActions")!.Children.OfType<Border>().Single();
            Assert.Contains("status-tag", status.Classes);
            Assert.Equal(24, status.Bounds.Height);
            Assert.All(actions, button =>
            {
                Assert.Contains("secondary", button.Classes);
                Assert.Equal(24, button.Bounds.Height);
                Assert.Equal(status.Bounds.Center.Y, button.Bounds.Center.Y, 2);
            });
            Assert.All(actions, button => Assert.True(button.Bounds.Width >= 24));
            var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().First();
            if (width < 880)
            {
                Assert.True(scroll.Extent.Width > scroll.Viewport.Width);
                scroll.Offset = new Vector(scroll.Extent.Width - scroll.Viewport.Width, 0);
                window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            }
            var actionPoint = actions[1].TranslatePoint(new Point(actions[1].Bounds.Width / 2, actions[1].Bounds.Height / 2), window)!.Value;
            window.MouseDown(actionPoint, MouseButton.Left); window.MouseUp(actionPoint, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.RowMenu?.IsOpen);
            Assert.Single(view.RowMenu!.Items);
            view.RowMenu.Close();

        }
        finally { window.Close(); Application.Current.Styles.Remove(theme); Application.Current.Styles.Remove(styles); Application.Current.Styles.Remove(components); }
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(640, false)]
    [InlineData(640, true)]
    [InlineData(1200, false)]
    [InlineData(1200, true)]
    public async Task NetworkUsesNavigationRefreshAndConnectionIconBesideBreadcrumb(int width, bool dark)
    {
        using var theme = new FastListTestTheme();
        var previous = App.Services;
        var discovery = new NavigationDiscovery();
        using var services = new ServiceCollection().AddSingleton<INetworkDiscoveryService>(discovery).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        var files = new FakeFileService(Path.Combine(RuntimePaths.DataDirectory, "NetworkToolbarFixture"));
        using var vm = CreateViewModel(files);
        await vm.NavigateToAsync(VirtualPath.Network);
        using var tab = new ExplorerTabViewModel(vm);
        using var workspace = new ExplorerWorkspaceView { DataContext = tab };
        var window = new Window { Width = width, Height = 500, Content = workspace,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs();
            var connect = workspace.FindControl<Button>("ConnectServerButton")!;
            var breadcrumb = workspace.FindControl<BreadcrumbBar>("BreadcrumbControl")!;
            Assert.True(connect.IsEffectivelyVisible);
            Assert.IsType<PathIcon>(connect.Content);
            Assert.Equal(LocalizationText.Get("network.connect"), Avalonia.Automation.AutomationProperties.GetName(connect));
            Assert.Equal(LocalizationText.Get("network.connect"), ToolTip.GetTip(connect));
            Assert.True(connect.Focus());
            var connectPoint = connect.TranslatePoint(default, window)!.Value;
            var breadcrumbPoint = breadcrumb.TranslatePoint(default, window)!.Value;
            Assert.True(connectPoint.X >= breadcrumbPoint.X + breadcrumb.Bounds.Width);
            Assert.InRange(Math.Abs(breadcrumbPoint.Y + breadcrumb.Bounds.Height / 2 - connectPoint.Y - connect.Bounds.Height / 2), 0, 1);
            Assert.True(connectPoint.X + connect.Bounds.Width <= window.ClientSize.Width);
            var network = workspace.FindControl<NetworkView>("NetworkViewControl")!;
            Assert.DoesNotContain(network.GetVisualDescendants().OfType<Button>(), button => button.Content is string);
            var reads = files.EnumerateDirectoryCallCount;
            var refresh = workspace.FindControl<Button>("RefreshButton")!;
            var point = refresh.TranslatePoint(new Point(refresh.Bounds.Width / 2, refresh.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, discovery.RefreshCount);
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.Equal(2, discovery.RefreshCount);
            Assert.Equal(reads, files.EnumerateDirectoryCallCount);
            await vm.NavigateToAsync(files.HomeDirectory);
            Dispatcher.UIThread.RunJobs();
            Assert.False(connect.IsVisible);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previous);
        }
    }

    private sealed class NavigationDiscovery : INetworkDiscoveryService
    {
        public int RefreshCount { get; private set; }
        public IReadOnlyList<NetworkServer> Servers => [];
        public string? Error => null;
        public event Action? Changed { add { } remove { } }
        public IDisposable Acquire() => new Lease();
        public void Refresh() => RefreshCount++;
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task NetworkHasHistoryAndCannotCreateFilesOrPaste()
    {
        var files = new FakeFileService(Path.Combine(RuntimePaths.DataDirectory, "NetworkNavigationFixture"));
        using var vm = CreateViewModel(files);
        await vm.NavigateToAsync(VirtualPath.Network);
        Assert.True(vm.IsNetworkPage); Assert.Empty(vm.Entries);
        var reads = files.EnumerateDirectoryCallCount;
        await vm.CreateNewFileAsync(); await vm.CreateNewFolderAsync(); await vm.PasteAsync();
        Assert.Equal(reads, files.EnumerateDirectoryCallCount);
        await vm.NavigateToAsync(files.HomeDirectory);
        Assert.False(vm.IsNetworkPage);
        await vm.NavigateBackAsync(); Assert.True(vm.IsNetworkPage);
        await vm.NavigateForwardAsync(); Assert.False(vm.IsNetworkPage);
    }
}
