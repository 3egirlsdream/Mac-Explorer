using System.Net;
using System.Text;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using Xunit;

namespace MacExplorer.Tests;

[Trait("Kind", "Unit")]
[Trait("Area", "Network")]
public sealed class NetworkServiceTests
{
    [Theory]
    [InlineData("smb://nas.local/共享%20文件/项目", "nas.local", 445, "共享 文件", "/项目")]
    [InlineData("fe80::1", "fe80::1", 445, "", "/")]
    [InlineData("nas.local", "nas.local", 445, "", "/")]
    [InlineData("smb://[fe80::1]:1445/Files", "fe80::1", 1445, "Files", "/")]
    public void ParsesHostShareAndDirectoryWithoutPuttingCredentialsInUrl(string input, string host, int port, string share, string path)
    {
        var address = SmbAddress.Parse(input);
        Assert.Equal(host, address.Host); Assert.Equal(port, address.Port);
        Assert.Equal(share, address.Share); Assert.Equal(path, address.Path);
        Assert.Empty(address.MountUri.UserInfo);
        Assert.Equal("/Volumes/Files/项目", SmbAddress.Parse("nas", share: "Files", path: "/项目").LocationWithin("/Volumes/Files"));
    }

    [Theory]
    [InlineData("smb://user:secret@nas/Files")]
    [InlineData("https://nas/Files")]
    [InlineData("smb://nas/Files?password=x")]
    [InlineData("smb://nas/Files#fragment")]
    [InlineData("smb://nas/Files%2Fother")]
    public void RejectsCredentialsAndAmbiguousAddressComponents(string input) => Assert.Throws<ArgumentException>(() => SmbAddress.Parse(input));

    [Fact]
    public void DirectoryCannotEscapeTheSelectedShare()
    {
        Assert.Throws<ArgumentException>(() => SmbAddress.Parse("nas", path: "/../other"));
        Assert.Throws<ArgumentException>(() => SmbAddress.Parse("nas", share: "../other"));
    }

    [Fact]
    public void DiscoveryMergesAddressesWithoutCollapsingDistinctSameNameMachines()
    {
        var rows = NetworkDiscoveryService.Merge([
            new("b:one", "Office", "mac.local", 445, ["192.168.1.2", "fe80::2"]),
            new("w:one", "192.168.1.2", "192.168.1.2", 445, ["192.168.1.2"]),
            new("b:two", "Office", "other.local", 445, ["192.168.1.3"])]);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, s => s.Host == "mac.local" && s.Addresses.Contains("fe80::2"));
    }

    [Fact]
    public void WsdIgnoresPrintersAndUsesTheReplyAddressForSmb()
    {
        var xml = """
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope" xmlns:d="http://schemas.xmlsoap.org/ws/2005/04/discovery" xmlns:a="http://schemas.xmlsoap.org/ws/2004/08/addressing" xmlns:p="http://schemas.microsoft.com/windows/2006/08/wdp/pub">
            <s:Body><d:ProbeMatches><d:ProbeMatch><a:EndpointReference><a:Address>urn:uuid:fixture</a:Address></a:EndpointReference><d:Types>p:Computer</d:Types><d:XAddrs>http://untrusted.example/metadata</d:XAddrs></d:ProbeMatch></d:ProbeMatches></s:Body></s:Envelope>
            """;
        var row = Assert.Single(NetworkDiscoveryService.ParseWsd(Encoding.UTF8.GetBytes(xml), IPAddress.Parse("192.168.1.4")));
        Assert.Equal("192.168.1.4", row.Host); Assert.Equal(445, row.Port);
        Assert.Empty(NetworkDiscoveryService.ParseWsd(Encoding.UTF8.GetBytes(xml.Replace("p:Computer", "p:Printer")), IPAddress.Loopback));
        Assert.Empty(NetworkDiscoveryService.ParseWsd(Encoding.UTF8.GetBytes("<!DOCTYPE foo [<!ENTITY x SYSTEM 'file:///etc/passwd'>]><foo>&x;</foo>"), IPAddress.Loopback));
    }

    [Fact]
    public void SmbConfigurationPreservesShareDomainAndDatabasePassword()
    {
        var root = Path.Combine(Path.GetTempPath(), "smb-model-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "servers.json");
            var credentials = new PrivacySecurityFollowupTests.MemoryCredentials();
            using var service = new RemoteConnectionService(path, credentials);
            service.SaveServer(new RemoteServerInfo { Id = "smb", ServiceType = RemoteServiceType.Smb, Host = "nas", Port = 445,
                ShareName = "Files", Domain = "WORKGROUP", Password = "fixture-secret", LastConnectedAt = DateTimeOffset.Parse("2026-10-19T01:00:00Z") });
            using var reloaded = new RemoteConnectionService(path, credentials);
            var server = Assert.Single(reloaded.GetSavedServers());
            Assert.Equal(RemoteServiceType.Smb, server.ServiceType); Assert.Equal("Files", server.ShareName);
            Assert.Equal("WORKGROUP", server.Domain); Assert.Equal("fixture-secret", server.Password);
            Assert.Equal(DateTimeOffset.Parse("2026-10-19T01:00:00Z"), server.LastConnectedAt);
            Assert.DoesNotContain("fixture-secret", File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }
}
