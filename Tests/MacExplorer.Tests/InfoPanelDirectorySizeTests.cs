using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Views;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "UI")]
[Trait("Area", "DirectorySize")]
public sealed class InfoPanelDirectorySizeTests : FileListViewModelTestBase
{
    [AvaloniaFact(Explicit = true)]
    public void ApplicationWithSystemSizeDoesNotStartARecursiveScan()
    {
        using var vm = CreateViewModel(new FakeFileService("/tmp/size-panel"));
        vm.IsInfoPanelVisible = true;
        var entry = new FileSystemEntry { FullPath = "/tmp/size-panel/Example.app", Name = "Example.app", IsDirectory = true, Size = 1768565 };
        vm.SelectedEntries.Add(entry);
        var calls = 0;
        var panel = new InfoPanelView(null, (_, _) => { calls++; return Task.FromResult(new DirectorySizeResult(0, true)); }) { DataContext = vm };
        var window = new Window { Content = panel };
        try
        {
            window.Show();
            Assert.Equal(0, calls);
            Assert.Equal(entry.FormattedSize, panel.FindControl<TextBlock>("InfoSize")!.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task OpeningPanelLoadsFolderSizeWithoutBlockingUiOrChangingListAndLocalizesTheResult()
    {
        var token = TestContext.Current.CancellationToken;
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.ChineseSimplified);
        using var vm = CreateViewModel(new FakeFileService("/tmp/size-panel"));
        var entry = new FileSystemEntry { FullPath = "/tmp/size-panel/folder", Name = "folder", IsDirectory = true };
        vm.SelectedEntries.Add(entry);
        var pending = new TaskCompletionSource<DirectorySizeResult>();
        var calls = 0;
        var panel = new InfoPanelView(null, (_, _) => { calls++; return pending.Task; }) { DataContext = vm };
        var window = new Window { Content = panel, Width = 320, Height = 700 };
        try
        {
            window.Show();
            Assert.Equal(0, calls);
            vm.IsInfoPanelVisible = true;
            Assert.Equal(1, calls);
            Assert.Equal("计算中…", panel.FindControl<TextBlock>("InfoSize")!.Text);
            var uiRan = false;
            await Dispatcher.UIThread.InvokeAsync(() => uiRan = true);
            Assert.True(uiRan);
            Assert.False(pending.Task.IsCompleted);
            pending.SetResult(new(2048, false));
            await UntilAsync(() => panel.FindControl<TextBlock>("InfoSize")!.Text == "至少 2 KB", token);
            Assert.Equal(0L, entry.Size);
            Assert.Equal("--", entry.FormattedSize);
            language.SetLanguage(AppLanguage.English);
            Assert.Equal("At least 2 KB", panel.FindControl<TextBlock>("InfoSize")!.Text);
            Assert.Contains("Some items", ToolTip.GetTip(panel.FindControl<TextBlock>("InfoSize")!)!.ToString());
        }
        finally { window.Close(); }
    }

    [AvaloniaFact(Explicit = true)]
    public async Task SelectionChangesAndClosingThePanelCancelRequestsAndIgnoreLateResults()
    {
        var token = TestContext.Current.CancellationToken;
        using var language = new LocalizationService(new StartupSettings(), () => AppLanguage.English);
        using var vm = CreateViewModel(new FakeFileService("/tmp/size-panel"));
        vm.IsInfoPanelVisible = true;
        var requests = new List<(CancellationToken Token, TaskCompletionSource<DirectorySizeResult> Result)>();
        var first = new FileSystemEntry { FullPath = "/tmp/size-panel/first", Name = "first", IsDirectory = true };
        var second = new FileSystemEntry { FullPath = "/tmp/size-panel/second", Name = "second", IsDirectory = true };
        vm.SelectedEntries.Add(first);
        var panel = new InfoPanelView(null, (_, cancellation) =>
        {
            var result = new TaskCompletionSource<DirectorySizeResult>();
            requests.Add((cancellation, result));
            return result.Task;
        }) { DataContext = vm };
        var window = new Window { Content = panel, Width = 320, Height = 700 };
        try
        {
            window.Show();
            Assert.Single(requests);
            vm.SelectedEntries[0] = second;
            Assert.True(requests[0].Token.IsCancellationRequested);
            await UntilAsync(() => requests.Count == 2, token);
            requests[1].Result.SetResult(new(2048, true));
            await UntilAsync(() => panel.FindControl<TextBlock>("InfoSize")!.Text == "2 KB", token);
            requests[0].Result.SetResult(new(9999, true));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("2 KB", panel.FindControl<TextBlock>("InfoSize")!.Text);

            vm.IsInfoPanelVisible = false;
            vm.IsInfoPanelVisible = true;
            Assert.Equal(3, requests.Count);
            vm.IsInfoPanelVisible = false;
            Assert.True(requests[2].Token.IsCancellationRequested);
            requests[2].Result.SetResult(new(4096, true));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("—", panel.FindControl<TextBlock>("InfoSize")!.Text);
        }
        finally
        {
            window.Close();
            foreach (var request in requests) request.Result.TrySetCanceled();
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, token);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(condition());
    }
}
