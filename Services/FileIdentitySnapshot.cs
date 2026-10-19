using System.Security.Cryptography;

namespace MacExplorer.Services;

internal sealed record FileIdentitySnapshot(string Identity, string Signature)
{
    public static FileIdentitySnapshot? Capture(string path, bool content)
    {
        if (VirtualPath.IsRemotePath(path)) return null;
        if (!File.Exists(path) && !Directory.Exists(path) && new FileInfo(path).LinkTarget == null) return null;
        var info = (File.GetAttributes(path) & FileAttributes.Directory) != 0
            ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        var identity = OperatingSystem.IsMacOS() ? Platforms.MacOS.MacSandboxNative.FileIdentity(path)
            : info.CreationTimeUtc.Ticks.ToString();
        if (identity == null) return null;
        if (!content) return new(identity, "");
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string value) => digest.AppendData(System.Text.Encoding.UTF8.GetBytes(value + "\0"));
        void Visit(FileSystemInfo entry, string relative)
        {
            entry.Refresh();
            Add(relative); Add(entry.LinkTarget ?? "");
            Add(entry.LastWriteTimeUtc.Ticks.ToString()); Add(((int)entry.Attributes).ToString());
            if (entry.LinkTarget != null) return;
            if (entry is DirectoryInfo dir)
            {
                foreach (var child in dir.EnumerateFileSystemInfos().OrderBy(x => x.Name, StringComparer.Ordinal))
                    Visit(child, relative + "/" + child.Name);
            }
            else
            {
                using var stream = File.OpenRead(entry.FullName);
                var buffer = new byte[128 * 1024]; int read;
                while ((read = stream.Read(buffer)) > 0) digest.AppendData(buffer.AsSpan(0, read));
            }
        }
        Visit(info, "");
        return new(identity, Convert.ToHexString(digest.GetHashAndReset()));
    }
}
