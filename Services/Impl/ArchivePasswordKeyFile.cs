using System.Security.Cryptography;
using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services.Impl;

internal static class ArchivePasswordKeyFile
{
    private const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;

    internal static byte[]? ReadLocalKey(bool create)
        => Read(Path.Combine(RuntimePaths.DataDirectory, "archive-vault",
            DistributionChannel.IsAppStore ? "store" : "website"), create);

    internal static byte[]? Read(string directory, bool create)
    {
        if (OperatingSystem.IsWindows()) return null;
        if (!Directory.Exists(directory))
        {
            if (!create) return null;
            Directory.CreateDirectory(directory, PrivateDirectory);
        }
        if (new DirectoryInfo(directory).LinkTarget != null || File.GetUnixFileMode(directory) != PrivateDirectory)
            return null;

        var path = Path.Combine(directory, "key-v1");
        if (!File.Exists(path) && create && new FileInfo(path).LinkTarget == null) CreateKey(path);
        if (!File.Exists(path) || new FileInfo(path).LinkTarget != null || File.GetUnixFileMode(path) != PrivateFile)
            return null;

        using var stream = OperatingSystem.IsMacOS()
            ? new FileStream(MacSandboxNative.OpenRegularRead(path), FileAccess.Read) : File.OpenRead(path);
        if (stream.Length != 32) return null;
        var key = new byte[32];
        try { stream.ReadExactly(key); return key; }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CreateKey(string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        var key = RandomNumberGenerator.GetBytes(32);
        var ownsTemporary = false;
        try
        {
            using (var output = new FileStream(temporary, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile
            }))
            {
                ownsTemporary = true;
                output.Write(key);
                output.Flush(flushToDisk: true);
            }
            try { File.Move(temporary, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (ownsTemporary) File.Delete(temporary);
        }
    }
}
