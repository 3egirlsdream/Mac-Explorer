using MacExplorer.Services;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Sftp")]
public sealed class SftpFileServiceDeleteTests
{
    [Fact]
    public void DeleteDirectoryTreeDeletesChildrenBeforeParents()
    {
        var tree = new Dictionary<string, IReadOnlyList<SftpFileService.RemoteDeleteEntry>>
        {
            ["/root"] =
            [
                new("file.txt", false, false),
                new("sub", true, false),
                new("linked-dir", true, true)
            ],
            ["/root/sub"] = [new("nested.txt", false, false)]
        };
        var operations = new List<string>();

        SftpFileService.DeleteDirectoryTree(
            "/root/",
            path => tree[path],
            path => operations.Add("file:" + path),
            path => operations.Add("dir:" + path));

        Assert.Equal(
        [
            "file:/root/file.txt",
            "file:/root/sub/nested.txt",
            "dir:/root/sub",
            "file:/root/linked-dir",
            "dir:/root"
        ], operations);
    }

    [Fact]
    public void DeleteDirectoryTreeDeletesEmptyDirectory()
    {
        var operations = new List<string>();

        SftpFileService.DeleteDirectoryTree(
            "/empty",
            _ => [],
            path => operations.Add("file:" + path),
            path => operations.Add("dir:" + path));

        Assert.Equal(["dir:/empty"], operations);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("////")]
    public void DeleteDirectoryTreeRejectsRemoteRoot(string path)
    {
        Assert.Throws<InvalidOperationException>(() =>
            SftpFileService.DeleteDirectoryTree(path, _ => [], _ => { }, _ => { }));
    }

}
