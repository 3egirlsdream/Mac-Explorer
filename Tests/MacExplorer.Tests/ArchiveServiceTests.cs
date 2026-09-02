using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public sealed class ArchiveServiceTests
{
    [Fact]
    public async Task TarGzArchive_ListsItsTarContents()
    {
        var root = Path.Combine(Path.GetTempPath(), "MacExplorer-archive-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "sample.tar.gz");
        try
        {
            using (var output = File.Create(path))
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
            {
                var file = new PaxTarEntry(TarEntryType.RegularFile, "docs/readme.txt")
                {
                    DataStream = new MemoryStream(Encoding.UTF8.GetBytes("super preview"))
                };
                tar.WriteEntry(file);
            }

            var service = new ArchiveService();
            var rootEntries = await service.GetArchiveContentsAsync(path);
            var docs = Assert.Single(rootEntries);
            Assert.True(docs.IsDirectory);
            Assert.Equal("docs", docs.Name);

            var nestedEntries = await service.GetArchiveContentsAsync(path, "docs");
            var readme = Assert.Single(nestedEntries);
            Assert.False(readme.IsDirectory);
            Assert.Equal("readme.txt", readme.Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
