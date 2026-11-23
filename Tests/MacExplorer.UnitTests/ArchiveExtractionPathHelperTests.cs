using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Archive")]
public sealed class ArchiveExtractionPathHelperTests
{
    [Theory]
    [InlineData("Project.zip", "Project")]
    [InlineData("Project.tar.gz", "Project")]
    [InlineData("Project.TAR.BZ2", "Project")]
    [InlineData("Project.tar.xz", "Project")]
    [InlineData("Project.tgz", "Project")]
    [InlineData("Project.tar.lz", "Project")]
    [InlineData("Project.TAR.Z", "Project")]
    [InlineData("Project.tar.gzip", "Project")]
    [InlineData("Project.tar.bzip2", "Project")]
    [InlineData("Project.tar.zstd", "Project")]
    [InlineData("Project.tlz", "Project")]
    [InlineData("Project.Z", "Project")]
    [InlineData("Project.arj", "Project")]
    public void GetArchiveFolderName_RemovesArchiveExtension(string fileName, string expected)
    {
        var archivePath = Path.Combine("/tmp", fileName);

        var actual = ArchiveExtractionPathHelper.GetArchiveFolderName(archivePath);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void CreateUniqueExtractionDirectory_AddsSuffixForExistingFilesAndFolders()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fkfinder-extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Project"));
            File.WriteAllText(Path.Combine(root, "Project 2"), "conflict");

            var destinationPath = ArchiveExtractionPathHelper.CreateUniqueExtractionDirectory(
                root,
                Path.Combine(root, "Project.zip"));

            Assert.Equal(Path.Combine(root, "Project 3"), destinationPath);
            Assert.True(Directory.Exists(destinationPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

}
