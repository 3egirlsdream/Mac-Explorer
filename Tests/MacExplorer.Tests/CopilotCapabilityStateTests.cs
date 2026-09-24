using System.Text.Json;
using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using MacExplorer.Views;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MacExplorer.Tests;

public sealed partial class FileListViewModelCreateTests
{
    [AvaloniaFact]
    public void ToolbarViewSwitchUsesRegisteredCapability()
    {
        var previousServices = App.Services;
        var registry = new AppCapabilityRegistry(null!, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        using var services = new ServiceCollection()
            .AddSingleton<IAppCapabilityRegistry>(registry).BuildServiceProvider();
        typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, services);
        using var pane = CreateViewModel(new FakeFileService("/tmp/FKFinderCopilotToolbar"),
            sortFilter: new SortFilterViewModel { ViewMode = ViewMode.List });
        var toolbar = new FinderToolbar { DataContext = pane };
        var window = new Window { Width = 900, Height = 120, Content = toolbar };
        try
        {
            window.Show();
            toolbar.FindControl<Button>("GridViewToggle")!
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(ViewMode.Grid, pane.ViewMode);
        }
        finally
        {
            window.Close();
            typeof(App).GetProperty(nameof(App.Services))!.SetValue(null, previousServices);
        }
    }

    [AvaloniaFact]
    public async Task CopilotPlanExpiresWhenSourceChangesOrPaneSwitches()
    {
        var files = new FakeFileService("/tmp/FKFinderCopilotCapabilityState");
        var path = Path.Combine(files.HomeDirectory, "sample.txt");
        var stamp = DateTime.UtcNow;
        files.Seed(new FileSystemEntry
        {
            FullPath = path, Name = "sample.txt", Size = 12, LastModified = stamp
        });
        using var pane = CreateViewModel(files);
        using var otherPane = CreateViewModel(files);
        var registry = new AppCapabilityRegistry(files, null!, null!, null!, null!, null!, null!,
            null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        var arguments = JsonSerializer.Serialize(new { path, rating = 3 });

        var switched = await registry.PreviewAsync("file.rating", arguments, pane);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteApprovedAsync(switched.Id, otherPane));

        var changed = await registry.PreviewAsync("file.rating", arguments, pane);
        files.Seed(new FileSystemEntry
        {
            FullPath = path, Name = "sample.txt", Size = 13, LastModified = stamp.AddSeconds(1)
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteApprovedAsync(changed.Id, pane));
    }

    [AvaloniaFact]
    public async Task ApprovedContentPagesExposeContinuationUntilEndOfFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-copilot-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "long.html");
            var content = new string('a', CopilotContentExtractor.MaxPageUtf8Bytes + 100);
            await File.WriteAllTextAsync(path, content);
            var files = new FakeFileService(root);
            files.Seed(new FileSystemEntry
            {
                FullPath = path, Name = "long.html", Size = new FileInfo(path).Length,
                LastModified = File.GetLastWriteTimeUtc(path)
            });
            using var pane = CreateViewModel(files);
            var registry = new AppCapabilityRegistry(
                files: files, pins: null!, search: null!, archives: null!, plugins: null!,
                market: null!, delivery: null!, tags: null!, changes: null!, remoteConnections: null!,
                appSettings: null!, themes: null!, interactionStyles: null!,
                contentExtractor: new CopilotContentExtractor(null!, null!),
                homeWorkspace: null!, scriptRunner: null!, deliveryController: null!,
                batchRename: null!, batchRenameOperation: null!);

            var firstPlan = await registry.PreviewAsync("file.content", JsonSerializer.Serialize(new { path }), pane);
            Assert.Contains("0 至 51200", firstPlan.Summary);
            var first = (CopilotContentExtractor.Page)(await registry.ExecuteApprovedAsync(firstPlan.Id, pane)).Data!;
            Assert.True(first.HasMore);
            Assert.Equal(CopilotContentExtractor.MaxPageUtf8Bytes, first.NextOffset);

