using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using MacExplorer.Indexing;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.ViewModels;
using Renci.SshNet;
using Xunit;

namespace MacExplorer.Tests;

public sealed class FileListLoadingPipelineTests
{
    private const string TestHome = "/tmp/FKFinderPipelineTests";

    [AvaloniaFact]
    public async Task RefreshAsync_LargeDirectory_PublishesCompleteSortedCollectionOnce()
    {
        const int totalEntries = 40 * 256;
        var all = new List<FileSystemEntry>(totalEntries);
        for (var i = 0; i < 240; i++)
            all.Add(MakeEntry(TestHome, $"dir-{i:D4}", isDirectory: true, extension: ""));
        for (var i = 0; i < totalEntries - 240; i++)
            all.Add(MakeEntry(TestHome, $"file-{i:D5}.txt"));
        Shuffle(new Random(20240601), all);

        var viewModel = CreateViewModel(new StreamingFakeFileService(TestHome, all));

        var entriesPropertyChanges = 0;
        var streamedResets = 0;
        var streamedAdds = 0;
        var streamedAddedEntries = new List<FileSystemEntry>();
        ObservableCollection<FileSystemEntry>? streamed = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(FileListViewModel.Entries)) return;
            entriesPropertyChanges++;
            if (streamed != null && !ReferenceEquals(streamed, viewModel.Entries))
                streamed.CollectionChanged -= OnStreamedCollectionChanged;
            streamed = viewModel.Entries;
            streamed.CollectionChanged += OnStreamedCollectionChanged;
        };

        await viewModel.RefreshAsync();

        // No partial membership or per-file notifications reach the visible list.
        Assert.Equal(totalEntries, viewModel.Entries.Count);
        Assert.Equal(1, entriesPropertyChanges);
        Assert.Equal(0, streamedResets);
        Assert.Empty(streamedAddedEntries);
        Assert.Equal(0, streamedAdds);
        Assert.All(streamedAddedEntries, e => Assert.False(string.IsNullOrEmpty(e.FullPath)));

        var expected = all
            .OrderBy(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => e.FullPath)
            .ToList();
        Assert.Equal(expected, viewModel.Entries.Select(e => e.FullPath).ToList());

        void OnStreamedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
        {
            switch (args.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    if (args.NewItems != null)
                    {
                        streamedAdds += args.NewItems.Count;
                        streamedAddedEntries.AddRange(args.NewItems.Cast<FileSystemEntry>());
                    }
                    break;
                case NotifyCollectionChangedAction.Reset:
                    streamedResets++;
                    break;
            }
        }
    }

    [AvaloniaFact]
    public async Task RemoteNavigationAndRefresh_UseBatchApiAndPublishOnlyAfterCompletion()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        const string serverId = "test-server";
        const string remotePath = "/large";
        var remote = new StreamingFakeRemoteFileService();
        var initial = Enumerable.Range(0, 600)
            .Select(i => MakeRemoteEntry(serverId, remotePath, $"file-{i:D4}.txt"))
            .ToArray();
        var initialScenario = remote.SetScenario(remotePath, initial, pauseAfterFirstBatch: true);
        var viewModel = CreateViewModel(
            new StreamingFakeFileService(TestHome, []),
            remote,
            new ConnectedRemoteService(serverId));

        var navigation = viewModel.NavigateToAsync(VirtualPath.BuildRemotePath(serverId, remotePath));
        await initialScenario.AfterFirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.False(navigation.IsCompleted);
        Assert.True(viewModel.IsDirectoryLoading);
        Assert.Empty(viewModel.Entries);
        Assert.Equal(1, remote.BatchCallCount);
        Assert.Equal(0, remote.FullCallCount);

        initialScenario.Release.TrySetResult();
        await navigation.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        Assert.Equal(600, viewModel.Entries.Count);
        Assert.False(viewModel.IsDirectoryLoading);

        var refreshed = Enumerable.Range(0, 300)
            .Select(i => MakeRemoteEntry(serverId, remotePath, $"fresh-{i:D4}.txt"))
            .ToArray();
        remote.SetScenario(remotePath, refreshed, pauseAfterFirstBatch: false);

        await viewModel.RefreshAsync().WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(2, remote.BatchCallCount);
        Assert.Equal(0, remote.FullCallCount);
        Assert.Equal(refreshed.Select(entry => entry.FullPath).Order(), viewModel.Entries.Select(entry => entry.FullPath).Order());
    }

    [AvaloniaFact]
    public async Task RemoteDirectorySwitch_CancelsOldProducerWithoutPollutingNewDirectory()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        const string serverId = "test-server";
        var remote = new StreamingFakeRemoteFileService();
        var slowScenario = remote.SetScenario(
            "/slow",
            Enumerable.Range(0, 600).Select(i => MakeRemoteEntry(serverId, "/slow", $"old-{i:D4}.txt")).ToArray(),
            pauseAfterFirstBatch: true);
        var freshEntries = Enumerable.Range(0, 40)
            .Select(i => MakeRemoteEntry(serverId, "/fresh", $"new-{i:D4}.txt"))
            .ToArray();
        remote.SetScenario("/fresh", freshEntries, pauseAfterFirstBatch: false);
        var viewModel = CreateViewModel(
            new StreamingFakeFileService(TestHome, []),
            remote,
            new ConnectedRemoteService(serverId));

        var slowNavigation = viewModel.NavigateToAsync(VirtualPath.BuildRemotePath(serverId, "/slow"));
        await slowScenario.AfterFirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        var freshNavigation = viewModel.NavigateToAsync(VirtualPath.BuildRemotePath(serverId, "/fresh"));

        await Task.WhenAll(slowNavigation, freshNavigation).WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(VirtualPath.BuildRemotePath(serverId, "/fresh"), viewModel.CurrentPath);
        Assert.Equal(freshEntries.Select(entry => entry.FullPath).Order(), viewModel.Entries.Select(entry => entry.FullPath).Order());
        Assert.DoesNotContain(viewModel.Entries, entry => entry.Name.StartsWith("old-", StringComparison.Ordinal));
        Assert.False(viewModel.IsLoading);
        Assert.False(viewModel.IsDirectoryLoading);
    }

    [AvaloniaFact]
    public async Task RemoteRefresh_RestoresSelectionThatArrivesAfterFirstBatch()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        const string serverId = "test-server";
        const string remotePath = "/selection";
        var entries = Enumerable.Range(0, 600)
            .Select(i => MakeRemoteEntry(serverId, remotePath, $"file-{i:D4}.txt"))
            .ToArray();
        var remote = new StreamingFakeRemoteFileService();
        remote.SetScenario(remotePath, entries, pauseAfterFirstBatch: false);
        var viewModel = CreateViewModel(
            new StreamingFakeFileService(TestHome, []),
            remote,
            new ConnectedRemoteService(serverId));
        await viewModel.NavigateToAsync(VirtualPath.BuildRemotePath(serverId, remotePath));
        var selectedPath = entries[500].FullPath;
        viewModel.SelectEntry(Assert.Single(viewModel.Entries, entry => entry.FullPath == selectedPath));

        var refreshEntries = entries.Select(CloneEntry).ToArray();
        var refreshScenario = remote.SetScenario(remotePath, refreshEntries, pauseAfterFirstBatch: true);
        var refresh = viewModel.RefreshAsync();
        await refreshScenario.AfterFirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(selectedPath, Assert.Single(viewModel.SelectedEntries).FullPath);
        refreshScenario.Release.TrySetResult();
        await refresh.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        var restored = Assert.Single(viewModel.SelectedEntries);
        Assert.Equal(selectedPath, restored.FullPath);
        Assert.Contains(restored, viewModel.Entries);
    }

    [AvaloniaFact]
    public async Task RemoteNavigation_ConsumesPendingSelectionOnlyAfterLaterBatchArrives()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        const string serverId = "test-server";
        const string remotePath = "/pending";
        var entries = Enumerable.Range(0, 600)
            .Select(i => MakeRemoteEntry(serverId, remotePath, $"file-{i:D4}.txt"))
            .ToArray();
        var remote = new StreamingFakeRemoteFileService();
        var scenario = remote.SetScenario(remotePath, entries, pauseAfterFirstBatch: true);
        var viewModel = CreateViewModel(
            new StreamingFakeFileService(TestHome, []),
            remote,
            new ConnectedRemoteService(serverId));
        viewModel.PendingSelectFileName = entries[500].Name;

        var navigation = viewModel.NavigateToAsync(VirtualPath.BuildRemotePath(serverId, remotePath));
        await scenario.AfterFirstBatch.Task.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Equal(entries[500].Name, viewModel.PendingSelectFileName);
        Assert.Empty(viewModel.SelectedEntries);
        scenario.Release.TrySetResult();
        await navigation.WaitAsync(TimeSpan.FromSeconds(2), testCancellation);

        Assert.Null(viewModel.PendingSelectFileName);
        Assert.Equal(entries[500].FullPath, Assert.Single(viewModel.SelectedEntries).FullPath);
    }

    [Fact]
    public async Task MacFileService_EarlyEnumeratorDisposal_CancelsBoundedProducerPromptly()
    {
        var testCancellation = TestContext.Current.CancellationToken;
        var directory = Path.Combine(Path.GetTempPath(), $"fkfinder-producer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < 4096; i++)
                await File.WriteAllBytesAsync(Path.Combine(directory, $"file-{i:D4}.txt"), [], testCancellation);

            var enumerator = new MacFileService()
                .EnumerateDirectoryBatchesAsync(directory, 32, testCancellation)
                .GetAsyncEnumerator(testCancellation);
            Assert.True(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), testCancellation));

            await enumerator.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), testCancellation);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(SortField.Name, true, 1)]
    [InlineData(SortField.Name, false, 2)]
    [InlineData(SortField.Modified, true, 3)]
    [InlineData(SortField.Modified, false, 4)]
    [InlineData(SortField.Size, true, 5)]
    [InlineData(SortField.Size, false, 6)]
    [InlineData(SortField.Type, true, 7)]
    [InlineData(SortField.Type, false, 8)]
    public void InsertBatch_ProducesSameOrderAndGroupsAsFullSort(SortField sortField, bool ascending, int seed)
    {
        var entries = BuildUniqueEntries(10_240);
        var order = Enumerable.Range(0, entries.Count).ToArray();
        Shuffle(new Random(seed), order);
        var batches = new List<IReadOnlyList<FileSystemEntry>>();
        const int batchSize = 256;
        for (var i = 0; i < order.Length; i += batchSize)
            batches.Add(order.Skip(i).Take(batchSize).Select(index => entries[index]).ToArray());

        foreach (var groupField in new[] { GroupField.None, GroupField.Type, GroupField.Modified, GroupField.Size })
        {
            var full = ConfigureSort(sortField, ascending, groupField);
            full.SetRawEntries(entries);
            var fullEntries = ObserveFullApply(full);

            var streamed = ConfigureSort(sortField, ascending, groupField);
            var (streamedEntries, _) = RunStreamedPipeline(streamed, batches);

            Assert.Equal(
                fullEntries.Select(e => e.FullPath).ToList(),
                streamedEntries.Select(e => e.FullPath).ToList());
            Assert.Equal(
                full.Groups.Select(g => g.Name).ToList(),
                streamed.Groups.Select(g => g.Name).ToList());
            Assert.Equal(
                full.Groups.Select(g => g.Entries.Select(e => e.FullPath).ToList()).ToList(),
                streamed.Groups.Select(g => g.Entries.Select(e => e.FullPath).ToList()).ToList());
        }
    }

    [Fact]
    public void InsertBatch_FiltersTemporarySystemAndDotEntries()
    {
        var sortFilter = new SortFilterViewModel();
        var normal = MakeEntry(TestHome, "normal.txt");
        var keep = MakeEntry(TestHome, "pass.txt");
        var batch = new[]
        {
            MakeEntry(TestHome, "note.tmp.fkfinder-tmp"),
            MakeEntry(TestHome, ".DS_Store"),
            MakeEntry(TestHome, ".hidden"),
            MakeEntry(TestHome, ".hiddendir", isDirectory: true),
            keep,
        };
        var target = new ObservableCollection<FileSystemEntry>([normal]);

        sortFilter.InsertBatch(batch, target);

        Assert.Equal(["normal.txt", "pass.txt"], target.Select(e => e.Name).ToList());
    }

    private static ObservableCollection<FileSystemEntry> ObserveFullApply(SortFilterViewModel sortFilter)
    {
        ObservableCollection<FileSystemEntry> entries = [];
        sortFilter.ApplySortAndGroup(list => entries = list);
        return entries;
    }

    private static (ObservableCollection<FileSystemEntry> Entries, SortFilterViewModel SortFilter) RunStreamedPipeline(
        SortFilterViewModel sortFilter, IReadOnlyList<IReadOnlyList<FileSystemEntry>> batches)
    {
        ObservableCollection<FileSystemEntry> entries = [];
        sortFilter.ApplySortAndGroup(list => entries = list);
        var accumulated = new List<FileSystemEntry>();
        for (var i = 0; i < batches.Count; i++)
        {
            accumulated.AddRange(batches[i]);
            sortFilter.SetRawEntries(accumulated);
            if (i == 0)
                sortFilter.ApplySortAndGroup(list => entries = list);
            else
                sortFilter.InsertBatch(batches[i], entries);
        }
        return (entries, sortFilter);
    }

    private static SortFilterViewModel ConfigureSort(SortField field, bool ascending, GroupField groupField)
    {
        var sortFilter = new SortFilterViewModel();
        sortFilter.SetSort(field, ascending);
        sortFilter.GroupField = groupField;
        return sortFilter;
    }

    private static FileSystemEntry MakeEntry(
        string directory,
        string name,
        bool isDirectory = false,
        string extension = ".txt",
        long size = 0,
        DateTime lastModified = default) => new()
    {
        FullPath = $"{directory}/{name}",
        Name = name,
        Extension = extension,
        IsDirectory = isDirectory,
        Size = size,
        LastModified = lastModified,
        IconKey = isDirectory ? "folder" : "file-text",
    };

    private static FileSystemEntry MakeRemoteEntry(string serverId, string directory, string name) => new()
    {
        FullPath = VirtualPath.BuildRemotePath(serverId, $"{directory.TrimEnd('/')}/{name}"),
        Name = name,
        Extension = Path.GetExtension(name),
        IsDirectory = false,
        IconKey = "file-text",
    };

    private static FileSystemEntry CloneEntry(FileSystemEntry entry) => new()
    {
        FullPath = entry.FullPath,
        Name = entry.Name,
        Extension = entry.Extension,
        IsDirectory = entry.IsDirectory,
        Size = entry.Size,
        LastModified = entry.LastModified,
        IconKey = entry.IconKey,
    };

    private static List<FileSystemEntry> BuildUniqueEntries(int count)
    {
        string[] extensions = [".txt", ".png", ".mp4", ".cs", ".md", ""];
        var now = DateTime.Now;
        var entries = new List<FileSystemEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var isDirectory = i % 5 == 0;
            var extension = isDirectory ? "" : extensions[i % extensions.Length];
            var name = isDirectory ? $"dir-{i:D4}" : $"file-{i:D4}{extension}";
            entries.Add(MakeEntry(
                TestHome, name, isDirectory, extension,
                UniqueSize(i), now - TimeSpan.FromMinutes(i * 7)));
        }
        return entries;
    }

    // Distinct sizes across every bucket so Size sorting has no ties.
    private static long UniqueSize(int i) => (i % 7) switch
    {
        0 => 2_000_000_000L + i,
        1 => 500_000_000L + i,
        2 => 50_000_000L + i,
        3 => 500_000L + i,
        4 => 500L + i,
        5 => i == 5 ? 0L : 900_000L + i,
        _ => 5_000_000L + i,
    };

    private static void Shuffle<T>(Random random, IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static FileListViewModel CreateViewModel(
        IFileService fileService,
        IRemoteFileService? remoteFileService = null,
        IRemoteConnectionService? remoteConnectionService = null)
    {
        var navigation = new NavigationViewModel(fileService)
        {
            CurrentPath = fileService.HomeDirectory,
            IsHomePage = false
        };
        var index = new FakeFileIndex();
        var writer = new FakeFileIndexWriter();
        var fileOps = new FileOpsViewModel(
            fileService: fileService,
            directoryChangeNotifier: null);

        return new FileListViewModel(
            navigation,
            fileOps,
            new SearchViewModel(),
            new ArchiveViewModel(fileService: fileService),
            new AiViewModel(fileIndex: index),
            new PinnedFoldersViewModel(),
            new SortFilterViewModel(),
            fileService,
            index,
            writer,
            new IndexConfiguration(),
            directoryChangeNotifier: null,
            remoteConnectionService: remoteConnectionService,
            sftpFileService: remoteFileService);
    }

    private sealed class StreamingFakeFileService(string homeDirectory, IReadOnlyList<FileSystemEntry> entries) : IFileService
    {
        public string HomeDirectory { get; } = homeDirectory;
        public string RootDirectory => "/";
        public string TrashDirectory => Path.Combine(HomeDirectory, ".Trash");

        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string path, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
            string path,
            int batchSize = 256,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            for (var i = 0; i < entries.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return entries.Skip(i).Take(batchSize).ToArray();
            }
        }

        public Task<FileSystemEntry?> GetEntryAsync(string path) => Task.FromResult<FileSystemEntry?>(null);
        public Task<bool> ExistsAsync(string path) => Task.FromResult(true);
        public Task<string> CreateFolderAsync(string parentPath, string name) => throw new NotSupportedException();
        public Task<string> CreateFileAsync(string parentPath, string name) => throw new NotSupportedException();
        public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool moveToTrash = true) => throw new NotSupportedException();
        public Task RenameAsync(string path, string newName) => throw new NotSupportedException();
        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite = false) => throw new NotSupportedException();
        public Task CopyAsync(string sourcePath, string destinationDirectory) => throw new NotSupportedException();
        public string GetParentPath(string path) => Path.GetDirectoryName(path) ?? "";
        public string CombinePath(string directory, string name) => Path.Combine(directory, name);
        public IReadOnlyList<string> GetVolumes() => [];
        public Task DeletePermanentlyAsync(string path) => throw new NotSupportedException();
        public Task EmptyTrashAsync() => throw new NotSupportedException();
        public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsCrossVolume(string sourcePath, string destinationPath) => false;
        public Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StreamingFakeRemoteFileService : IRemoteFileService
    {
        private readonly Dictionary<string, RemoteScenario> _scenarios = new(StringComparer.Ordinal);

        public int BatchCallCount { get; private set; }
        public int FullCallCount { get; private set; }
        public string? CurrentServerId { get; private set; }
        public string HomeDirectory => "/";
        public string RootDirectory => "/";
        public string TrashDirectory => "__remote_trash__";

        public RemoteScenario SetScenario(
            string path,
            IReadOnlyList<FileSystemEntry> entries,
            bool pauseAfterFirstBatch)
        {
            var scenario = new RemoteScenario(entries, pauseAfterFirstBatch);
            _scenarios[path] = scenario;
            return scenario;
        }

        public void SetCurrentServer(string serverId) => CurrentServerId = serverId;
        public SftpClient? GetConnectedClient() => null;

        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(
            string path,
            CancellationToken cancellationToken = default)
        {
            FullCallCount++;
            throw new InvalidOperationException("The full remote listing API must not be used by the loading pipeline.");
        }

        public async IAsyncEnumerable<IReadOnlyList<FileSystemEntry>> EnumerateDirectoryBatchesAsync(
            string path,
            int batchSize = 256,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            var scenario = _scenarios[path];
            for (var i = 0; i < scenario.Entries.Count; i += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return scenario.Entries.Skip(i).Take(batchSize).ToArray();
                if (i == 0 && scenario.PauseAfterFirstBatch)
                {
                    scenario.AfterFirstBatch.TrySetResult();
                    await scenario.Release.Task.WaitAsync(cancellationToken);
                }
            }
        }

        public Task<FileSystemEntry?> GetEntryAsync(string path) => Task.FromResult<FileSystemEntry?>(null);
        public Task<bool> ExistsAsync(string path) => Task.FromResult(true);
        public Task<string> CreateFolderAsync(string parentPath, string name) => throw new NotSupportedException();
        public Task<string> CreateFileAsync(string parentPath, string name) => throw new NotSupportedException();
        public Task<string> CreateFileWithContentAsync(string parentPath, string name, byte[] content) => throw new NotSupportedException();
        public Task DeleteAsync(string path, bool moveToTrash = true) => throw new NotSupportedException();
        public Task RenameAsync(string path, string newName) => throw new NotSupportedException();
        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite = false) => throw new NotSupportedException();
        public Task CopyAsync(string sourcePath, string destinationDirectory) => throw new NotSupportedException();
        public string GetParentPath(string path) => "/";
        public string CombinePath(string directory, string name) => $"{directory.TrimEnd('/')}/{name}";
        public IReadOnlyList<string> GetVolumes() => [];
        public Task DeletePermanentlyAsync(string path) => throw new NotSupportedException();
        public Task EmptyTrashAsync() => throw new NotSupportedException();
        public Task ResolveAppIconsAsync(IEnumerable<FileSystemEntry> entries, Action? onBatchResolved = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public bool IsCrossVolume(string sourcePath, string destinationPath) => false;
        public Task MoveWithProgressAsync(IReadOnlyList<string> sourcePaths, string destinationDirectory, IProgress<FileOperationProgress>? progress = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RemoteScenario(IReadOnlyList<FileSystemEntry> entries, bool pauseAfterFirstBatch)
    {
        public IReadOnlyList<FileSystemEntry> Entries { get; } = entries;
        public bool PauseAfterFirstBatch { get; } = pauseAfterFirstBatch;
        public TaskCompletionSource AfterFirstBatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ConnectedRemoteService(string serverId) : IRemoteConnectionService
    {
        private readonly RemoteServerInfo _server = new()
        {
            Id = serverId,
            Name = "Test server",
            IsConnected = true,
        };

        public Task<SftpClient> GetOrConnectAsync(RemoteServerInfo server, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SftpClient> ConnectAsync(RemoteServerInfo server, CancellationToken ct = default) => throw new NotSupportedException();
        public void Disconnect(string id) { }
        public void DisconnectAll() { }
        public bool IsConnected(string id) => string.Equals(id, serverId, StringComparison.Ordinal);
        public SftpClient? GetClient(string id) => null;
        public IReadOnlyList<RemoteServerInfo> GetSavedServers() => [_server];
        public void SaveServer(RemoteServerInfo server) { }
        public void RemoveServer(string id) { }
        public event EventHandler<string>? ConnectionLost { add { } remove { } }
        public event EventHandler<string>? Reconnecting { add { } remove { } }
        public event EventHandler<string>? ReconnectFailed { add { } remove { } }
    }

    private sealed class FakeFileIndex : IFileIndex
    {
        public Task<IReadOnlyList<FileSystemEntry>> GetDirectoryContentsAsync(string parentPath)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public Task<FileSystemEntry?> GetEntryAsync(string path)
            => Task.FromResult<FileSystemEntry?>(null);

        public Task<IReadOnlyList<FileSystemEntry>> SearchByNameAsync(string pattern, int limit = 100)
            => Task.FromResult<IReadOnlyList<FileSystemEntry>>([]);

        public Task<bool> IsDirectoryFreshAsync(string path, TimeSpan freshnessThreshold)
            => Task.FromResult(false);
    }

    private sealed class FakeFileIndexWriter : IFileIndexWriter
    {
        public Task UpdateDirectoryAsync(string directoryPath, IReadOnlyList<FileSystemEntry> entries, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task InvalidateDirectoriesAsync(IEnumerable<string> directoryPaths) => Task.CompletedTask;
        public Task RenameEntryAsync(string oldPath, string newPath, string newName) => Task.CompletedTask;
        public Task RemoveEntryAsync(string path) => Task.CompletedTask;
        public Task AddEntryAsync(FileSystemEntry entry) => Task.CompletedTask;
    }
}
