using System.Text.Json;
using System.Text.Json.Nodes;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ReviewPluginBoundaryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("macexplorer-boundary-").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
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
    [InlineData("null-id")]
    [InlineData("null-command")]
    [InlineData("null-command-id")]
    public void MalformedManifestProducesAValidationErrorInsteadOfANullReference(string scenario)
    {
        File.Copy(typeof(IFileActionPlugin).Assembly.Location, Path.Combine(_root, "entry.dll"));
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
    public void ManifestSizeIsLimitedBeforeJsonMaterialization()
    {
        File.WriteAllBytes(Path.Combine(_root, "plugin.json"), new byte[1024 * 1024 + 1]);
        Assert.Contains("过大", Assert.Throws<InvalidDataException>(() => PluginPackage.ReadManifest(_root)).Message);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("version")]
    [InlineData("api")]
    [InlineData("paid")]
    [InlineData("trial")]
    [InlineData("ui")]
    [InlineData("platform")]
    [InlineData("architecture")]
    public async Task MarketDeclarationMismatchDoesNotReplaceTheInstalledVersion(string field)
    {
        using var environment = new PluginTestEnvironment();
        var package = environment.CreateFixture();
        await environment.Manager.InstallAsync(package);
        var previous = environment.Manager.Plugins.Single(plugin => plugin.Manifest.Id == "test.fixture");
        var expected = field switch
        {
            "id" => previous.Manifest with { Id = "test.other" },
            "version" => previous.Manifest with { Version = "2.0.0" },
            "api" => previous.Manifest with { ApiVersion = 2 },
            "paid" => previous.Manifest with { Paid = true },
            "trial" => previous.Manifest with { TrialDays = 30 },
            "ui" => previous.Manifest with { HasUserInterface = true },
            "platform" => previous.Manifest with { Platform = "windows" },
            _ => previous.Manifest with { Architecture = "x64" }
        };
        var state = environment.Settings.Get("plugins_state_v1");
        await Assert.ThrowsAsync<InvalidDataException>(() => environment.Manager.InstallAsync(package,
            fromMarket: true, expectedManifest: expected));
        Assert.Equal(state, environment.Settings.Get("plugins_state_v1"));
        Assert.Equal(previous.Directory, environment.Manager.Plugins.Single(plugin => plugin.Manifest.Id == "test.fixture").Directory);
        Assert.True(Directory.Exists(previous.Directory));
        Assert.Empty(Directory.GetDirectories(environment.Manager.RootDirectory, ".install-*"));
    }
}
