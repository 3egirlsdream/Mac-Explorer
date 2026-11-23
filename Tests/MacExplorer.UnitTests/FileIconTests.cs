using System.Globalization;
using System.Xml.Linq;
using MacExplorer.Models;
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

    [Theory]
    [InlineData(".CAF", "file-audio")]
    [InlineData(".aiff", "file-audio")]
    [InlineData(".pkf", "file-waveform")]
    [InlineData(".band", "file-audio-project")]
    [InlineData(".fcpbundle", "file-video-project")]
    [InlineData(".xmp", "file-metadata")]
    [InlineData(".jxl", "file-image")]
    [InlineData(".ora", "file-design")]
    [InlineData(".3mf", "file-3d")]
    [InlineData(".gpkg", "file-map")]
    [InlineData(".ods", "file-table")]
    [InlineData(".ics", "file-calendar")]
    [InlineData(".sln", "file-vs-solution")]
    public void KnownFormatsHaveSemanticIcons(string extension, string iconKey)
        => Assert.Equal(iconKey, FileIconResolver.ResolveIconKey(extension));

    [Fact]
    public void LocalPackageUsesAFileIconWithoutChangingDirectorySemantics()
    {
        var project = new FileSystemEntry
        {
            Name = "Song.band", FullPath = "/test/Song.band", IsDirectory = true, IconKey = "app-bundle"
        };
        var ordinaryFolder = new FileSystemEntry { Name = "Folder.mp3", FullPath = "/test/Folder.mp3", IsDirectory = true };
        Assert.True(project.IsFolder);
        Assert.False(project.DetailsIconSource.IsDirectory);
        Assert.Equal("file-audio-project", project.DetailsIconSource.IconKey);
        Assert.Equal("file-audio-project", project.GridIconSource.IconKey);
        Assert.True(ordinaryFolder.DetailsIconSource.IsDirectory);
    }

    [Fact]
    public void DynamicBadgesUseBoundedInvariantSvgWhileFixedArtworkIgnoresExtensions()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            XNamespace ns = "http://www.w3.org/2000/svg";
            var audio = XDocument.Parse(FileIconRenderer.Render("file-audio", ".aiff", 48));
            var badge = Assert.Single(audio.Descendants(ns + "text"));
            Assert.Equal("AIFF", badge.Value);
            Assert.Equal("4.8", badge.Attribute("font-size")?.Value);
            Assert.Equal("CSPROJ", FileIconRenderer.GetBadgeExtension("file-vs-project", ".csproj"));
            Assert.Equal("", FileIconRenderer.GetBadgeExtension("file-code", ".a-very-long-extension"));
            Assert.Equal(FileIconRenderer.Render("file-word", ".doc", 48), FileIconRenderer.Render("file-word", ".docx", 48));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }
}
