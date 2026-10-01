using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MacExplorer.Copilot;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class PrivacySecurityFollowupTests : IDisposable
{
    private readonly string _root = Path.Combine(RuntimePaths.TestRoot ?? Path.GetTempPath(), "security-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Token => TestContext.Current.CancellationToken;
    public PrivacySecurityFollowupTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task CopilotRevocationBlocksAnExistingTransportAndRequiresFreshConsent()
    {
        var settings = new CopilotSettings(new PluginTestEnvironment.MemorySettings());
        if (RuntimePaths.TestRoot == null) Assert.Skip("Run through Tools/Testing/run-isolated.sh to keep Keychain isolated.");
        var keychain = new CopilotKeychain(); keychain.Save("security-followup-fixture-key"); var key = keychain.Read()!;
        settings.AllowMetadataSharing(key);
        var transport = new RecordingTransport();
        using var client = new HttpClient(new PrivacyConsentHandler(transport, settings, keychain, settings.Endpoint, settings.Model, key));
        using (await client.GetAsync("https://fixture.test/v1", Token)) { }
        Assert.Equal(1, transport.Sends);
        settings.RevokeMetadataSharing();
        Assert.False(settings.HasMetadataConsent(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("https://fixture.test/v1", Token));
        Assert.Equal(1, transport.Sends);
        settings.AllowMetadataSharing(key);
        using (await client.GetAsync("https://fixture.test/v1", Token)) { }
        Assert.Equal(2, transport.Sends);
    }

    [Fact]
    public void FailedPrivacyPersistenceReportsFailureBlocksCurrentSharingAndCanRetry()
    {
        var factory = new DatabaseConnectionFactory(Path.Combine(_root, "privacy-settings.db"));
        using var database = factory.GetConnection();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT)";
        command.ExecuteNonQuery();
        using var settings = new SettingsService(factory);
        var copilot = new CopilotSettings(settings);
        copilot.AllowMetadataSharing("fixture-key");
        var leasePath = Path.Combine(_root, "privacy-lease");
        var photo = new PhotoLocationConsent(settings, leasePath);
        photo.SetAllowed(true);
        command.CommandText = """
            CREATE TRIGGER fail_privacy BEFORE INSERT ON app_settings
            WHEN NEW.key IN ('copilot.metadata-consent', 'privacy.photo-location-network')
            BEGIN SELECT RAISE(FAIL, 'fixture persistence blocked'); END
            """;
        command.ExecuteNonQuery();
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => copilot.RevokeMetadataSharing());
        Assert.False(copilot.HasMetadataConsent("fixture-key"));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => photo.SetAllowed(false));
        Assert.False(photo.Enabled);
        Assert.Equal("", File.ReadAllText(leasePath));
        command.CommandText = "DROP TRIGGER fail_privacy";
        command.ExecuteNonQuery();
        copilot.RevokeMetadataSharing();
        photo.SetAllowed(false);
        using var reloaded = new SettingsService(factory);
        Assert.False(new CopilotSettings(reloaded).HasMetadataConsent("fixture-key"));
        Assert.False(new PhotoLocationConsent(reloaded, leasePath).Enabled);
    }

    [Fact]
    public void PhotoConsentDefaultsOffRevokesExistingLeaseAndDoesNotReviveOldHelpers()
    {
        var settings = new PluginTestEnvironment.MemorySettings();
        var path = Path.Combine(_root, "consent");
        var consent = new PhotoLocationConsent(settings, path);
        var defaults = new ProcessStartInfo(); consent.ConfigureHelper(defaults);
        Assert.Empty(defaults.ArgumentList); Assert.Equal("", File.ReadAllText(path));
        consent.SetAllowed(true);
        var first = new ProcessStartInfo(); consent.ConfigureHelper(first);
        var oldLease = first.ArgumentList[2];
        Assert.Equal(oldLease, File.ReadAllText(path));
        consent.SetAllowed(false);
        Assert.Equal("", File.ReadAllText(path));
        Assert.False(new PhotoLocationConsent(settings, path).Enabled);
        consent.SetAllowed(true);
        Assert.NotEqual(oldLease, File.ReadAllText(path));
        var restarted = new PhotoLocationConsent(settings, path);
        Assert.True(restarted.Enabled); Assert.NotEqual(oldLease, File.ReadAllText(path));
        restarted.SetAllowed(false);
        // Even if a settings write failed, the durable empty lease keeps a restart offline.
        settings.Set(PhotoLocationConsent.SettingKey, "true");
        Assert.False(new PhotoLocationConsent(settings, path).Enabled);
    }

    [Fact]
    public async Task SwiftNetworkEntryChecksLiveLeaseBeforeCallingGeocoder()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(PluginTestEnvironment.Repository, "Platforms/MacOS/ImageAnalysisHelper.swift"), Token);
        var start = source.IndexOf("func locationNetworkAllowed()", StringComparison.Ordinal);
        var end = source.IndexOf("enum PdfExtractionError", start, StringComparison.Ordinal);
        var harness = """
            import Foundation
            struct CLLocation { init(latitude: Double, longitude: Double) {} }
            struct Place { var country: String?; var administrativeArea: String?; var locality: String?; var subLocality: String? }
            class CLGeocoder {
                static var calls = 0
                func reverseGeocodeLocation(_ location: CLLocation, completionHandler: ([Place]?, Error?) -> Void) {
                    CLGeocoder.calls += 1; completionHandler(nil, nil)
                }
            }
            """ + "\n" + source[start..end] + "\n_ = reverseGeocode(latitude: 30, longitude: 120)\nprint(CLGeocoder.calls)\n";
        var swift = Path.Combine(_root, "main.swift"); var executable = Path.Combine(_root, "geocoder-spy");
        await File.WriteAllTextAsync(swift, harness, Token);
        Assert.Equal(0, (await Run("/usr/bin/xcrun", ["swiftc", swift, "-o", executable])).ExitCode);
        Assert.Equal("0", (await Run(executable, ["fixture"])).Output.Trim());
        var path = Path.Combine(_root, "consent"); await File.WriteAllTextAsync(path, "lease-a", Token);
        Assert.Equal("0", (await Run(executable, ["fixture", "--location-consent", path, "wrong"])).Output.Trim());
        Assert.Equal("1", (await Run(executable, ["fixture", "--location-consent", path, "lease-a"])).Output.Trim());
        await File.WriteAllTextAsync(path, "", Token);
        Assert.Equal("0", (await Run(executable, ["fixture", "--location-consent", path, "lease-a"])).Output.Trim());
        Assert.Equal("0", (await Run(executable, ["fixture", "--location-consent", path + "-missing", "lease-a"])).Output.Trim());
    }

    [Fact]
    public async Task NativeHelperRetainsLocalGpsCameraAndOcrWithoutNetworkPermission()
    {
        using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root]);
        using var scope = DirectoryAccess.UseForTests(access);
        var photo = Path.Combine(_root, "photo.jpg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData/gps-ocr-fixture.jpg"), photo);
        var result = await new MacImageAnalysisService().AnalyzeImageAsync(photo, Token);
        Assert.NotNull(result.Location);
        Assert.Equal(30.25, result.Location.Latitude); Assert.Equal(120.5, result.Location.Longitude);
        Assert.Equal("30.2500, 120.5000", result.Location.PlaceName);
        Assert.Equal("Fixture QA Camera", result.CameraInfo);
        Assert.Contains(result.RecognizedTexts, text => text.Text.Contains("PRIVACY TEST 2026"));
    }

    [Fact]
    public async Task Aes256ZipRoundTripsChineseNamesAndDuplicateSourcesThroughSharpCompress()
    {
        using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root]);
        using var scope = DirectoryAccess.UseForTests(access);
        var first = Directory.CreateDirectory(Path.Combine(_root, "甲")).FullName;
        var second = Directory.CreateDirectory(Path.Combine(_root, "乙")).FullName;
        File.WriteAllText(Path.Combine(first, "照片.txt"), "first 中文");
        File.WriteAllText(Path.Combine(second, "照片.txt"), "second 中文");
        var values = new List<double>(); var service = new ArchiveService();
        var path = await service.CompressAsync(new CompressOptions { ArchiveName = "密码包", OutputDirectory = _root,
            Password = "fixture-password", SourcePaths = [Path.Combine(first, "照片.txt"), Path.Combine(second, "照片.txt")] },
            new InlineProgress(p => values.Add(p.Percentage)), Token);
        Assert.True(service.IsEncrypted(path));
        // Independent reader checks encryption strength, UTF-8 entry names and payloads.
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(path) { Password = "fixture-password" })
        {
            Assert.Equal(2, zip.Count);
            for (var i = 0; i < zip.Count; i++) Assert.Equal(256, zip[i].AESKeySize);
        }
        var destination = Directory.CreateDirectory(Path.Combine(_root, "extracted")).FullName;
        await service.ExtractAsync(path, destination, ct: Token, password: "fixture-password");
        Assert.Equal("first 中文", File.ReadAllText(Path.Combine(destination, "照片.txt")));
        Assert.Equal("second 中文", File.ReadAllText(Path.Combine(destination, "照片 2.txt")));
        Assert.Equal(100, values[^1]); Assert.True(values.SequenceEqual(values.Order()));
        Assert.Empty(Directory.GetFiles(_root, "*.fkfinder-tmp"));
        var wrong = Directory.CreateDirectory(Path.Combine(_root, "wrong-password")).FullName;
        await Assert.ThrowsAnyAsync<Exception>(() => service.ExtractAsync(path, wrong, ct: Token, password: "wrong"));
    }

    [Fact]
    public async Task LegacyDotNetZipAes256PackageStillExtracts()
    {
        using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root]);
        using var scope = DirectoryAccess.UseForTests(access);
        var path = Path.Combine(_root, "legacy.zip");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData/legacy-dotnetzip-aes256.zip"), path);
        var destination = Directory.CreateDirectory(Path.Combine(_root, "legacy-extracted")).FullName;
        await new ArchiveService().ExtractAsync(path, destination, ct: Token, password: "fixture-password");
        Assert.Equal("legacy AES256 中文 fixture", File.ReadAllText(Path.Combine(destination, "中文目录/旧照片.txt")));
    }

    [Fact]
    public async Task CancellationDuringEncryptedFileWriteRemovesPartialArchive()
    {
        using var access = new DirectoryAccess(Path.Combine(_root, "access.json"), true, internalRoots: [_root]);
        using var scope = DirectoryAccess.UseForTests(access);
        var source = Path.Combine(_root, "large.bin");
        await File.WriteAllBytesAsync(source, new byte[1024 * 1024 * 4], Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ArchiveService().CompressAsync(new CompressOptions
        { ArchiveName = "cancel", OutputDirectory = _root, Password = "secret", SourcePaths = [source] },
            new InlineProgress(_ => cancellation.Cancel()), cancellation.Token));
        Assert.False(File.Exists(Path.Combine(_root, "cancel.zip")));
        Assert.Empty(Directory.GetFiles(_root, "*.fkfinder-tmp")); Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task HostTrustRequiresConsentPersistsAndRejectsChangedKeyWithoutOverwriting()
    {
        var path = Path.Combine(_root, "known.json"); var store = new SftpHostKeyStore(path);
        await Assert.ThrowsAsync<SftpHostKeyException>(() => store.VerifyAsync("EXAMPLE.test", 22, "ssh-ed25519", [1, 2, 3], null, Token));
        Assert.False(File.Exists(path));
        await Assert.ThrowsAsync<SftpHostKeyException>(() => store.VerifyAsync("example.test", 22, "ssh-ed25519", [1, 2, 3], (_, _) => Task.FromResult(false), Token));
        Assert.False(File.Exists(path));
        SftpHostKey? shown = null;
        await store.VerifyAsync("EXAMPLE.test.", 22, "ssh-ed25519", [1, 2, 3], (key, _) => { shown = key; return Task.FromResult(true); }, Token);
        Assert.Equal("SHA256:A5BYxvLAy0ksUzsKTRTvd8wPeKvMztUofYShogEc+4E", shown!.Fingerprint);
        var original = File.ReadAllText(path);
        await new SftpHostKeyStore(path).VerifyAsync("example.test", 22, "ssh-ed25519", [1, 2, 3], (_, _) => throw new Exception("Must not prompt for a known key"), Token);
        var rsa = new SftpHostKeyStore(Path.Combine(_root, "rsa.json"));
        await rsa.VerifyAsync("rsa.test", 22, "rsa-sha2-512", [7, 8, 9], (_, _) => Task.FromResult(true), Token);
        await rsa.VerifyAsync("rsa.test", 22, "rsa-sha2-256", [7, 8, 9], (_, _) => throw new Exception("Same public key must remain trusted"), Token);
        await Assert.ThrowsAsync<SftpHostKeyException>(() => store.VerifyAsync("example.test", 22, "ssh-ed25519", [4, 5, 6], (_, _) => Task.FromResult(true), Token));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Null(store.Get("other.test", 22)); Assert.Null(store.Get("example.test", 2222));
        await store.VerifyAsync("other.test", 22, "ssh-ed25519", [9], (_, _) => Task.FromResult(true), Token);
        store.Forget("example.test", 22); Assert.NotNull(store.Get("other.test", 22)); Assert.Null(store.Get("example.test", 22));
    }

    [Fact]
    public async Task CancelledConfirmationAndFailedTrustPersistenceCannotSaveTrust()
    {
        var path = Path.Combine(_root, "known.json"); var store = new SftpHostKeyStore(path);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.VerifyAsync("fixture.test", 22, "ssh-ed25519", [1],
            (_, _) => { cancellation.Cancel(); return Task.FromResult(true); }, cancellation.Token));
        Assert.False(File.Exists(path));
        Directory.CreateDirectory(path + ".tmp");
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => store.VerifyAsync("fixture.test", 22, "ssh-ed25519", [1], (_, _) => Task.FromResult(true), Token));
        Assert.False(File.Exists(path)); Assert.Null(store.Get("fixture.test", 22));
        Directory.Delete(path + ".tmp");
        await store.VerifyAsync("fixture.test", 22, "ssh-ed25519", [1], (_, _) => Task.FromResult(true), Token);
        Assert.NotNull(store.Get("fixture.test", 22));
    }

    [Fact]
    public void SaveAndDeleteFailuresKeepPreviousConfigCredentialAndOtherServers()
    {
        var path = Path.Combine(_root, "servers.json"); var credentials = new MemoryCredentials();
        using var service = new RemoteConnectionService(path, credentials);
        service.SaveServer(new RemoteServerInfo { Id = "first", Host = "first.test", Password = "original" });
        service.SaveServer(new RemoteServerInfo { Id = "second", Host = "second.test", Password = "other" });
        var original = File.ReadAllText(path); Directory.CreateDirectory(path + ".tmp");
        Assert.ThrowsAny<UnauthorizedAccessException>(() => service.SaveServer(new RemoteServerInfo { Id = "first", Host = "edited.test", Password = "changed" }));
        Assert.Equal(original, File.ReadAllText(path)); Assert.Equal("original", credentials.Read("first"));
        Assert.Equal("first.test", service.GetSavedServers().Single(s => s.Id == "first").Host);
        Assert.ThrowsAny<UnauthorizedAccessException>(() => service.RemoveServer("first"));
        Assert.Equal(original, File.ReadAllText(path)); Assert.Equal("original", credentials.Read("first")); Assert.Equal(2, service.GetSavedServers().Count);
        Directory.Delete(path + ".tmp"); credentials.FailDelete = true;
        Assert.Throws<UnauthorizedAccessException>(() => service.RemoveServer("first"));
        Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(2, service.GetSavedServers().Count);
        credentials.FailDelete = false; service.RemoveServer("first");
        Assert.Equal("second", Assert.Single(service.GetSavedServers()).Id); Assert.Equal("other", credentials.Read("second"));
    }

    [Fact]
    public void FailedLegacyMigrationShowsRetryStateAndRetainsAllSecrets()
    {
        var path = Path.Combine(_root, "servers.json");
        var original = JsonSerializer.Serialize(new[] { new RemoteServerInfo { Id = "a", Password = "a-secret" }, new RemoteServerInfo { Id = "b", Password = "b-secret" } });
        File.WriteAllText(path, original); var credentials = new MemoryCredentials { FailSave = true };
        using var service = new RemoteConnectionService(path, credentials);
        Assert.NotNull(service.CredentialLoadError); Assert.Equal(original, File.ReadAllText(path)); Assert.Equal(2, service.GetSavedServers().Count);
        credentials.FailSave = false; service.RetryCredentialMigration();
        Assert.Null(service.CredentialLoadError); Assert.DoesNotContain("secret", File.ReadAllText(path));
        Assert.Equal("a-secret", credentials.Read("a")); Assert.Equal("b-secret", credentials.Read("b"));
    }

    [Fact]
    public void UnreadableConfigOrHostTrustCannotBeSilentlyOverwritten()
    {
        var path = Path.Combine(_root, "servers.json"); File.WriteAllText(path, "invalid fixture");
        using var service = new RemoteConnectionService(path, new MemoryCredentials());
        Assert.NotNull(service.CredentialLoadError);
        Assert.Throws<InvalidOperationException>(() => service.SaveServer(new RemoteServerInfo { Password = "new" }));
        Assert.Throws<InvalidOperationException>(() => service.RemoveServer("fixture"));
        Assert.Equal("invalid fixture", File.ReadAllText(path));
        var trust = Path.Combine(_root, "known.json"); File.WriteAllText(trust, "invalid fixture");
        Assert.Throws<SftpHostKeyException>(() => new SftpHostKeyStore(trust).Get("fixture.test", 22));
        Assert.Equal("invalid fixture", File.ReadAllText(trust));
        File.WriteAllText(path, "[]"); service.RetryCredentialMigration();
        service.SaveServer(new RemoteServerInfo { Id = "new", Password = "fixture-secret" });
        Assert.Equal("new", Assert.Single(service.GetSavedServers()).Id);
    }

    private async Task<(int ExitCode, string Output)> Run(string command, string[] args)
    {
        var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Token); var errors = process.StandardError.ReadToEndAsync(Token);
        try { await process.WaitForExitAsync(Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        return (process.ExitCode, await output + await errors);
    }
    private sealed class RecordingTransport : HttpMessageHandler
    {
        public int Sends;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Sends++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
    private sealed class InlineProgress(Action<ArchiveProgress> callback) : IProgress<ArchiveProgress>
    { public void Report(ArchiveProgress value) => callback(value); }
    internal sealed class MemoryCredentials : ICredentialStore
    {
        private readonly Dictionary<string, string> _values = [];
        public bool FailSave, FailDelete;
        public string? Read(string account) => _values.GetValueOrDefault(account);
        public void Save(string account, string secret) { if (FailSave) throw new IOException("Keychain locked fixture"); _values[account] = secret; }
        public void Delete(string account) { if (FailDelete) throw new UnauthorizedAccessException("Keychain denied fixture"); _values.Remove(account); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
