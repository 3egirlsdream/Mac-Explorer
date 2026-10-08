using System.Text.Json;
using System.Diagnostics;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.Services.Plugins;
using MacExplorer.Services.Search;
using MacExplorer.Indexing;
using Microsoft.Data.Sqlite;
using Xunit;

namespace MacExplorer.Tests;

public sealed class DistributionCommonTests : IDisposable
{
    private readonly string _root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "channel-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public DistributionCommonTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void UpgradeCopiesCommittedWalAndPreservesSourceAndExistingDestination()
    {
        var oldPath = Path.Combine(_root, "old.db"); var newPath = Path.Combine(_root, "new.db");
        using var old = new SqliteConnection($"Data Source={oldPath};Pooling=False"); old.Open();
        using (var command = old.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE state (value TEXT); INSERT INTO state VALUES ('favorites-tabs-settings');";
            command.ExecuteNonQuery();
        }
        Assert.True(File.Exists(oldPath + "-wal"));
        ApplicationDataMigration.CopyDatabaseIfAbsent(oldPath, newPath);
        using var migrated = new SqliteConnection($"Data Source={newPath};Pooling=False"); migrated.Open();
        using var read = migrated.CreateCommand(); read.CommandText = "SELECT value FROM state";
        Assert.Equal("favorites-tabs-settings", read.ExecuteScalar());
        read.CommandText = "UPDATE state SET value='new-data'"; read.ExecuteNonQuery();
        ApplicationDataMigration.CopyDatabaseIfAbsent(oldPath, newPath);
        read.CommandText = "SELECT value FROM state"; Assert.Equal("new-data", read.ExecuteScalar());
        using var original = old.CreateCommand(); original.CommandText = "SELECT value FROM state";
        Assert.Equal("favorites-tabs-settings", original.ExecuteScalar());
    }

