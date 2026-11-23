using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MacExplorer.Models;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using MacExplorer.ViewModels;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Archive")]
public sealed class ArchiveWorkflowTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("fk-archive-workflow-");
    private readonly ShortcutMemorySettings _settings = new();
    private readonly byte[] _key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    private const string Password = "fixture-pass-报价-2784";

    [Fact]
    public void CompressionUsesTheFirstSourceDirectoryForSelectionsFromDifferentFolders()
    {
        var first = new FileSystemEntry { FullPath = InRoot("photos/summer.jpg"), Name = "summer.jpg" };
        var second = new FileSystemEntry { FullPath = InRoot("documents/notes.txt"), Name = "notes.txt" };
        var vm = new ArchiveViewModel();
        vm.ShowCompressDialog([first, second], null, currentTag: null);
        var options = Assert.IsType<CompressOptions>(vm.PendingCompressOptions);
        Assert.Equal(InRoot("photos"), options.OutputDirectory);
        Assert.Equal(new[] { first.FullPath, second.FullPath }, options.SourcePaths);

        vm.CancelCompressDialog();
        vm.ShowCompressDialog([], second, currentTag: null);
        Assert.Equal(InRoot("documents"), vm.PendingCompressOptions!.OutputDirectory);
    }

    [Fact]
    public async Task CreatedArchiveUsesEncryptedRecordAfterRenameMoveCopyAndRestart()
    {
        _settings.Set(ArchivePreferences.SavePasswords, true);
        var keyDirectory = InRoot("local-keys");
        var database = new DatabaseConnectionFactory(InRoot("passwords.db"));
        ArchivePasswordVault LocalVault() => new(_settings, database, create => ArchivePasswordKeyFile.Read(keyDirectory, create));
        var service = new ArchiveService(null, null, LocalVault());
        var archive = await CreateEncryptedZip(service);
        var destination = Directory.CreateDirectory(InRoot("moved")).FullName;
        var moved = Path.Combine(destination, "rename#copy.zip");
        File.Move(archive, moved);
        File.Copy(moved, InRoot("copy.zip"));

        var reopened = new ArchiveService(null, null, LocalVault());
        foreach (var path in new[] { moved, InRoot("copy.zip") })
        {
            Assert.Equal((path, "报价.txt"), ArchivePathHelper.Parse(ArchivePathHelper.Build(path, "报价.txt")));
            var calls = 0;
            var extracted = await ArchivePasswordAccess.ReadAsync(reopened, path,
                password => reopened.ExtractEntryToTempAsync(path, "报价.txt", password), () =>
                { calls++; return Task.FromResult<string?>(null); });
            try { Assert.Equal("sample archive payload", await File.ReadAllTextAsync(extracted)); }
            finally { Directory.Delete(Path.GetDirectoryName(extracted)!, true); }
            Assert.Equal(0, calls);
        }

        await CreateZip(archive, ("报价.txt", "different contents"));
        Assert.Null(await reopened.ReadSavedPasswordAsync(archive, default));
        _settings.Set(ArchivePreferences.SavePasswords, false);
        Assert.Null(await reopened.ReadSavedPasswordAsync(moved, default));
        _settings.Set(ArchivePreferences.SavePasswords, true);
        Assert.Equal(Password, await reopened.ReadSavedPasswordAsync(moved, default));
        foreach (var path in Directory.GetFiles(_root.FullName, "passwords.db*"))
            Assert.DoesNotContain(Password, Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        Assert.Null(await Vault().ReadAsync(moved, default));
        if (!OperatingSystem.IsWindows())
        {
            var keyPath = Path.Combine(keyDirectory, "key-v1");
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(keyPath));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(keyDirectory));
            File.SetUnixFileMode(keyPath, File.GetUnixFileMode(keyPath) | UnixFileMode.GroupRead);
            Assert.Null(await reopened.ReadSavedPasswordAsync(moved, default));
        }
    }

    [Fact]
    public async Task IncorrectSavedPasswordPromptsOnceThenReplacesRecordAfterValidation()
    {
        var path = await CreateEncryptedZip(new ArchiveService());
        _settings.Set(ArchivePreferences.SavePasswords, true);
        var vault = Vault();
        await vault.SaveAsync(path, vault.Capture(path), "stale-password", default);
        var service = new ArchiveService(null, null, vault);
        Task<string> Open(Func<Task<string?>> prompt) => ArchivePasswordAccess.ReadAsync(service, path,
            password => service.ExtractEntryToTempAsync(path, "报价.txt", password), prompt);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Open(() => Task.FromResult<string?>(null)));
        Assert.Equal("stale-password", await service.ReadSavedPasswordAsync(path, default));
        var prompts = 0;
        var output = await Open(() => { prompts++; return Task.FromResult<string?>(Password); });
        Directory.Delete(Path.GetDirectoryName(output)!, true);
        Assert.Equal(1, prompts);
        Assert.Equal(Password, await service.ReadSavedPasswordAsync(path, default));
    }

    [Fact]
    public async Task MissingVolumeIsReportedAndDoesNotReuseTheCompleteSetFingerprint()
    {
        var original = ArchiveFormatTests.Fixture("Rar.multi.solid.part01.rar");
        var complete = Directory.CreateDirectory(InRoot("complete")).FullName;
        var incomplete = Directory.CreateDirectory(InRoot("incomplete")).FullName;
        foreach (var part in Directory.GetFiles(Path.GetDirectoryName(original)!, "Rar.multi.solid.part*.rar"))
            File.Copy(part, Path.Combine(complete, Path.GetFileName(part)));
        var firstPart = Path.GetFileName(original);
        var incompletePath = Path.Combine(incomplete, firstPart);
        File.Copy(original, incompletePath);
        _settings.Set(ArchivePreferences.SavePasswords, true);
        var vault = Vault();
        var completePath = Path.Combine(complete, firstPart);
        await vault.SaveAsync(completePath, vault.Capture(completePath), Password, default);
        Assert.Equal(Password, await vault.ReadAsync(completePath, default));
        Assert.Null(await vault.ReadAsync(incompletePath, default));
        var service = new ArchiveService(null, null, vault);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => service.GetArchiveContentsAsync(incompletePath));
        Assert.Equal(LocalizationText.Get("archive.missing_volumes"), error.Message);
    }

    [Fact]
    public async Task BrowsingAloneWrongPasswordAndCorruptAesTagDoNotSave()
    {
        var path = await CreateEncryptedZip(new ArchiveService());
        _settings.Set(ArchivePreferences.SavePasswords, true);
        var service = new ArchiveService(null, null, Vault());
        await service.GetArchiveContentsAsync(path, password: Password);
        Assert.Null(await service.ReadSavedPasswordAsync(path, default));
        await Assert.ThrowsAsync<SharpCompress.Common.CryptographicException>(() =>
            service.VerifyBrowsingPasswordAsync(path, "incorrect", default));
        Assert.Null(await service.ReadSavedPasswordAsync(path, default));

        var contents = await File.ReadAllBytesAsync(path);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(contents.AsSpan(26));
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(contents.AsSpan(28));
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(new MemoryStream(contents));
        var compressedSize = checked((int)zip[0].CompressedSize);
        contents[30 + nameLength + extraLength + compressedSize - 1] ^= 0x80;
        var tampered = InRoot("tampered.zip");
        await File.WriteAllBytesAsync(tampered, contents);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.VerifyBrowsingPasswordAsync(tampered, Password, default));
        Assert.Null(await service.ReadSavedPasswordAsync(tampered, default));

        await ArchivePasswordAccess.ReadAsync(service, path,
            password => service.GetArchiveContentsAsync(path, password: password),
            () => Task.FromResult<string?>(Password), browse: true, checkEncryption: true);
        Assert.Equal(Password, await service.ReadSavedPasswordAsync(path, default));
    }

    [Fact]
    public async Task KeepingBothUsesSameNamedDirectoryWithoutTouchingExistingFiles()
    {
        var path = InRoot("交付资料.zip");
        await CreateZip(path, ("报价/报价单.txt", "archive"), ("报价/其他文件.txt", "second"));
        var conflicts = new ConflictResolver();
        var service = new ArchiveService(null, conflicts, null);
        var parent = Directory.CreateDirectory(InRoot("交付资料/报价")).FullName;
        File.WriteAllText(Path.Combine(parent, "客户备注.txt"), "note");
        await service.ExtractAsync(path, new ArchiveExtractionOptions(_root.FullName, ArchiveExtractionMode.NamedFolder, ["报价/报价单.txt"]));
        Assert.False(File.Exists(Path.Combine(parent, "其他文件.txt")));
        File.WriteAllText(Path.Combine(parent, "报价单.txt"), "existing");
        await service.ExtractAsync(path, new ArchiveExtractionOptions(_root.FullName, ArchiveExtractionMode.NamedFolder));
        Assert.Single(conflicts.Seen);
        Assert.Equal("existing", File.ReadAllText(Path.Combine(parent, "报价单.txt")));
        Assert.Equal("archive", File.ReadAllText(Path.Combine(parent, "报价单 (1).txt")));
        Assert.Equal("second", File.ReadAllText(Path.Combine(parent, "其他文件.txt")));
        Assert.Equal("note", File.ReadAllText(Path.Combine(parent, "客户备注.txt")));
    }

    [Fact]
    public async Task AutomaticThumbnailSourceIsRestrictedToSmallPlainZipEntries()
    {
        var path = InRoot("image.zip");
        await CreateZip(path, ("small.png", "sample"));
        var service = new ArchiveService();
        var entry = Assert.Single(await service.GetArchiveContentsAsync(path));
        Assert.NotNull(entry.ArchiveThumbnailVersion);
        var source = Assert.IsType<string>(await service.ExtractThumbnailSourceAsync(path, "small.png", default));
        try { Assert.Equal("sample", File.ReadAllText(source)); }
        finally { Directory.Delete(Path.GetDirectoryName(source)!, true); }
        var encrypted = await CreateEncryptedZip(service);
        Assert.Null(Assert.Single(await service.GetArchiveContentsAsync(encrypted)).ArchiveThumbnailVersion);
        Assert.Null(await service.ExtractThumbnailSourceAsync(encrypted, "报价.txt", default));
        Assert.Null(await service.ExtractThumbnailSourceAsync(ArchiveFormatTests.Fixture("sample.tar.gz"), "docs/中文说明.txt", default));
    }

    [Fact]
    public async Task ArchiveImageUsesRegularThumbnailPipelineAndInvalidatesWhenArchiveChanges()
    {
        var path = InRoot("photos.zip");
        var service = new ArchiveService();
        var thumbnails = new MacThumbnailService(InRoot("thumbnail-cache"), 4 * 1024 * 1024, .8);
        WritePhoto(path, SKColors.CornflowerBlue);
        var previous = Assert.Single(await service.GetArchiveContentsAsync(path));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var thumbnail = await thumbnails.GetArchiveThumbnailAsync(service, previous, 64, timeout.Token);
        Assert.NotNull(thumbnail);
        using var image = SKBitmap.Decode(thumbnail.Bytes);
        Assert.InRange(image.Width, 1, 64);

        var replacement = InRoot("replacement.zip");
        WritePhoto(replacement, SKColors.Green);
        File.Move(replacement, path, overwrite: true);
        var current = Assert.Single(await service.GetArchiveContentsAsync(path));
        Assert.NotEqual(previous.ArchiveThumbnailVersion, current.ArchiveThumbnailVersion);
        Assert.Null(await thumbnails.GetArchiveThumbnailAsync(service, previous, 64, timeout.Token));
        var currentThumbnail = await thumbnails.GetArchiveThumbnailAsync(service, current, 64, timeout.Token);
        Assert.NotNull(currentThumbnail);
        Assert.NotEqual(thumbnail.CachePath, currentThumbnail.CachePath);

        static void WritePhoto(string path, SKColor color)
        {
            using var photo = new SKBitmap(80, 64);
            photo.Erase(color);
            using var png = photo.Encode(SKEncodedImageFormat.Png, 90);
            using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            using var entry = zip.CreateEntry("photo.png").Open();
            entry.Write(png.AsSpan());
        }
    }

    private ArchivePasswordVault Vault() => new(_settings, new DatabaseConnectionFactory(InRoot("passwords.db")), _ => _key.ToArray());
    private string InRoot(string name) => Path.Combine(_root.FullName, name);

    private async Task<string> CreateEncryptedZip(ArchiveService service)
    {
        var file = InRoot("报价.txt");
        await File.WriteAllTextAsync(file, "sample archive payload");
        return await service.CompressAsync(new CompressOptions
        {
            ArchiveName = "交付", OutputDirectory = _root.FullName, SourcePaths = [file], Password = Password
        });
    }

    private static async Task CreateZip(string path, params (string Name, string Data)[] entries)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries)
        {
            await using var content = archive.CreateEntry(entry.Name).Open();
            await content.WriteAsync(Encoding.UTF8.GetBytes(entry.Data));
        }
    }

    private sealed class ConflictResolver : IFileConflictResolver
    {
        public List<FileConflict> Seen { get; } = [];
        public Task<FileConflictDecision> ResolveAsync(FileConflict conflict, CancellationToken cancellationToken)
        { Seen.Add(conflict); return Task.FromResult(new FileConflictDecision(FileConflictAction.KeepBoth)); }
    }

    public void Dispose() => _root.Delete(true);
}
