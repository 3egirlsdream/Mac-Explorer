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
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "AirDrop")]
public sealed class AirDropMenuTests : FileListViewModelTestBase
{
    private const string Root = "/private/tmp/fk-airdrop-menu";
    private static FileSystemEntry Entry(string name) => new() { FullPath = Root + "/" + name, Name = name };

    [AvaloniaFact(Explicit = true)]
    public async Task AirDropIsIndependentOfLocalSendAndCoexistsWhenEnabled()
    {
        var entry = Entry("one.txt");
        var files = new FakeFileService(Root);
        var localSend = new FakeLocalSend { IsEnabled = false };
        using var vm = CreateViewModel(files, localSendService: localSend, airDropService: new FakeAirDrop());
        var send = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label == "发送");
        var airDrop = Assert.Single(send.SubItems!);
        Assert.Equal("airdrop-send", airDrop.Tag);
        Assert.NotNull(airDrop.IconImage);
        localSend.IsEnabled = true;
        send = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label == "发送");
        Assert.Equal(["隔空投送", "LocalSend"], send.SubItems!.Select(a => a.Label));
        using var noLocalSend = CreateViewModel(files, airDropService: new FakeAirDrop());
        Assert.NotNull(await AirDropAction(noLocalSend, entry));
    }

    [AvaloniaFact(Explicit = true)]
    public async Task UnsupportedPlatformRetainsOnlyEnabledLocalSend()
    {
        var entry = Entry("one.txt");
        var files = new FakeFileService(Root);
        var localSend = new FakeLocalSend();
        using var vm = CreateViewModel(files, localSendService: localSend,
            airDropService: new FakeAirDrop { IsSupported = false });
        var send = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label == "发送");
        Assert.Equal("LocalSend", Assert.Single(send.SubItems!).Label);
        localSend.IsEnabled = false;
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label == "发送");
    }

    [AvaloniaTheory(Explicit = true)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SendsSingleOrMultipleFilesUsingTheMenuSelectionSnapshot(bool multiple)
    {
        var files = new FakeFileService(Root);
        var first = Entry("含 空格.txt");
        var second = Entry("second.txt");
        files.Seed(first);
        files.Seed(second);
        var service = new FakeAirDrop();
        using var vm = CreateViewModel(files, airDropService: service);
        await vm.RefreshAsync();
        vm.SelectEntry(first, false, false);
        if (multiple) vm.SelectEntry(second, true, false);
        var action = await AirDropAction(vm, first);
        vm.ClearSelection();
        await action.Execute!();
        Assert.Null(service.Paths);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(multiple ? [first.FullPath, second.FullPath] : [first.FullPath], service.Paths);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task RightClickingAnUnselectedFileSendsOnlyThatFile()
    {
        var files = new FakeFileService(Root);
        var first = Entry("one.txt");
        var second = Entry("two.txt");
        files.Seed(first);
        files.Seed(second);
        var service = new FakeAirDrop();
        using var vm = CreateViewModel(files, airDropService: service);
        await vm.RefreshAsync();
        vm.SelectEntry(first, false, false);
        await (await AirDropAction(vm, second)).Execute!();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([second.FullPath], service.Paths);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ExcludesRemoteVirtualArchiveTrashBrowseAndMixedSelections()
    {
        var files = new FakeFileService(Root);
        var local = Entry("local.txt");
        var remote = new FileSystemEntry { FullPath = VirtualPath.BuildRemotePath("host", "/remote.txt"), Name = "remote.txt" };
        using var vm = CreateViewModel(files, airDropService: new FakeAirDrop());
        foreach (var entry in new[] { remote, new FileSystemEntry { FullPath = "sftp://host/a.txt", Name = "a.txt" },
                     new FileSystemEntry { FullPath = "relative.txt", Name = "relative.txt" },
                     new FileSystemEntry { FullPath = Root + "/virtual.txt", Name = "virtual.txt", IsVirtual = true } })
            Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label == "发送");
        vm.SelectedEntries.Add(local);
        vm.SelectedEntries.Add(remote);
        Assert.DoesNotContain(await vm.LoadCompleteFileContextMenuAsync(local), a => a.Label == "发送");
        var archiveNavigation = new NavigationViewModel(files) { CurrentPath = Root, IsHomePage = false, IsArchiveView = true };
        using var archive = CreateViewModel(files, navigation: archiveNavigation, airDropService: new FakeAirDrop());
        Assert.DoesNotContain(await archive.LoadCompleteFileContextMenuAsync(local), a => a.Label == "发送");
        using var browse = CreateViewModel(files, browseOnly: true, airDropService: new FakeAirDrop());
        Assert.Empty(await browse.LoadCompleteFileContextMenuAsync(local));
        var trashNavigation = new NavigationViewModel(files) { CurrentPath = vm.TrashPath, IsHomePage = false };
        using var trash = CreateViewModel(files, navigation: trashNavigation, airDropService: new FakeAirDrop());
        Assert.DoesNotContain(await trash.LoadCompleteFileContextMenuAsync(local), a => a.Label == "发送");
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ReopenedMenuUsesCurrentLanguageAndFailuresAreReported()
    {
        using var language = new LocalizationService(new PluginTestEnvironment.MemorySettings());
        language.SetLanguage(AppLanguage.ChineseSimplified);
        var entry = Entry("file.txt");
        var service = new FakeAirDrop();
        using var vm = CreateViewModel(new FakeFileService(Root), airDropService: service);
        Assert.Equal("隔空投送", (await AirDropAction(vm, entry)).Label);
        language.SetLanguage(AppLanguage.English);
        var action = await AirDropAction(vm, entry);
        Assert.Equal("AirDrop", action.Label);
        service.Error = new IOException("AirDrop test failure");
        await action.Execute!();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(service.Error.Message, vm.StatusText);
        service.Error = null;
        vm.StatusText = "unchanged";
        await (await AirDropAction(vm, entry)).Execute!();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("unchanged", vm.StatusText);
    }

    [AvaloniaFact(Explicit = true)]
    public async Task ClickingAirDropClosesMenuBeforeStartingSystemSharing()
    {
        var entry = Entry("file.txt");
        var service = new FakeAirDrop();
        using var vm = CreateViewModel(new FakeFileService(Root), airDropService: service,
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List });
        vm.Entries.Add(entry);
        var view = new FileListView { DataContext = vm };
        var window = new Window { Width = 900, Height = 360, Content = view };
        window.Styles.Add(new FluentTheme());
        window.Show();
        vm.SetOwnerWindow(window);
        try
        {
            Dispatcher.UIThread.RunJobs();
            var origin = view.FindControl<FastFileList>("FastList")!.TranslatePoint(default, window)!.Value;
            var point = new Point(origin.X + 70, origin.Y + 15);
            window.MouseDown(point, MouseButton.Right, RawInputModifiers.RightMouseButton);
            window.MouseUp(point, MouseButton.Right, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            var menu = Assert.Single(window.GetVisualDescendants().OfType<ContextMenu>());
            var send = Assert.Single(menu.Items.OfType<MenuItem>(), i => i.Header?.ToString() == "发送");
            send.IsSubMenuOpen = true;
            Dispatcher.UIThread.RunJobs();
            var airDrop = Assert.Single(send.Items.OfType<MenuItem>(), i => i.Header?.ToString() == "隔空投送");
            Assert.NotNull(Assert.IsType<Image>(airDrop.Icon).Source);
            service.OnSend = () => Assert.False(menu.IsOpen);
            airDrop.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal([entry.FullPath], service.Paths);
            Assert.Same(window, service.Owner);
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static async Task<ContextMenuAction> AirDropAction(FileListViewModel vm, FileSystemEntry entry)
    {
        var send = Assert.Single(await vm.LoadCompleteFileContextMenuAsync(entry), a => a.Label is "发送" or "Send");
        return Assert.Single(send.SubItems!, a => a.Tag == "airdrop-send");
    }

    private sealed class FakeAirDrop : IAirDropService
    {
        public bool IsSupported { get; set; } = true;
        public IReadOnlyList<string>? Paths { get; private set; }
        public Window? Owner { get; private set; }
        public Action? OnSend { get; set; }
        public Exception? Error { get; set; }
        public Task SendAsync(Window? owner, IReadOnlyList<string> paths)
        {
            OnSend?.Invoke();
            Owner = owner;
            Paths = paths.ToArray();
            return Error == null ? Task.CompletedTask : Task.FromException(Error);
        }
    }
}
