using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

public class OssFileServiceTests
{
    [Theory]
    [InlineData("__remote:srv1:/photos/2026/a.jpg", "photos/2026/a.jpg")]
    [InlineData("__remote:srv1:/", "")]
    [InlineData("/photos/a.jpg", "photos/a.jpg")]
    [InlineData("photos/a.jpg", "photos/a.jpg")]
    [InlineData("/", "")]
    public void ToObjectKey_StripsSentinelAndLeadingSlash(string path, string expected)
    {
        Assert.Equal(expected, OssFileService.ToObjectKey(path));
    }

    [Theory]
    [InlineData("photos", "photos/")]
    [InlineData("photos/2026", "photos/2026/")]
    [InlineData("photos/", "photos/")]
    [InlineData("", "")]
    public void ToPrefix_AppendsExactlyOneSlash(string key, string expected)
    {
        Assert.Equal(expected, OssFileService.ToPrefix(key));
    }

    [Theory]
    [InlineData("photos/2026/a.jpg", "a.jpg")]
    [InlineData("photos/2026/", "2026")]
    [InlineData("a.jpg", "a.jpg")]
    public void KeyToName_TakesLastSegment(string key, string expected)
    {
        Assert.Equal(expected, OssFileService.KeyToName(key));
    }

    [Theory]
    [InlineData("oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("https://oss-cn-hangzhou.aliyuncs.com", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("http://oss-cn-hangzhou.aliyuncs.com/", "oss-cn-hangzhou.aliyuncs.com")]
    [InlineData("  oss-cn-beijing.aliyuncs.com  ", "oss-cn-beijing.aliyuncs.com")]
    [InlineData("", "")]
    public void NormalizeEndpoint_AcceptsWhatUsersPaste(string endpoint, string expected)
    {
        Assert.Equal(expected, OssClientFactory.NormalizeEndpoint(endpoint));
    }

    [Theory]
    [InlineData("my-bucket", "my-bucket")]
    [InlineData("my-bucket.oss-cn-shenzhen.aliyuncs.com", "my-bucket")]
    [InlineData("oss://my-bucket/", "my-bucket")]
    [InlineData("https://my-bucket.oss-cn-shenzhen.aliyuncs.com", "my-bucket")]
    [InlineData("  my-bucket  ", "my-bucket")]
    [InlineData("", "")]
    public void NormalizeBucket_StripsEndpointSuffixAndScheme(string bucket, string expected)
    {
        Assert.Equal(expected, OssClientFactory.NormalizeBucket(bucket));
    }

    [Theory]
    [InlineData("my-bucket.oss-cn-shenzhen.aliyuncs.com", "oss-cn-shenzhen.aliyuncs.com")]
    [InlineData("https://my-bucket.oss-cn-shenzhen.aliyuncs.com", "oss-cn-shenzhen.aliyuncs.com")]
    [InlineData("oss-cn-shenzhen.aliyuncs.com", "oss-cn-shenzhen.aliyuncs.com")]
    public void NormalizeEndpoint_StripsBucketPrefix(string endpoint, string expected)
    {
        Assert.Equal(expected, OssClientFactory.NormalizeEndpoint(endpoint));
    }

    [Theory]
    [InlineData("oss://my-bucket/", "my-bucket", "/")]
    [InlineData("oss://my-bucket/photos", "my-bucket", "/photos")]
    [InlineData("oss://my-bucket/photos/2026/", "my-bucket", "/photos/2026")]
    [InlineData("photos", "my-bucket", "/photos")]
    [InlineData("/photos/", "my-bucket", "/photos")]
    [InlineData("", "my-bucket", "/")]
    [InlineData("/", "my-bucket", "/")]
    public void NormalizeDefaultPath_YieldsBucketRelativePath(string path, string bucket, string expected)
    {
        Assert.Equal(expected, OssClientFactory.NormalizeDefaultPath(path, bucket));
    }

    [Fact]
    public void NormalizeDefaultPath_KeepsSegmentThatIsNotTheBucket()
    {
        // A folder that happens to sit at the root must not be mistaken for the bucket.
        Assert.Equal("/photos", OssClientFactory.NormalizeDefaultPath("oss://photos", "my-bucket"));
    }

    [Fact]
    public void RemotePathHelper_RoundTripsSentinelPaths()
    {
        var directory = VirtualPath.BuildRemotePath("srv1", "/photos");
        var child = RemotePathHelper.CombinePath(directory, "a.jpg");

        Assert.Equal("__remote:srv1:/photos/a.jpg", child);
        Assert.Equal(directory, RemotePathHelper.GetParentPath(child));
        Assert.Equal("photos/a.jpg", OssFileService.ToObjectKey(child));
    }

    [Fact]
    public void RemotePathHelper_ParentOfRootStaysRoot()
    {
        var root = VirtualPath.BuildRemotePath("srv1", "/");
        Assert.Equal(root, RemotePathHelper.GetParentPath(root));
    }
}
