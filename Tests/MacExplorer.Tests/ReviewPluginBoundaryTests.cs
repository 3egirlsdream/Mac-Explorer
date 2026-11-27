using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Area", "Plugin")]
public sealed class ReviewPluginBoundaryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-boundary-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [Trait("Kind", "Unit")]
    [InlineData("report.")]
    [InlineData(".env")]
    [InlineData("report.tar.gz")]
    public async Task SuggestedOutputNameIsPreservedExactly(string name)
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var source = Path.Combine(_root, "source.txt");
        var output = Path.Combine(work, "result.tmp");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(output, "result");
        var paths = await PluginOutputCommitter.CommitAsync([new(output, name)], [new PluginFile(source)], work, default);
        Assert.Equal(name, Path.GetFileName(Assert.Single(paths)));
        Assert.Equal("result", await File.ReadAllTextAsync(paths[0]));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task InvalidLaterFilenameIsRejectedBeforeAnyOutputIsPublished()
    {
        var work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var source = Path.Combine(_root, "source.txt");
        var output = Path.Combine(work, "result.tmp");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(output, "result");
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync(
            [new(output, "first.txt"), new(output, "bad\0name.txt")], [new PluginFile(source)], work, default));
        Assert.False(File.Exists(Path.Combine(_root, "first.txt")));
        Assert.Empty(Directory.GetFiles(_root, ".MacExplorer-create-*"));
    }

    [Theory]
    [Trait("Kind", "Unit")]
    [InlineData("null-id")]
    [InlineData("null-command")]
    [InlineData("null-command-id")]
    public void MalformedManifestProducesAValidationErrorInsteadOfANullReference(string scenario)
    {
        var json = JsonSerializer.SerializeToNode(new PluginManifest
        {
            Id = "test.boundary", Name = "Test", Version = "1.0.0", ApiVersion = PluginProtocol.ApiVersion,
            Entry = "entry.dll", Commands = [new() { Id = "run", Title = "Run" }]
        }, PluginProtocol.Json)!;
        switch (scenario)
        {
            case "null-id": json["id"] = null; break;
            case "null-command": json["commands"] = new JsonArray((JsonNode?)null); break;
            case "null-command-id": json["commands"]![0]!["id"] = null; break;
        }
        File.WriteAllText(Path.Combine(_root, "plugin.json"), json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => PluginPackage.ReadManifest(_root));
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public void ManifestSizeIsLimitedBeforeJsonMaterialization()
    {
        File.WriteAllBytes(Path.Combine(_root, "plugin.json"), new byte[1024 * 1024 + 1]);
        Assert.Contains("过大", Assert.Throws<InvalidDataException>(() => PluginPackage.ReadManifest(_root)).Message);
    }

    [Fact]
    [Trait("Kind", "Unit")]
    public async Task ExternalDllPackageIsRejectedWithoutTouchingTheBuiltInConversion()
    {
        await using var manager = new PluginManager(Path.Combine(_root, "work"));
        await manager.InitializeAsync();
        var conversion = Assert.Single(manager.Plugins);
        Assert.Equal(PluginManager.BuiltInId, conversion.Manifest.Id);
        Assert.True(conversion.Enabled);

        var package = Path.Combine(_root, "external.mexplug");
        using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            const string entry = "MacExplorer.PluginSdk.dll";
            archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, entry), entry);
            using var manifest = archive.CreateEntry("plugin.json").Open();
            await JsonSerializer.SerializeAsync(manifest, new PluginManifest
            {
                Id = "test.external", Name = "External", Version = "1.0.0",
                ApiVersion = PluginProtocol.ApiVersion, Entry = entry, Commands = [new() { Id = "run", Title = "Run" }]
            }, PluginJsonContext.Default.PluginManifest);
        }

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => manager.InstallAsync(package));
        Assert.Equal(Services.Impl.LocalizationText.Get("plugin.nativeOnly"), error.Message);
        await Assert.ThrowsAsync<NotSupportedException>(() => manager.StartAsync("external", "run", []));
        Assert.Same(conversion, Assert.Single(manager.Plugins));
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }
}
