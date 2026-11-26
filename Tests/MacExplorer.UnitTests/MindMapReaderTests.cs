using System.IO.Compression;
using MacExplorer.Services.Preview;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Preview")]
public sealed class MindMapReaderTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("FKFinder_MindMap_");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task XMindPrefersJsonAndPreservesSheetsFoldedAndFloatingTopics()
    {
        var path = Package("dual.xmind",
            ("content.xml", "<xmap-content><sheet><topic><title>Legacy</title></topic></sheet></xmap-content>"),
            ("content.json", """
                [
                  {"title":"Plan","rootTopic":{"title":"Main","children":{
                    "attached":[{"title":"Folded","branch":"folded","children":{"attached":[{"title":"Hidden"}]}}],
                    "detached":[{"title":"Floating"}]}}},
                  {"title":"Notes","rootTopic":{"title":"Next"}}
                ]
                """));
        var document = await Load(path);
        Assert.Equal(new[] { "Plan", "Notes" }, document.Sheets.Select(sheet => sheet.Title));
        var sheet = document.Sheets[0];
        Assert.Equal(new[] { "Main", "Floating" }, sheet.Roots.Select(node => node.Title));
        var folded = Assert.Single(sheet.Roots[0].Children);
        Assert.True(folded.IsCollapsed);
        Assert.Equal("Hidden", Assert.Single(folded.Children).Title);
    }

    [Fact]
    public async Task XMindLegacyReadsNamespacedXmlWithoutAThumbnail()
    {
        var path = Package("legacy.xmind", ("content.xml", """
            <xmap-content xmlns="urn:xmind:xmap:xmlns:content:2.0">
              <sheet><title>Legacy sheet</title>
                <topic><title>Legacy root</title><children><topics type="attached">
                  <topic branch="folded"><title>Legacy branch</title><children><topics type="attached">
                    <topic><title>Legacy leaf</title></topic>
                  </topics></children></topic>
                </topics><topics type="detached"><topic><title>Floating</title></topic></topics></children></topic>
              </sheet>
            </xmap-content>
            """));
        var sheet = Assert.Single((await Load(path)).Sheets);
        Assert.Equal("Legacy sheet", sheet.Title);
        Assert.Equal(new[] { "Legacy root", "Floating" }, sheet.Roots.Select(node => node.Title));
        var branch = Assert.Single(sheet.Roots[0].Children);
        Assert.True(branch.IsCollapsed);
        Assert.Equal("Legacy leaf", Assert.Single(branch.Children).Title);
    }

    [Fact]
    public async Task FreeplaneReadsRichTopicAndOpmlKeepsIndependentOutlineRoots()
    {
        var mindMap = await Load(Plain("sample.mm", """
            <?xml version="1.0"?>
            <!DOCTYPE map SYSTEM "freeplane.dtd">
            <map><node TEXT="Root"><node FOLDED="true">
              <richcontent TYPE="NODE"><html><body><p>Rich <b>title</b></p></body></html></richcontent>
              <node TEXT="Leaf"/>
            </node></node></map>
            """));
        var rich = Assert.Single(Assert.Single(Assert.Single(mindMap.Sheets).Roots).Children);
        Assert.Equal("Rich title", rich.Title);
        Assert.True(rich.IsCollapsed);

        var opml = await Load(Plain("sample.opml", """
            <opml version="2.0"><head><title>Outline</title></head><body>
              <outline text="First"><outline text="Nested"/></outline><outline text="Second"/>
            </body></opml>
            """));
        var sheet = Assert.Single(opml.Sheets);
        Assert.Equal("Outline", sheet.Title);
        Assert.Equal(new[] { "First", "Second" }, sheet.Roots.Select(node => node.Title));
        Assert.Equal("Nested", Assert.Single(sheet.Roots[0].Children).Title);
    }

    [Fact]
    public async Task MindManagerReadsNamespacedTopicTreeWithoutAThumbnail()
    {
        var path = Package("topics.mmap", ("Document.xml", """
            <ap:Map xmlns:ap="http://schemas.mindjet.com/MindManager/Application/2003">
              <ap:OneTopic><ap:Topic>
                <ap:Text PlainText="Root"/><ap:SubTopics>
                  <ap:Topic><ap:Text PlainText="Nested &amp; more"/></ap:Topic>
                </ap:SubTopics>
              </ap:Topic></ap:OneTopic>
            </ap:Map>
            """));
        var root = Assert.Single(Assert.Single((await Load(path)).Sheets).Roots);
        Assert.Equal("Root", root.Title);
        Assert.Equal("Nested & more", Assert.Single(root.Children).Title);
    }

    [Theory]
    [InlineData("""{"meta":{"diagramInfo":{"category":"mindmap"}},"diagram":{"elements":{"root":{"title":"Root","children":[{"title":"<b>Nested</b> &amp; more","collapsed":true,"children":[{"title":"Leaf"}]}]}}}}""")]
    [InlineData("""{"nodes":[{"id":1,"text":"Root"},{"id":2,"parentId":1,"text":"Nested & more","collapsed":true},{"id":3,"parentId":2,"text":"Leaf"}]}""")]
    [InlineData("""{"data":"{\"rootNode\":{\"text\":\"Root\",\"children\":[{\"text\":\"Nested & more\",\"folded\":true,\"children\":[{\"text\":\"Leaf\"}]}]}}"}""")]
    public async Task ProcessOnReadsTreesAndFlatParentLinks(string payload)
    {
        var root = Assert.Single(Assert.Single((await Load(Plain("sample.pos", payload))).Sheets).Roots);
        Assert.Equal("Root", root.Title);
        var child = Assert.Single(root.Children);
        Assert.Equal("Nested & more", child.Title);
        Assert.True(child.IsCollapsed);
        Assert.Equal("Leaf", Assert.Single(child.Children).Title);
    }

    [Fact]
    public async Task ProcessOnSchemaExportKeepsBothBranchLists()
    {
        var path = Plain("schema.pos", """
            {"diagram":{"elements":{"root":true,"id":"root","structure":"mind_free","title":"Root",
              "children":[{"id":"r","parent":"root","title":"Right<br>branch"}],
              "leftChildren":[{"id":"l","parent":"root","title":"Left","collapsed":true,"children":[{"title":"Leaf"}]}]}},
             "meta":{"diagramInfo":{"title":"Document","category":"mind_free"},"type":"ProcessOn Schema File","version":"1.0"}}
            """);
        var sheet = Assert.Single((await Load(path)).Sheets);
        Assert.Equal("Document", sheet.Title);
        var root = Assert.Single(sheet.Roots);
        Assert.Equal(new[] { "Right\nbranch", "Left" }, root.Children.Select(child => child.Title));
        Assert.True(root.Children[1].IsCollapsed);
    }

    [Theory]
    [InlineData("""{"meta":{"diagramInfo":{"category":"flow"}},"diagram":{"root":{"title":"Start","children":[{"title":"End"}]}}}""")]
    [InlineData("""{"diagram":{"elements":{"elements":{"start":{"name":"terminator"},"end":{"name":"terminator"}}}}}""")]
    [InlineData("""{"cells":[{"id":"a","label":"Start"},{"id":"b","label":"End"},{"id":"e","source":"a","target":"b"}]}""")]
    [InlineData("""{"nodes":[{"id":"a","parentId":"b","text":"A"},{"id":"b","parentId":"a","text":"B"}]}""")]
    public async Task ProcessOnRejectsFlowchartsAndMalformedParentTrees(string payload)
        => Assert.Equal(MindMapReader.Status.Unavailable, (await Read(Plain("flow.pos", payload))).Status);

    [Fact]
    public async Task SourceMmIsDistinctFromBrokenMapsAndUnsupportedPackages()
    {
        Assert.Equal(MindMapReader.Status.NotMindMap, (await Read(Plain("source.mm", "#include <Foundation/Foundation.h>"))).Status);
        Assert.Equal(MindMapReader.Status.Unavailable, (await Read(Plain("broken.mm", "<map><node"))).Status);
        Assert.Equal(MindMapReader.Status.Unavailable, (await Read(Plain("bad.xmind", "broken ZIP"))).Status);
        Assert.Equal(MindMapReader.Status.Unavailable, (await Read(Plain("empty.opml", "<opml><body/></opml>"))).Status);
        Assert.Equal(MindMapReader.Status.Unavailable, (await Read(Package("empty.mmap", ("Preview.png", "not a map")))).Status);
    }

    [Fact]
    public async Task PasswordProtectedPackageHasNoPreview()
    {
        var path = Path.Combine(_directory.FullName, "locked.xmind");
        using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipOutputStream(File.Create(path)) { Password = "fixture-password" })
        {
            zip.PutNextEntry(new ICSharpCode.SharpZipLib.Zip.ZipEntry("content.json") { AESKeySize = 256 });
            zip.Write("""[{"rootTopic":{"title":"Locked topic"}}]"""u8.ToArray());
        }
        Assert.Equal(MindMapReader.Status.Unavailable, (await Read(path)).Status);
    }

    [Fact]
    public async Task LargeHierarchyRemainsUsableAndOversizedInputStops()
    {
        var topics = string.Concat(Enumerable.Range(0, 4999).Select(index => $"<node TEXT=\"Topic {index}\"/>"));
        var file = Plain("many.mm", $"<map><node TEXT=\"Root\">{topics}</node></map>");
        var roots = Assert.Single((await Load(file)).Sheets).Roots;
        Assert.Equal(4999, Assert.Single(roots).Children.Count);
        Assert.Equal(MindMapReader.Status.TooLarge,
            (await Read(Plain("too-many.mm", $"<map><node TEXT=\"Root\">{topics}<node TEXT=\"Extra\"/></node></map>"))).Status);

        var depth = string.Concat(Enumerable.Repeat("<node TEXT=\"Topic\">", 100));
        var closing = string.Concat(Enumerable.Repeat("</node>", 100));
        Assert.Equal(MindMapReader.Status.TooLarge, (await Read(Plain("deep.mm", $"<map>{depth}{closing}</map>"))).Status);

        var oversized = Package("oversized.xmind", ("content.json", new string(' ', 16 * 1024 * 1024 + 1)));
        Assert.Equal(MindMapReader.Status.TooLarge, (await Read(oversized)).Status);
    }

    [Fact]
    public async Task CancelledReadDoesNotPublishADocument()
    {
        var path = Plain("cancel.mm", """<map><node TEXT="Root"/></map>""");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MindMapReader.ReadAsync(path, ".mm", new CancellationToken(canceled: true)));
    }

    private string Plain(string name, string content)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Package(string name, params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_directory.FullName, name);
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(entry).Open());
            writer.Write(content);
        }
        return path;
    }

    private static Task<(MindMapReader.Status Status, MindMapDocument? Document)> Read(string path)
        => MindMapReader.ReadAsync(path, Path.GetExtension(path), Token);

    private static async Task<MindMapDocument> Load(string path)
    {
        var result = await Read(path);
        Assert.Equal(MindMapReader.Status.Ready, result.Status);
        return Assert.IsType<MindMapDocument>(result.Document);
    }

    public void Dispose() => _directory.Delete(recursive: true);
}
