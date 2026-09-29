using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MacExplorer.Controls;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Views;
using MacExplorer.Views.Dialogs;
using MacExplorer.ViewModels;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public async Task LocalSendMenuAppearsOnlyForEnabledLocalEntries()
    {
        var files = new FakeFileService("/private/tmp/fk-localsend-menu");
        var local = new FileSystemEntry { FullPath = "/private/tmp/fk-localsend-menu/file.txt", Name = "file.txt" };
        files.Seed(local);
        var service = new FakeLocalSend();
        using var vm = CreateViewModel(files, localSendService: service);
        await vm.RefreshAsync();
        var fast = await vm.LoadCompleteFileContextMenuAsync(local);
        var sendTo = Assert.Single(fast, action => action.Label == "发送到");
        var localSend = Assert.Single(sendTo.SubItems!);
        Assert.Equal("LocalSend", localSend.Label);
        Assert.NotNull(localSend.IconImage);
        Assert.NotNull(localSend.LoadSubItemsAsync);
        service.IsEnabled = false;
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(local), action => action.Label == "发送到");
        service.IsEnabled = true;
        var remote = new FileSystemEntry { FullPath = VirtualPath.BuildRemotePath("host", "/file.txt"), Name = "file.txt" };
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(remote), action => action.Label == "发送到");
        var invalid = new FileSystemEntry { FullPath = "sftp://host/file.txt", Name = "file.txt" };
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(invalid), action => action.Label == "发送到");
        using var browseVm = CreateViewModel(files, browseOnly: true, localSendService: service);
        Assert.Empty(await browseVm.LoadCompleteFileContextMenuAsync(local));
        var navigation = new NavigationViewModel(files) { CurrentPath = vm.TrashPath, IsHomePage = false };
        using var trashVm = CreateViewModel(files, navigation: navigation, localSendService: service);
        Assert.DoesNotContain(await trashVm.LoadCompleteFileContextMenuAsync(local), action => action.Label == "发送到");
    }

    [AvaloniaFact]
    public async Task ClosingMenuCancelsDiscoveryAndDiscardsLateDeviceResults()
    {
        var files = new FakeFileService("/private/tmp/fk-localsend-menu");
        var entry = new FileSystemEntry { FullPath = "/private/tmp/fk-localsend-menu/file.txt", Name = "file.txt" };
        files.Seed(entry);
        var service = new FakeLocalSend();
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List }, localSendService: service);
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            var origin = list.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + 70, origin.Y + 15);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            Assert.True(menu.IsOpen);
            var sendTo = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "发送到");
            sendTo.IsSubMenuOpen = true;
            var localSend = Assert.Single(sendTo.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "LocalSend");
            localSend.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(service.DiscoveryStarted);
            menu.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.True(service.DiscoveryCancelled);
            service.FinishDiscovery();
            await service.DiscoveryReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Late device");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task SelectingDiscoveredDeviceSendsTheSelectedFile()
    {
        var files = new FakeFileService("/private/tmp/fk-localsend-menu");
        var entry = new FileSystemEntry { FullPath = "/private/tmp/fk-localsend-menu/file.txt", Name = "file.txt" };
        files.Seed(entry);
        var service = new FakeLocalSend();
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List }, localSendService: service);
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            var origin = list.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + 70, origin.Y + 15);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            Assert.True(menu.IsOpen);
            var sendTo = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "发送到");
            sendTo.IsSubMenuOpen = true;
            var localSend = Assert.Single(sendTo.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "LocalSend");
            localSend.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(service.DiscoveryStarted);
            service.FinishDiscovery();
            await service.DiscoveryReturned.Task.WaitAsync(TimeSpan.FromSeconds(5));
            MenuItem? deviceItem = null;
            for (var attempt = 0; attempt < 20 && deviceItem == null; attempt++)
            {
                Dispatcher.UIThread.RunJobs();
                deviceItem = localSend.Items.OfType<MenuItem>().SingleOrDefault(item => item.Header?.ToString() == "Late device");
                if (deviceItem == null) await Task.Delay(10);
            }
            Assert.NotNull(deviceItem);
            Assert.Contains(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "通过 IP 连接…");
            deviceItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var sent = await service.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("Late device", sent.Device.Alias);
            Assert.Equal([entry.FullPath], sent.Paths);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    [AvaloniaFact]
    public async Task ProgressiveDiscoveryKeepsDeviceRowCurrentAndIgnoresOldRefreshResults()
    {
        var files = new FakeFileService("/private/tmp/fk-localsend-progress");
        var entry = new FileSystemEntry { FullPath = "/private/tmp/fk-localsend-progress/file.txt", Name = "file.txt" };
        files.Seed(entry);
        var service = new FakeLocalSend();
        using var vm = CreateViewModel(files, sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List }, localSendService: service);
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var list = view.FindControl<FastFileList>("FastList")!;
            var origin = list.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + 70, origin.Y + 15);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            var sendTo = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "发送到");
            sendTo.IsSubMenuOpen = true;
            var localSend = Assert.Single(sendTo.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "LocalSend");
            localSend.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();

            var first = new LocalSendDevice("Live A", new string('a', 64), "127.0.0.1", 53317, "https");
            var second = new LocalSendDevice("Live B", new string('b', 64), "127.0.0.2", 53317, "https");
            service.ProgressCallbacks[0]!([first]);
            Dispatcher.UIThread.RunJobs();
            var originalRow = Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A");
            Assert.Contains(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "正在搜索…");
            service.ProgressCallbacks[0]!([first, second]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(originalRow, Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A"));

            service.StopDiscovery(0);
            for (var attempt = 0; attempt < 20 && !localSend.Items.OfType<MenuItem>()
                     .Any(item => item.Header?.ToString() == "搜索已停止"); attempt++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.DoesNotContain(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A");
            Assert.Contains(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "通过 IP 连接…");

            var refresh = Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "刷新");
            refresh.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, service.ProgressCallbacks.Count);
            service.ProgressCallbacks[0]!([new LocalSendDevice("Old result", new string('c', 64), "127.0.0.3", 53317, "https")]);
            var updated = first with { Alias = "Live A updated", Address = "127.0.0.4" };
            service.ProgressCallbacks[1]!([updated]);
            Dispatcher.UIThread.RunJobs();
            var updatedRow = Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A updated");
            service.ProgressCallbacks[1]!([updated, second]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(updatedRow, Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A updated"));
            Assert.DoesNotContain(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Old result");

            var moved = updated with { Address = "127.0.0.6" };
            service.ProgressCallbacks[1]!([moved, second]);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(updatedRow, Assert.Single(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "Live A updated"));
            updatedRow.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var sent = await service.Sent.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("127.0.0.6", sent.Device.Address);
            Assert.Equal([entry.FullPath], sent.Paths);
            service.ProgressCallbacks[1]!([new LocalSendDevice("After close", new string('d', 64), "127.0.0.5", 53317, "https")]);
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain(localSend.Items.OfType<MenuItem>(), item => item.Header?.ToString() == "After close");
        }
        finally
        {
            window.Close();
            service.FinishDiscovery(0);
            if (service.ProgressCallbacks.Count > 1) service.FinishDiscovery(1);
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void ManualAddressDialogKeepsActionsVisibleWithLongStatus()
    {
        var dialog = new LocalSendAddressDialog(new FakeLocalSend());
        dialog.Styles.Add(new FluentTheme());
        dialog.FindControl<TextBlock>("StatusText")!.Text = string.Join(' ', Enumerable.Repeat("连接失败：设备名称很长", 30));
        dialog.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            var button = dialog.FindControl<Button>("ConnectButton")!;
            var bottom = button.TranslatePoint(new Point(0, button.Bounds.Height), dialog);
            Assert.NotNull(bottom);
            Assert.True(bottom.Value.Y <= dialog.ClientSize.Height + 1);
            Assert.True(dialog.Bounds.Height <= 300);
        }
        finally { dialog.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private sealed class FakeLocalSend : ILocalSendService
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<LocalSendDevice>>> _discoveries = [];
        public readonly List<Action<IReadOnlyList<LocalSendDevice>>?> ProgressCallbacks = [];
        public bool IsEnabled = true;
        public bool DiscoveryStarted;
        public bool DiscoveryCancelled;
        public TaskCompletionSource DiscoveryReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<(LocalSendDevice Device, IReadOnlyList<string> Paths)> Sent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Enabled => IsEnabled;
        public string? LastError => null;
        public int ListeningPort => 53317;
        public string Alias { get; set; } = "Test";
        public string ReceiveDirectory { get; set; } = "/private/tmp";
        public string ScanSubnet { get; set; } = "";
        public Func<LocalSendIncomingRequest, CancellationToken, Task<LocalSendReceiveDecision>>? ConfirmReceiveAsync { get; set; }
        public Func<CancellationToken, Task<string?>>? RequestPinAsync { get; set; }
        public Task StartAsync() => Task.CompletedTask;
        public Task<LocalSendDevice> ConnectByAddressAsync(string address, int port, CancellationToken token)
            => Task.FromResult(new LocalSendDevice("Manual device", new string('b', 64), address, port, "https"));
        public Task SetEnabledAsync(bool enabled) { IsEnabled = enabled; return Task.CompletedTask; }
        public async Task<IReadOnlyList<LocalSendDevice>> DiscoverAsync(CancellationToken token,
            Action<IReadOnlyList<LocalSendDevice>>? onDevicesChanged = null)
        {
            DiscoveryStarted = true;
            var completion = new TaskCompletionSource<IReadOnlyList<LocalSendDevice>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _discoveries.Add(completion);
            ProgressCallbacks.Add(onDevicesChanged);
            using var registration = token.Register(() => DiscoveryCancelled = true);
            var devices = await completion.Task;
            DiscoveryReturned.TrySetResult();
            return devices;
        }
        public void FinishDiscovery(int? index = null) => _discoveries[index ?? (_discoveries.Count - 1)]
            .TrySetResult([new LocalSendDevice("Late device", new string('a', 64), "127.0.0.1", 53317, "https")]);
        public void StopDiscovery(int index) => _discoveries[index].TrySetCanceled();
        public Task SendAsync(LocalSendDevice device, IReadOnlyList<string> paths)
        {
            Sent.TrySetResult((device, paths));
            return Task.CompletedTask;
        }
    }
}
