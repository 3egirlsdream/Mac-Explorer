using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "FileIcon")]
public sealed class FileIconTests
{
    [Theory]
    [InlineData(".apk")]
    [InlineData(".APK")]
    public void ApkFilesResolveToAndroidPackageIcon(string extension)
    {
        Assert.Equal("file-android-package", FileIconResolver.ResolveIconKey(extension));
    }

}