    [Fact]
    public void FailedUpgradeLeavesOriginalAndNoPartialDestination()
    {
        var oldPath = Path.Combine(_root, "corrupt.db"); var newPath = Path.Combine(_root, "new.db");
        File.WriteAllText(oldPath, "not a database");
        Assert.Throws<SqliteException>(() => ApplicationDataMigration.CopyDatabaseIfAbsent(oldPath, newPath));
        Assert.Equal("not a database", File.ReadAllText(oldPath));
        Assert.False(File.Exists(newPath));
        Assert.Empty(Directory.GetFiles(_root, "*.migration-*"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SchemaInitializationFailureCannotResetExistingUserState(bool useFactory)
    {
        var path = Path.Combine(_root, "existing-profile.db");
        using (var existing = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            existing.Open(); using var command = existing.CreateCommand();
            command.CommandText = "CREATE TABLE files (incompatible TEXT); CREATE TABLE profile_fixture (value TEXT); INSERT INTO profile_fixture VALUES ('favorites-settings-history');";
            command.ExecuteNonQuery();
        }
        Assert.Throws<SqliteException>(() => useFactory
            ? new SqliteFileIndex(path, new DatabaseConnectionFactory(path)) : new SqliteFileIndex(path));
        using var verify = new SqliteConnection($"Data Source={path};Pooling=False");
        verify.Open(); using var read = verify.CreateCommand();
        read.CommandText = "SELECT value FROM profile_fixture";
        Assert.Equal("favorites-settings-history", read.ExecuteScalar());
    }

    [Fact]
    public void DirectoryGrantRestoresMovedRootAndClosesEveryNativeScope()
    {
        var store = Path.Combine(_root, "bookmarks.json"); var original = Path.Combine(_root, "original");
        var moved = Path.Combine(_root, "moved"); var offline = Path.Combine(_root, "offline");
        File.WriteAllText(store, JsonSerializer.Serialize(new Dictionary<string, string> { [original] = "old", [offline] = "offline" }));
        var native = new FakeBookmarks(); native.Bookmarks["old"] = moved; native.Refreshed = "new";
        using (var access = new DirectoryAccess(store, true, native, []))
        {
            Assert.Equal(moved + "/file.txt", access.ResolvePath(original + "/file.txt"));
            Assert.True(access.CanAccess(moved + "/file.txt"));
            Assert.False(access.CanAccess(moved + "-sibling/file.txt"));
            Assert.Contains(offline, access.UnavailableRoots);
            access.RememberSelection(moved);
            Assert.Single(access.AuthorizedRoots);
        }
        Assert.Equal(native.Opened, native.Closed);
        using (var restarted = new DirectoryAccess(store, true, native, []))
        {
            Assert.Equal(moved + "/file.txt", restarted.ResolvePath(original + "/file.txt"));
            native.Refreshed = null; native.Bookmarks["offline"] = offline;
            restarted.RetryUnavailable();
            Assert.Empty(restarted.UnavailableRoots);
            Assert.True(restarted.CanAccess(offline));
            restarted.Revoke(moved);
            Assert.False(restarted.CanAccess(moved));
            Assert.Equal(original, restarted.ResolvePath(original));
        }
        Assert.Equal(native.Opened, native.Closed);
    }

    [Fact]
    public void MalformedGrantsRemainRecoverableAndFailedSaveClosesNewScope()
    {
        var native = new FakeBookmarks();
        foreach (var malformed in new[] { "invalid json", "null", "[]", "{\"Bookmarks\":null}", "{\"Bookmarks\":{\"/fixture\":null}}" })
        {
            var store = Path.Combine(_root, "bad-bookmarks-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(store, malformed);
            using var access = new DirectoryAccess(store, true, native, []);
            Assert.NotNull(access.RestoreError);
            Assert.Single(Directory.GetFiles(_root, Path.GetFileName(store) + ".invalid-*"));
            access.RememberSelection(_root);
            Assert.True(access.CanAccess(_root));
        }
        var blocker = Path.Combine(_root, "not-a-directory"); File.WriteAllText(blocker, "fixture");
        using var failed = new DirectoryAccess(Path.Combine(_root, "failed.json"), true, native, []);
        File.Delete(Path.Combine(_root, "failed.json"));
        Directory.CreateDirectory(Path.Combine(_root, "failed.json"));
        Assert.ThrowsAny<IOException>(() => failed.RememberSelection(_root));
        Assert.Empty(failed.AuthorizedRoots);
        Assert.Equal(native.Opened, native.Closed);
    }

    [Fact]
    public void DirectoryGrantRejectsSymlinkEscapeAndUnselectedPaths()
    {
        var root = Path.Combine(_root, "selected"); Directory.CreateDirectory(root);
        var outside = Path.Combine(_root, "outside"); Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(root, "escape"), outside);
        var native = new FakeBookmarks();
        using var access = new DirectoryAccess(Path.Combine(_root, "bookmarks.json"), true, native, []);
        Assert.Throws<UnauthorizedAccessException>(() => access.EnsureAccess(root));
        access.RememberSelection(root);
        Assert.True(access.CanAccess(Path.Combine(root, "new-file.txt")));
        Assert.False(access.CanAccess(Path.Combine(root, "escape", "secret.txt")));
    }

    [Fact]
    public void RepeatedAccessChecksResolveOnlyTargetsAndStillRejectRetargetedLinks()
    {
        var selected = Directory.CreateDirectory(Path.Combine(_root, "selected-cache")).FullName;
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside-cache")).FullName;
        var native = new FakeBookmarks();
        using var access = new DirectoryAccess(Path.Combine(_root, "cached.json"), true, native, []);
        access.RememberSelection(selected);
        native.Resolved = 0;
        for (var i = 0; i < 100; i++) Assert.True(access.CanAccess(Path.Combine(selected, $"new-{i}.txt")));
        Assert.Equal(100, native.Resolved);
        var link = Path.Combine(selected, "link");
        Directory.CreateSymbolicLink(link, selected);
        Assert.True(access.CanAccess(Path.Combine(link, "new.txt")));
        Directory.Delete(link);
        Directory.CreateSymbolicLink(link, outside);
        Assert.False(access.CanAccess(Path.Combine(link, "new.txt")));
        access.Revoke(selected);
        Assert.False(access.CanAccess(Path.Combine(selected, "new.txt")));
    }

    [Fact]
    public void HelpersReuseOnlyRequestedGrantsAndCannotReuseRevokedAccess()
    {
        var selected = Directory.CreateDirectory(Path.Combine(_root, "helper-selected")).FullName;
        var unrelated = Directory.CreateDirectory(Path.Combine(_root, "helper-unrelated")).FullName;
        var native = new FakeBookmarks();
        using var access = new DirectoryAccess(Path.Combine(_root, "helpers.json"), true, native, []);
        access.RememberSelection(selected); access.RememberSelection(unrelated);
        for (var i = 0; i < 10; i++)
        {
            var start = new ProcessStartInfo();
            access.ConfigureHelper(start, Path.Combine(selected, $"image-{i}.png"));
            Assert.Equal(new[] { selected }, JsonSerializer.Deserialize<string[]>(start.Environment["MACEXPLORER_FILE_BOOKMARKS"]!));
        }
        Assert.Equal(1, native.ImplicitCreated);
        access.Revoke(selected);
        Assert.Throws<UnauthorizedAccessException>(() => access.ConfigureHelper(new(), Path.Combine(selected, "image.png")));
        access.RememberSelection(selected);
        access.ConfigureHelper(new(), Path.Combine(selected, "image.png"));
        Assert.Equal(2, native.ImplicitCreated);
    }

    [Fact]
    public async Task StoreGitStatusRejectsInvalidPaths()
    {
        if (!DistributionChannel.IsAppStore) return;
        using var git = new GitStatusService();
        Assert.Null(await git.GetRepoStatusAsync("invalid\0path"));
        Assert.Empty(GitStatusService.GetIgnoredPaths("invalid\0path"));
        Assert.Empty(GitStatusService.GetUntrackedPaths("invalid\0path"));
    }

    [Fact]
    public void NativePathResolutionKeepsNewChildrenWithinTheirCanonicalParentAndRejectsDanglingLinkEscape()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var selected = Path.Combine(_root, "native-selected"); Directory.CreateDirectory(selected);
        var outside = Path.Combine(_root, "native-outside"); Directory.CreateDirectory(outside);
        var parent = MacExplorer.Platforms.MacOS.MacSandboxNative.RealPath(selected);
        var child = MacExplorer.Platforms.MacOS.MacSandboxNative.RealPath(Path.Combine(selected, "new-file.txt"));
        Assert.Equal(Path.Combine(parent, "new-file.txt"), child);
        var target = Path.Combine(outside, "not-created");
        Directory.CreateSymbolicLink(Path.Combine(selected, "escape"), target);
        var escaped = MacExplorer.Platforms.MacOS.MacSandboxNative.RealPath(Path.Combine(selected, "escape", "new-file.txt"));
        Assert.Equal(Path.Combine(MacExplorer.Platforms.MacOS.MacSandboxNative.RealPath(outside), "not-created", "new-file.txt"), escaped);
        Assert.False(DirectoryAccess.IsWithin(escaped, parent));
    }

    [Fact]
    public void SftpUpgradeMovesPasswordsIntoCredentialStoreAndKeepsConnections()
    {
        var path = Path.Combine(_root, "remote-servers.json"); var credentials = new MemoryCredentials();
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new RemoteServerInfo { Id = "existing", Host = "example.test", Username = "user", Password = "old-secret" } }));
        using (var service = new RemoteConnectionService(path, credentials))
            Assert.Equal("old-secret", Assert.Single(service.GetSavedServers()).Password);
        Assert.DoesNotContain("old-secret", File.ReadAllText(path)); Assert.DoesNotContain("Password", File.ReadAllText(path));
        Assert.Equal("old-secret", credentials.Read("existing"));
        using var restarted = new RemoteConnectionService(path, credentials);
        Assert.Equal("old-secret", Assert.Single(restarted.GetSavedServers()).Password);
    }

    [Fact]
    public void SftpCredentialFailureKeepsLegacyConfigAndConnectionForRetry()
    {
        var path = Path.Combine(_root, "remote-servers.json");
        var original = JsonSerializer.Serialize(new[] { new RemoteServerInfo { Id = "existing", Password = "old-secret" } });
        File.WriteAllText(path, original);
        using var service = new RemoteConnectionService(path, new MemoryCredentials { Fail = true });
        Assert.Equal(original, File.ReadAllText(path)); Assert.Single(service.GetSavedServers());
    }

    [Fact]
    public void SftpLaterSaveCannotEraseAnotherServersUnmigratedPassword()
    {
        var path = Path.Combine(_root, "remote-servers.json");
        var original = JsonSerializer.Serialize(new[] {
            new RemoteServerInfo { Id = "first", Password = "first-secret" },
            new RemoteServerInfo { Id = "locked", Password = "unmigrated-secret" } });
        File.WriteAllText(path, original);
        var credentials = new MemoryCredentials { FailedAccount = "locked" };
        using var service = new RemoteConnectionService(path, credentials);
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Throws<IOException>(() => service.SaveServer(new RemoteServerInfo { Id = "first", Password = "updated-secret" }));
        Assert.Equal(original, File.ReadAllText(path));
        credentials.FailedAccount = null;
        service.SaveServer(new RemoteServerInfo { Id = "first", Password = "updated-secret" });
        Assert.DoesNotContain("secret", File.ReadAllText(path));
        Assert.Equal("unmigrated-secret", credentials.Read("locked"));
        using var restarted = new RemoteConnectionService(path, credentials);
        Assert.Equal("unmigrated-secret", restarted.GetSavedServers().Single(s => s.Id == "locked").Password);
    }

    [Fact]
    public async Task RevokedGrantStopsExistingIndexWatchAndCanBeReauthorized()
    {
        var selected = Directory.CreateDirectory(Path.Combine(_root, "index-files")).FullName;
        await File.WriteAllTextAsync(Path.Combine(selected, "initial.txt"), "initial", Token);
        using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, new FakeBookmarks(), []);
        access.RememberSelection(selected);
        using var testAccess = DirectoryAccess.UseForTests(access);
        var database = Path.Combine(_root, "index.db");
        var catalog = new SearchCatalog(new DatabaseConnectionFactory(database));
        var changes = new RecordingChanges();
        await using var indexer = new SearchIndexer(catalog, new() { DatabasePath = database }, new TestPinyin(), changes);
        indexer.EnsureRoot(selected);
        await WaitForIndex(indexer, selected);
        Assert.Equal(1, changes.Starts);
        access.Revoke(selected);
        Assert.Equal(1, changes.Disposals);
        Assert.Equal(SearchIndexPhase.Unavailable, indexer.Observe(selected).Status.Phase);
        await File.WriteAllTextAsync(Path.Combine(selected, "after-revoke.txt"), "private", Token);
        changes.Emit(new SearchChange(Path.Combine(selected, "after-revoke.txt"), false, false, 1));
        Assert.False(indexer.Observe(selected).Status.IsBusy);
        Assert.Throws<UnauthorizedAccessException>(() => indexer.EnsureRoot(selected));
        using (var connection = new SqliteConnection($"Data Source={database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM search_entries WHERE name = 'after-revoke.txt'";
            Assert.Equal(0L, command.ExecuteScalar());
        }
        access.RememberSelection(selected);
        await WaitForIndex(indexer, selected);
        Assert.Equal(2, changes.Starts);
        Assert.Single(await catalog.SearchAsync(selected, SearchQuery.Parse("after-revoke"), new(), 10, false, Token));
        Directory.CreateDirectory(Path.Combine(_root, "access.json.tmp"));
        access.RetryUnavailable();
        Assert.NotNull(access.RestoreError); Assert.Empty(access.AuthorizedRoots);
        Assert.Equal(2, changes.Disposals);
        Assert.Equal(SearchIndexPhase.Unavailable, indexer.Observe(selected).Status.Phase);
        Directory.Delete(Path.Combine(_root, "access.json.tmp"));
        access.RetryUnavailable(); await WaitForIndex(indexer, selected);
        Assert.Null(access.RestoreError); Assert.Equal(3, changes.Starts);
        var newlyAuthorized = Path.Combine(_root, "recovered-new-root"); Directory.CreateDirectory(newlyAuthorized);
        await File.WriteAllTextAsync(Path.Combine(newlyAuthorized, "recovered.txt"), "fixture", Token);
        access.RememberSelection(newlyAuthorized);
        await WaitForIndex(indexer, newlyAuthorized);
        Assert.Single(await catalog.SearchAsync(newlyAuthorized, SearchQuery.Parse("recovered"), new(), 10, false, Token));
    }

    private async Task WaitForIndex(SearchIndexer indexer, string root)
    {
        while (true)
        {
            var observed = indexer.Observe(root);
            if (!observed.Status.IsBusy) { Assert.Equal(SearchIndexPhase.Ready, observed.Status.Phase); return; }
            await observed.Changed.WaitAsync(TimeSpan.FromSeconds(10), Token);
        }
    }

    private sealed class RecordingChanges : ISearchChangeSource
    {
        public int Starts, Disposals;
        private Action<SearchChange>? _callback;
        public IDisposable Watch(string root, ulong sinceEventId, Action<SearchChange> onChange, bool fullScanFollows)
        { Starts++; _callback = onChange; return new Registration(this); }
        public void Emit(SearchChange change) => _callback?.Invoke(change);
        private sealed class Registration(RecordingChanges owner) : IDisposable
        { public void Dispose() => owner.Disposals++; }
    }
    private sealed class TestPinyin : IPinyinInitials
    { public string GetInitials(string name) => ""; }

    [Fact]
    public void CopilotConsentExpiresOnEndpointModelOrCredentialChange()
    {
        var settings = new CopilotSettings(new PluginTestEnvironment.MemorySettings());
        Assert.False(settings.HasMetadataConsent("key")); settings.AllowMetadataSharing("key");
        Assert.True(settings.HasMetadataConsent("key")); Assert.False(settings.HasMetadataConsent("other-key"));
        settings.Model = "different"; Assert.False(settings.HasMetadataConsent("key"));
        settings.AllowMetadataSharing("key"); settings.Endpoint = "https://different.test/v1";
        Assert.False(settings.HasMetadataConsent("key"));
    }

    [Fact]
    public async Task CopilotRejectsUnconsentedSendBeforeAnyTransportOrHistoryWrite()
    {
        var settings = new CopilotSettings(new PluginTestEnvironment.MemorySettings()); var keychain = new CopilotKeychain();
        keychain.Save("isolated-test-key"); var store = new CopilotStore(Path.Combine(_root, "copilot.db"));
        using var engine = new CopilotEngine(null!, settings, keychain, store, null!, () => null,
            () => throw new Exception("Transport must not be created"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SendAsync("fixture", cancellationToken: Token));
        Assert.Empty(engine.History);
    }

    [Fact]
    public async Task FixedBuiltInConversionProducesDocxThroughSharedSession()
    {
        var input = Path.Combine(_root, "input.txt"); await File.WriteAllTextAsync(input, "shared conversion", Token);
        var native = new FakeBookmarks(); using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, native, []);
        access.RememberSelection(_root); using var testAccess = DirectoryAccess.UseForTests(access);
        await using var session = new PluginSession(Path.Combine(AppContext.BaseDirectory, "BuiltInConversion"), Path.Combine(_root, "work"), Path.Combine(_root, "log"), () => Task.CompletedTask);
        var request = new PluginInvocation("test", "to-docx", [new PluginFile(input)], session.WorkDirectory);
        var result = await session.CallAsync<PluginResult>("execute", request, TimeSpan.FromSeconds(30), Token);
        Assert.Single(result.Outputs); Assert.True(File.Exists(result.Outputs[0].Path));
        using var zip = System.IO.Compression.ZipFile.OpenRead(result.Outputs[0].Path);
        Assert.Contains(zip.Entries, e => e.FullName == "word/document.xml");
    }

    [Fact]
    public async Task BothChannelsTransferFilesOverIpv4AndIpv6AndKeepLegacyInfo()
    {
        var native = new FakeBookmarks(); using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, native, []);
        access.RememberSelection(_root); using var testAccess = DirectoryAccess.UseForTests(access);
        var settings = new PluginTestEnvironment.MemorySettings();
        var tasks = new BackgroundTaskManager();
        await using var service = new LocalSendService(settings, tasks);
        service.FileAccessOverride = access;
        service.ScanInterfacesOverride = () => [];
        service.DiscoveryAddressesOverride = () => [];
        using (var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
            service.DiscoveryUdpPortOverride = ((System.Net.IPEndPoint)udp.Client.LocalEndPoint!).Port;
        service.ReceiveDirectory = Path.Combine(_root, "received");
        service.ConfirmReceiveAsync = (request, _) => Task.FromResult(new LocalSendReceiveDecision(true, request.DefaultDirectory));
        await service.StartAsync();
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false,
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true });
        foreach (var address in new[] { "127.0.0.1", "[::1]" })
        {
            foreach (var version in new[] { "v1", "v2" })
            {
                using var info = await client.GetAsync($"https://{address}:{service.ListeningPort}/api/localsend/{version}/info", Token);
                Assert.Equal(System.Net.HttpStatusCode.OK, info.StatusCode);
            }
            var source = Path.Combine(_root, address == "127.0.0.1" ? "ipv4.txt" : "ipv6.txt");
            await File.WriteAllTextAsync(source, "network fixture", Token);
            await service.SendAsync(new LocalSendDevice("fixture", service.Fingerprint!, address.Trim('[', ']'), service.ListeningPort, "https"), [source]).WaitAsync(Token);
            Assert.Equal("network fixture", await File.ReadAllTextAsync(Path.Combine(service.ReceiveDirectory, Path.GetFileName(source)), Token));
        }
    }

    [Fact]
    public async Task StoreChannelRejectsExternalExecutionAtServiceBoundary()
    {
        if (!DistributionChannel.IsAppStore) return;
        var manager = new PluginManager(new PluginTestEnvironment.MemorySettings(), Path.Combine(_root, "Plugins"), "missing", "missing", "missing");
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.InstallAsync("missing", Token));
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.StartAsync("external", "command", [], Token));
        Assert.Throws<NotSupportedException>(() => HomeScriptRunner.BuildTerminalScript("missing", new HomeScriptCommand("id", "title", "command", "/bin/zsh", "")));
        await Assert.ThrowsAsync<NotSupportedException>(() => new MacExplorer.Platforms.MacCatalyst.Services.MacFileService().EmptyTrashAsync());
        await manager.DisposeAsync();
    }

    private sealed class MemoryCredentials : ICredentialStore
    {
        public bool Fail; public string? FailedAccount; private readonly Dictionary<string, string> _values = [];
        public string? Read(string account) => _values.GetValueOrDefault(account);
        public void Save(string account, string secret) { if (Fail || FailedAccount == account) throw new IOException("locked"); _values[account] = secret; }
        public void Delete(string account) => _values.Remove(account);
    }
    private sealed class FakeBookmarks : IBookmarkAccess
    {
        public readonly Dictionary<string, string> Bookmarks = []; public string? Refreshed;
        public int Opened, Closed, Resolved, ImplicitCreated;
        public string Create(string path, bool explicitScope) { if (!explicitScope) ImplicitCreated++; Bookmarks[path] = path; return path; }
        public (IntPtr, string?, string?, bool) Open(string bookmark)
        {
            if (!Bookmarks.TryGetValue(bookmark, out var path)) return (IntPtr.Zero, null, null, false);
            if (Refreshed != null) Bookmarks[Refreshed] = path;
            return (new IntPtr(++Opened), path, Refreshed, Refreshed != null);
        }
        public void Close(IntPtr scope) => Closed++;
        public string RealPath(string path)
        {
            Resolved++;
            var suffix = new Stack<string>(); var parent = path;
            while (!Directory.Exists(parent) && !File.Exists(parent)) { suffix.Push(Path.GetFileName(parent)); parent = Path.GetDirectoryName(parent)!; }
            var target = new DirectoryInfo(parent).ResolveLinkTarget(true)?.FullName ?? parent;
            while (suffix.TryPop(out var part)) target = Path.Combine(target, part);
            return target;
        }
    }
    public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
