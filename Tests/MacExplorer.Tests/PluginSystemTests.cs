using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using MacExplorer.PluginSdk;
using MacExplorer.Services.Plugins;
using SkiaSharp;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace MacExplorer.Tests;

public sealed class PluginSystemTests
{
    [Theory]
    [InlineData("txt", "to-docx")]
    [InlineData("md", "to-docx")]
    [InlineData("md", "to-pdf")]
    [InlineData("docx", "to-pdf")]
    [InlineData("svg", "to-png")]
    [InlineData("svg", "to-jpg")]
    [InlineData("ico", "to-png")]
    [InlineData("icns", "to-png")]
    public async Task BundledConversionRunsInAnotherProcessAndPreservesInput(string extension, string command)
    {
        using var env = new PluginTestEnvironment();
        var source = CreateSource(env, extension);
        var original = File.ReadAllBytes(source);
        var plugin = Assert.Single(env.Manager.Plugins);
        Assert.True(plugin.Enabled, plugin.LastError);
        Assert.Equal("文件转换", plugin.Manifest.Name);
        var files = new[] { new PluginFile(source) };
        await using (var session = await env.Manager.StartAsync(plugin.Manifest.Id, command, files))
        {
            Assert.NotEqual(Environment.ProcessId, session.ProcessId);
            var invocation = new PluginInvocation("conversion-test", command, files, session.WorkDirectory);
            var preparation = await session.CallAsync<PluginPreparation>("prepare", invocation, TimeSpan.FromSeconds(30), default);
            if (preparation.Configuration is { } configuration)
                invocation = invocation with { Parameters = new()
                {
                    ["width"] = JsonSerializer.SerializeToElement(configuration.Width),
                    ["height"] = JsonSerializer.SerializeToElement(configuration.Height)
                } };
            var result = await session.CallAsync<PluginResult>("execute", invocation, TimeSpan.FromSeconds(40), default);
            Assert.Single(result.Outputs);
            Assert.False(File.Exists(Path.ChangeExtension(source, command.Replace("to-", "."))));
            var committed = await PluginOutputCommitter.CommitAsync(result.Outputs, source, session.WorkDirectory, default);
            var output = Assert.Single(committed);
            Assert.True(new FileInfo(output).Length > 0);
            if (command == "to-docx")
            {
                using var document = WordprocessingDocument.Open(output, false);
                Assert.Contains("PLUGIN_CONTENT", document.MainDocumentPart!.Document.InnerText);
            }
            else if (command == "to-pdf") Assert.StartsWith("%PDF-", Encoding.ASCII.GetString(File.ReadAllBytes(output).Take(5).ToArray()));
            else { using var image = SKBitmap.Decode(output); Assert.NotNull(image); Assert.True(image.Width > 0); }
        }
        Assert.Equal(original, File.ReadAllBytes(source));
        Assert.False(Assert.Single(env.Manager.Plugins).Running);
        Assert.Empty(Directory.GetDirectories(Path.Combine(env.Manager.RootDirectory, ".work")));
    }

    [Fact]
    public async Task DisableUninstallAndRestorePersistAcrossStartup()
    {
        using var env = new PluginTestEnvironment();
        await env.Manager.SetEnabledAsync(PluginManager.BuiltInId, false);
        await using (var restarted = env.NewManager())
        {
            await restarted.InitializeAsync();
            Assert.False(Assert.Single(restarted.Plugins).Enabled);
            await restarted.UninstallAsync(PluginManager.BuiltInId);
        }
        await using (var restarted = env.NewManager())
        {
            await restarted.InitializeAsync();
            Assert.True(Assert.Single(restarted.Plugins).Removed);
            await restarted.RestoreBuiltInAsync();
            Assert.True(Assert.Single(restarted.Plugins).Enabled);
            Assert.False(Assert.Single(restarted.Plugins).Removed);
        }
    }

