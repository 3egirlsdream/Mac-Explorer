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

[Trait("Kind", "Integration")]
[Trait("Area", "Plugin")]
public sealed class PluginSystemTests
{
    [Fact(Explicit = true)]
    public async Task TrackingAnAlreadyExitedHelperPreservesItsResultAndDoesNotPublishItsPid()
    {
        var start = new ProcessStartInfo("/bin/echo") { RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add("converted");
        using var helper = Process.Start(start)!;
        await helper.WaitForExitAsync(TestContext.Current.CancellationToken);
        var publishedPids = new List<int>();
        void OnStarted(int pid, long _) => publishedPids.Add(pid);
        PluginChildProcesses.Started += OnStarted;
        try
        {
            using var registration = PluginChildProcesses.Track(helper);
            Assert.Empty(publishedPids);
            Assert.Equal(0, helper.ExitCode);
            Assert.Equal("converted", (await helper.StandardOutput.ReadToEndAsync()).TrimEnd());
        }
        finally { PluginChildProcesses.Started -= OnStarted; }
    }

    [Theory(Explicit = true)]
    [InlineData("txt", "to-docx")]
    [InlineData("md", "to-docx")]
    [InlineData("md", "to-pdf")]
    [InlineData("docx", "to-pdf")]
    [InlineData("svg", "to-png")]
    [InlineData("svg", "to-jpg")]
    [InlineData("ico", "to-png")]
    [InlineData("icns", "to-png")]
    public async Task BundledConversionPreservesInputAndProducesOutput(string extension, string command)
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
            Assert.Same(plugin.Manifest, session.Manifest);
            var invocation = new PluginInvocation("conversion-test", command, files, session.WorkDirectory);
            var preparation = await session.PrepareAsync(invocation, TimeSpan.FromSeconds(30), default);
            if (preparation.Configuration is { } configuration)
                invocation = invocation with { Parameters = new()
                {
                    ["width"] = JsonSerializer.SerializeToElement(configuration.Width),
                    ["height"] = JsonSerializer.SerializeToElement(configuration.Height)
                } };
            var result = await session.ExecuteAsync( invocation, TimeSpan.FromSeconds(40), default);
            Assert.Single(result.Outputs);
            Assert.False(File.Exists(Path.ChangeExtension(source, command.Replace("to-", "."))));
            var committed = await PluginOutputCommitter.CommitAsync(result.Outputs, files, session.WorkDirectory, default);
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
        Assert.Empty(Directory.GetDirectories(Path.Combine(env.Root, ".work")));
    }

