using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public sealed class MacFileTagStore(IDirectoryChangeNotifier? notifier = null) : IFileTagStore
{
    private const string Attribute = "com.apple.metadata:_kMDItemUserTags";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern nint getxattr(string path, string name, byte[]? value, nuint size, uint position, int options);
    [DllImport("libSystem.B.dylib", SetLastError = true)]
    private static extern int setxattr(string path, string name, byte[] value, nuint size, uint position, int options);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(CoreFoundation)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(CoreFoundation)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
    [DllImport(CoreFoundation)] private static extern IntPtr CFPropertyListCreateWithData(IntPtr allocator, IntPtr data, nuint options, IntPtr format, out IntPtr error);
    [DllImport(CoreFoundation)] private static extern IntPtr CFPropertyListCreateData(IntPtr allocator, IntPtr value, nint format, nuint options, out IntPtr error);
    [DllImport(CoreFoundation)] private static extern IntPtr CFURLCreateFromFileSystemRepresentation(IntPtr allocator, byte[] path, nint length, [MarshalAs(UnmanagedType.I1)] bool isDirectory);
    [DllImport(CoreFoundation)] private static extern void CFURLClearResourcePropertyCache(IntPtr url);

    public Task<IReadOnlyList<NativeFileTag>> ReadAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<NativeFileTag>>(() => Read(filePath), cancellationToken);

    internal static IReadOnlyList<NativeFileTag> Read(string filePath)
    {
        var length = getxattr(filePath, Attribute, null, 0, 0, 0);
        if (length < 0)
        {
            if (Marshal.GetLastPInvokeError() == 93) return []; // ENOATTR: a readable file with no tags.
            throw new IOException("无法读取文件标签", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        if (length == 0) return [];
        var bytes = new byte[checked((int)length)];
        var read = getxattr(filePath, Attribute, bytes, (nuint)bytes.Length, 0, 0);
        if (read < 0) throw new IOException("无法读取文件标签", new Win32Exception(Marshal.GetLastPInvokeError()));
        var xml = XDocument.Parse(Encoding.UTF8.GetString(ConvertPlist(bytes[..checked((int)read)], 100)));
        return xml.Root?.Element("array")?.Elements("string").Select(element =>
        {
            var raw = element.Value;
            var separator = raw.LastIndexOf('\n');
            return separator >= 0 && int.TryParse(raw[(separator + 1)..], out var color)
                ? new NativeFileTag(raw[..separator], color)
                : new NativeFileTag(raw);
        }).ToArray() ?? [];
    }

    public Task WriteAsync(string filePath, IReadOnlyList<NativeFileTag> tags, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) notifier?.SuppressRefresh([directory], TimeSpan.FromSeconds(3));
            var xml = new XDocument(new XElement("plist", new XAttribute("version", "1.0"),
                new XElement("array", tags.Select(tag => new XElement("string", tag.Name + "\n" + tag.ColorId)))));
            var bytes = ConvertPlist(Encoding.UTF8.GetBytes(xml.ToString()), 200);
            if (setxattr(filePath, Attribute, bytes, (nuint)bytes.Length, 0, 0) != 0)
                throw new IOException("无法写入 Finder 标签", new Win32Exception(Marshal.GetLastPInvokeError()));
            var pathBytes = Encoding.UTF8.GetBytes(filePath);
            var url = CFURLCreateFromFileSystemRepresentation(IntPtr.Zero, pathBytes, pathBytes.Length, Directory.Exists(filePath));
            if (url != IntPtr.Zero)
            {
                CFURLClearResourcePropertyCache(url);
                CFRelease(url);
            }
        }, cancellationToken);

    private static byte[] ConvertPlist(byte[] bytes, int format)
    {
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        var value = IntPtr.Zero;
        var output = IntPtr.Zero;
        var error = IntPtr.Zero;
        try
        {
            value = CFPropertyListCreateWithData(IntPtr.Zero, data, 0, IntPtr.Zero, out error);
            if (value == IntPtr.Zero) throw new IOException("文件标签属性格式无效");
            if (error != IntPtr.Zero) { CFRelease(error); error = IntPtr.Zero; }
            output = CFPropertyListCreateData(IntPtr.Zero, value, format, 0, out error);
            if (output == IntPtr.Zero) throw new IOException("无法转换文件标签属性");
            var result = new byte[checked((int)CFDataGetLength(output))];
            Marshal.Copy(CFDataGetBytePtr(output), result, 0, result.Length);
            return result;
        }
        finally
        {
            if (error != IntPtr.Zero) CFRelease(error);
            if (output != IntPtr.Zero) CFRelease(output);
            if (value != IntPtr.Zero) CFRelease(value);
            if (data != IntPtr.Zero) CFRelease(data);
        }
    }
}