    [Fact]
    public async Task UpdatePinsRunningVersionAndUninstallWaitsForSession()
    {
        using var env = new PluginTestEnvironment();
        await env.Manager.InstallAsync(env.CreateFixture());
        var source = env.Write("source.txt", "test");
        var previous = env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Directory;
        var session = await env.Manager.StartAsync("test.fixture", "run", [new(source)]);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => env.Manager.StartAsync("test.fixture", "run", [new(source)]));
            await env.Manager.InstallAsync(env.CreateFixture("2.0.0"));
            var current = env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture");
            Assert.Equal("2.0.0", current.Manifest.Version);
            Assert.True(Directory.Exists(previous));
            Assert.NotEqual(previous, current.Directory);
            await Assert.ThrowsAsync<InvalidDataException>(() => env.Manager.InstallAsync(env.CreateFixture("3.0.0", 99)));
            Assert.Equal("2.0.0", env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Manifest.Version);
            await env.Manager.UninstallAsync("test.fixture");
            Assert.True(Directory.Exists(previous));
            Assert.True(env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Running);
            await Assert.ThrowsAsync<InvalidOperationException>(() => env.Manager.StartAsync("test.fixture", "run", [new(source)]));
            var result = await session.CallAsync<PluginResult>("execute", new PluginInvocation("old-version", "run", [new(source)], session.WorkDirectory), TimeSpan.FromSeconds(10), default);
            Assert.Single(result.Outputs);
        }
        finally { await session.DisposeAsync(); }
        Assert.False(Directory.Exists(previous));
        Assert.False(env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Running);
    }

    [Theory]
    [InlineData("../escape.txt", false)]
    [InlineData("/absolute.txt", false)]
    [InlineData("link", true)]
    public async Task RejectsArchiveTraversalAndSymlinks(string name, bool symlink)
    {
        using var env = new PluginTestEnvironment();
        var existing = Assert.Single(env.Manager.Plugins).Directory;
        var package = Path.Combine(env.Root, "bad.mexplug");
        using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry(name);
            if (symlink) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
            using var writer = new StreamWriter(entry.Open()); writer.Write("outside");
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => env.Manager.InstallAsync(package));
        Assert.Equal(existing, Assert.Single(env.Manager.Plugins).Directory);
        Assert.False(File.Exists(Path.Combine(env.Root, "escape.txt")));
        Assert.Empty(Directory.GetDirectories(env.Manager.RootDirectory, ".install-*"));
    }

    [Theory]
    [InlineData("crash")]
    [InlineData("invalid-wire")]
    [InlineData("crash-helper")]
    public async Task WorkerFailuresReleaseSession(string command)
    {
        using var env = new PluginTestEnvironment();
        await env.Manager.InstallAsync(env.CreateFixture());
        var source = env.Write("source.txt", "test");
        var session = await env.Manager.StartAsync("test.fixture", command, [new(source)]);
        var pid = session.ProcessId;
        var work = session.WorkDirectory;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => session.CallAsync<PluginResult>("execute",
                new PluginInvocation("failure", command, [new(source)], work), TimeSpan.FromSeconds(5), default));
        }
        finally { await session.DisposeAsync(); }
        Assert.False(IsRunning(pid));
        Assert.False(Directory.Exists(work));
        Assert.False(env.Manager.Plugins.Single(p => p.Manifest.Id == "test.fixture").Running);
    }

    [Theory]
    [InlineData("hang", true)]
    [InlineData("cancel", false)]
    public async Task TimeoutAndCancellationTerminateWorkers(string command, bool timeout)
    {
        using var env = new PluginTestEnvironment();
        await env.Manager.InstallAsync(env.CreateFixture());
        var source = env.Write("source.txt", "unchanged");
        var session = await env.Manager.StartAsync("test.fixture", command, [new(source)]);
        var pid = session.ProcessId;
        var work = session.WorkDirectory;
        using var cancellation = new CancellationTokenSource();
        if (!timeout) cancellation.CancelAfter(200);
        var task = session.CallAsync<PluginResult>("execute", new PluginInvocation("cancel", command, [new(source)], work),
            timeout ? TimeSpan.FromMilliseconds(600) : TimeSpan.FromSeconds(10), cancellation.Token);
        if (timeout) await Assert.ThrowsAsync<TimeoutException>(() => task);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        var childFile = Path.Combine(work, "helper.pid");
        var child = File.Exists(childFile) ? int.Parse(File.ReadAllText(childFile)) : 0;
        await session.DisposeAsync();
        Assert.False(IsRunning(pid));
        if (child != 0) Assert.False(IsRunning(child));
        Assert.Equal("unchanged", File.ReadAllText(source));
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public async Task ClosingSessionPipeReapsHelperAndManagerShutdownReapsWorkers()
    {
        using var env = new PluginTestEnvironment();
        await env.Manager.InstallAsync(env.CreateFixture());
        var source = env.Write("source.txt", "test");
        var session = await env.Manager.StartAsync("test.fixture", "helper", [new(source)]);
        var pid = session.ProcessId;
        var call = session.CallAsync<PluginResult>("execute", new PluginInvocation("eof", "helper", [new(source)], session.WorkDirectory), TimeSpan.FromSeconds(10), default);
        var childFile = Path.Combine(session.WorkDirectory, "helper.pid");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!File.Exists(childFile)) await Task.Delay(20, deadline.Token);
        var child = int.Parse(await File.ReadAllTextAsync(childFile));
        await env.Manager.DisposeAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => call);
        Assert.False(IsRunning(pid));
        Assert.False(IsRunning(child));
    }

    [Fact]
    public async Task CommitRejectsEscapedOutputAndPreservesCollisionFiles()
    {
        using var env = new PluginTestEnvironment();
        var source = env.Write("source.txt", "source");
        var existing = env.Write("source.docx", "existing");
        var work = Path.Combine(env.Root, "work"); Directory.CreateDirectory(work);
        var result = Path.Combine(work, "result.docx"); File.WriteAllText(result, "result");
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(source, "bad.txt")], source, work, default));
        var link = Path.Combine(work, "link"); File.CreateSymbolicLink(link, source);
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(link, "bad.txt")], source, work, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginOutputCommitter.CommitAsync([new(result, "source.docx")], source, work, cancellation.Token));
        Assert.Equal("existing", File.ReadAllText(existing));
        var outputs = await PluginOutputCommitter.CommitAsync([new(result, "source.docx")], source, work, default);
        Assert.Equal("source 2.docx", Path.GetFileName(Assert.Single(outputs)));
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(env.Root, ".MacExplorer*"));
    }

    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string CreateSource(PluginTestEnvironment env, string extension)
    {
        if (extension == "svg") return env.Write("source.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"80\" height=\"40\"><rect width=\"80\" height=\"40\" fill=\"red\"/></svg>");
        var path = Path.Combine(env.Root, "source." + extension);
        if (extension == "docx")
        {
            using var document = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
            document.AddMainDocumentPart().Document = new W.Document(new W.Body(new W.Paragraph(new W.Run(new W.Text("PLUGIN_CONTENT")))));
            return path;
        }
        if (extension is "ico" or "icns")
        {
            using var bitmap = new SKBitmap(128, 128); bitmap.Erase(SKColors.Red);
            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100); var png = data.ToArray();
            using var writer = new BinaryWriter(File.Create(path));
            if (extension == "ico")
            {
                writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1);
                writer.Write((byte)128); writer.Write((byte)128); writer.Write((ushort)0);
                writer.Write((ushort)1); writer.Write((ushort)32); writer.Write(png.Length); writer.Write(22); writer.Write(png);
            }
            else
            {
                writer.Write(Encoding.ASCII.GetBytes("icns")); writer.Write(System.Net.IPAddress.HostToNetworkOrder(16 + png.Length));
                writer.Write(Encoding.ASCII.GetBytes("ic07")); writer.Write(System.Net.IPAddress.HostToNetworkOrder(8 + png.Length)); writer.Write(png);
            }
            return path;
        }
        return env.Write("source." + extension, "PLUGIN_CONTENT\n中文测试\n");
    }
}