    [Fact(Explicit = true)]
    public async Task CommitRejectsEscapedOutputAndPreservesCollisionFiles()
    {
        using var env = new PluginTestEnvironment();
        var source = env.Write("source.txt", "source");
        var existing = env.Write("source.docx", "existing");
        var work = Path.Combine(env.Root, "work"); Directory.CreateDirectory(work);
        var result = Path.Combine(work, "result.docx"); File.WriteAllText(result, "result");
        var files = new[] { new PluginFile(source) };
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(source, "bad.txt")], files, work, default));
        var link = Path.Combine(work, "link"); File.CreateSymbolicLink(link, source);
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(link, "bad.txt")], files, work, default));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PluginOutputCommitter.CommitAsync([new(result, "source.docx")], files, work, cancellation.Token));
        Assert.Equal("existing", File.ReadAllText(existing));
        var outputs = await PluginOutputCommitter.CommitAsync([new(result, "source.docx")], files, work, default);
        Assert.Equal("source 2.docx", Path.GetFileName(Assert.Single(outputs)));
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Empty(Directory.GetFiles(env.Root, ".MacExplorer*"));
    }

    [Fact(Explicit = true)]
    public async Task CommitPlacesBatchOutputsNextToTheirSources()
    {
        using var env = new PluginTestEnvironment();
        var first = env.Write("first.txt", "first");
        var nested = Path.Combine(env.Root, "nested"); Directory.CreateDirectory(nested);
        var second = Path.Combine(nested, "second.txt"); File.WriteAllText(second, "second");
        var work = Path.Combine(env.Root, "work"); Directory.CreateDirectory(work);
        var stagedFirst = Path.Combine(work, "a.docx"); File.WriteAllText(stagedFirst, "a");
        var stagedSecond = Path.Combine(work, "b.docx"); File.WriteAllText(stagedSecond, "b");
        var outputs = await PluginOutputCommitter.CommitAsync(
            [new(stagedFirst, "first.docx") { SourcePath = first }, new(stagedSecond, "second.docx") { SourcePath = second }],
            [new PluginFile(first), new PluginFile(second)], work, default);
        Assert.Equal([Path.Combine(env.Root, "first.docx"), Path.Combine(nested, "second.docx")], outputs);
        Assert.Equal("a", File.ReadAllText(outputs[0]));
        Assert.Equal("b", File.ReadAllText(outputs[1]));
    }

    [Fact(Explicit = true)]
    public async Task CommitRejectsMissingOrForeignBatchSources()
    {
        using var env = new PluginTestEnvironment();
        var first = env.Write("first.txt", "first");
        var second = env.Write("second.txt", "second");
        var foreign = env.Write("foreign.txt", "foreign");
        var work = Path.Combine(env.Root, "work"); Directory.CreateDirectory(work);
        var staged = Path.Combine(work, "a.docx"); File.WriteAllText(staged, "a");
        var files = new[] { new PluginFile(first), new PluginFile(second) };
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(staged, "first.docx")], files, work, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => PluginOutputCommitter.CommitAsync([new(staged, "first.docx") { SourcePath = foreign }], files, work, default));
        Assert.False(File.Exists(Path.Combine(env.Root, "foreign.docx")));
        Assert.False(File.Exists(Path.Combine(env.Root, "first.docx")));
    }

    [Fact(Explicit = true)]
    public void BatchMatchingRequiresEveryFileToMatchAndHonorsSelectionRange()
    {
        var match = new PluginMatch { Extensions = [".txt"], MinSelection = 2, MaxSelection = 3 };
        Assert.True(match.Matches([new("/tmp/one.txt"), new("/tmp/two.txt")]));
        Assert.False(match.Matches([new("/tmp/one.txt")]));
        Assert.False(match.Matches([new("/tmp/one.txt"), new("/tmp/two.png")]));
        Assert.False(match.Matches([new("/tmp/one.txt"), new("/tmp/two.txt"), new("/tmp/three.txt"), new("/tmp/four.txt")]));
    }

    [Fact(Explicit = true)]
    public async Task BundledConversionProcessesBatchInOneInvocation()
    {
        using var env = new PluginTestEnvironment();
        var first = env.Write("first.md", "# PLUGIN_CONTENT\n");
        var second = env.Write("second.md", "# PLUGIN_CONTENT\n");
        var plugin = Assert.Single(env.Manager.Plugins);
        var files = new[] { new PluginFile(first), new PluginFile(second) };
        await using (var session = await env.Manager.StartAsync(plugin.Manifest.Id, "to-docx", files))
        {
            var progress = new List<PluginProgress>();
            session.Progress += item => progress.Add(item);
            var result = await session.ExecuteAsync(
                new PluginInvocation("batch-conversion", "to-docx", files, session.WorkDirectory), TimeSpan.FromSeconds(60), default);
            Assert.Equal(2, result.Outputs.Length);
            Assert.Contains(progress, item => item.ShowInTaskPanel);
            var committed = await PluginOutputCommitter.CommitAsync(result.Outputs, files, session.WorkDirectory, default);
            Assert.Equal([Path.Combine(env.Root, "first.docx"), Path.Combine(env.Root, "second.docx")], committed);
            foreach (var output in committed)
            {
                using var document = WordprocessingDocument.Open(output, false);
                Assert.Contains("PLUGIN_CONTENT", document.MainDocumentPart!.Document.InnerText);
            }
        }
        Assert.Equal("# PLUGIN_CONTENT\n", File.ReadAllText(first));
        Assert.False(Assert.Single(env.Manager.Plugins).Running);
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