            var secondPlan = await registry.PreviewAsync("file.content",
                JsonSerializer.Serialize(new { path, offset = first.NextOffset }), pane);
            var second = (CopilotContentExtractor.Page)(await registry.ExecuteApprovedAsync(secondPlan.Id, pane)).Data!;
            Assert.False(second.HasMore);
            Assert.Equal(content, first.Text + second.Text);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [AvaloniaFact]
    public async Task BatchRenamePlanUsesExistingPreviewAndRejectsChangedSource()
    {
        var files = new FakeFileService("/tmp/FKFinderCopilotBatchRename");
        var first = Path.Combine(files.HomeDirectory, "a.txt");
        var second = Path.Combine(files.HomeDirectory, "b.txt");
        var stamp = DateTime.UtcNow;
        files.Seed(new FileSystemEntry { FullPath = first, Name = "a.txt", Size = 1, LastModified = stamp });
        files.Seed(new FileSystemEntry { FullPath = second, Name = "b.txt", Size = 1, LastModified = stamp });
        using var pane = CreateViewModel(files);
        var rename = new BatchRenameService(files);
        var history = new FileOperationHistoryService(files);
        var registry = new AppCapabilityRegistry(
            files: files, pins: null!, search: null!, archives: null!, plugins: null!,
            market: null!, delivery: null!, tags: null!, changes: null!, remoteConnections: null!,
            appSettings: null!, themes: null!, interactionStyles: null!, contentExtractor: null!,
            homeWorkspace: null!, scriptRunner: null!, deliveryController: null!,
            batchRename: rename,
            batchRenameOperation: new MacExplorer.Services.BatchRenameOperationService(
                rename, history, new BackgroundTaskManager(null)));
        var args = JsonSerializer.Serialize(new
        {
            paths = new[] { first, second },
            rule = new { type = "AddPrefix", prefixText = "new-" }
        });
        var plan = await registry.PreviewAsync("file.batch-rename", args, pane);
        Assert.Contains("a.txt → new-a.txt", plan.Summary);
        Assert.Contains("b.txt → new-b.txt", plan.Summary);
        files.Seed(new FileSystemEntry { FullPath = first, Name = "a.txt", Size = 2, LastModified = stamp.AddSeconds(1) });
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.ExecuteApprovedAsync(plan.Id, pane));

        var approved = await registry.PreviewAsync("file.batch-rename", args, pane);
        var result = await registry.ExecuteApprovedAsync(approved.Id, pane);
        Assert.True(result.Success);
        Assert.Equal(2, ((System.Text.Json.JsonElement)JsonSerializer.SerializeToElement(result.Data))
            .GetProperty("SuccessfulItems").GetArrayLength());
        Assert.NotNull(await files.GetEntryAsync(Path.Combine(files.HomeDirectory, "new-a.txt")));
        Assert.True(history.CanUndo);
    }

    [AvaloniaFact]
    public async Task MarketInstallRejectsChangedPackageMetadataBeforeDownload()
    {
        using var plugins = new PluginTestEnvironment(initialize: false);
        using var handler = new MarketListingHandler();
        using var http = new HttpClient(handler);
        var market = new PluginMarketClient(http, "http://127.0.0.1:18548/");
        var registry = new AppCapabilityRegistry(
            files: null!, pins: null!, search: null!, archives: null!, plugins: plugins.Manager,
            market: market, delivery: null!, tags: null!, changes: null!, remoteConnections: null!,
            appSettings: null!, themes: null!, interactionStyles: null!, contentExtractor: null!,
            homeWorkspace: null!, scriptRunner: null!, deliveryController: null!,
            batchRename: null!, batchRenameOperation: null!);
        using var pane = CreateViewModel(new FakeFileService("/tmp/FKFinderCopilotMarket"));

        var plan = await registry.PreviewAsync("plugin.market-install",
            "{\"id\":\"test.market\",\"version\":\"1.0.0\"}", pane);
        Assert.Equal(CapabilityImpact.InstallSoftware, plan.Capability.Impact);
        handler.Sha256 = new string('b', 64);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            registry.ExecuteApprovedAsync(plan.Id, pane));
    }

    private sealed class MarketListingHandler : HttpMessageHandler
    {
        public string Sha256 { get; set; } = new('a', 64);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var item = new PluginMarketItem(new PluginManifest
            {
                Id = "test.market", Name = "测试市场插件", Version = "1.0.0",
                Developer = "Test", ApiVersion = PluginProtocol.ApiVersion
            }, "https://example.com/test.mexplug", 12, Sha256);
            var json = JsonSerializer.Serialize(new PluginMarketPage([item], false), PluginProtocol.Json);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
