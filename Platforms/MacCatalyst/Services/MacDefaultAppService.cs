using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacDefaultAppService : IDefaultAppService
{
    private const string AppBundleId = "com.macexplorer.app";
    private const string FinderBundleId = "com.apple.finder";
    private const string FolderUti = "public.folder";
    private const string LaunchServicesDomain = "com.apple.LaunchServices/com.apple.launchservices.secure";
    private const string CoreFoundationLib =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    private const string CoreServicesLib =
        "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    [DllImport(CoreServicesLib)]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string cString, int encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr cf);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFStringGetLength(IntPtr value);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint bufferSize, uint encoding);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFPreferencesCopyValue(IntPtr key, IntPtr application, IntPtr user, IntPtr host);

    [DllImport(CoreFoundationLib)]
    private static extern void CFPreferencesSetValue(IntPtr key, IntPtr value, IntPtr application, IntPtr user, IntPtr host);

    [DllImport(CoreFoundationLib)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CFPreferencesSynchronize(IntPtr application, IntPtr user, IntPtr host);

    [DllImport(CoreFoundationLib)]
    private static extern nuint CFGetTypeID(IntPtr value);

    [DllImport(CoreFoundationLib)]
    private static extern nuint CFStringGetTypeID();

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFPropertyListCreateData(IntPtr allocator, IntPtr value, nint format, nuint options, out IntPtr error);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFPropertyListCreateWithData(IntPtr allocator, IntPtr data, nuint options, IntPtr format, out IntPtr error);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);

    [DllImport(CoreFoundationLib)]
    private static extern nint CFDataGetLength(IntPtr data);

    [DllImport(CoreFoundationLib)]
    private static extern IntPtr CFDataGetBytePtr(IntPtr data);

    private static class GlobalPreferences
    {
        private static readonly IntPtr Library = NativeLibrary.Load(CoreFoundationLib);
        internal static readonly IntPtr Application = Constant("kCFPreferencesAnyApplication");
        internal static readonly IntPtr User = Constant("kCFPreferencesCurrentUser");
        internal static readonly IntPtr Host = Constant("kCFPreferencesAnyHost");

        private static IntPtr Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(Library, name));
    }

    private const uint LSRolesAll = 0xFFFFFFFF;

    public bool IsDefaultFolderHandler()
    {
        try
        {
            return IsHandler(ReadFolderHandler(), AppBundleId) && IsHandler(ReadFileViewer(), AppBundleId);
        }
        catch { return false; }
    }

    public (bool Success, string Message) SetAsDefaultFolderHandler()
    {
        return TrySetHandler(AppBundleId, "设为默认文件管理器");
    }

    public (bool Success, string Message) ResetDefaultFolderHandler()
    {
        return TrySetHandler(FinderBundleId, "恢复 Finder 为默认");
    }

    private (bool Success, string Message) TrySetHandler(string bundleId, string operationName)
    {
        try
        {
            var previousFolderHandler = ReadFolderHandler();
            var previousFileViewer = ReadFileViewer();
            var fileViewer = bundleId == AppBundleId ? AppBundleId : null;
            try
            {
                WriteFolderHandler(bundleId);
                WriteFileViewer(fileViewer);
                if (!IsHandler(ReadFolderHandler(), bundleId) || !IsHandler(ReadFileViewer(), fileViewer))
                    throw new InvalidOperationException("系统未保存完整的默认查看器设置。");
            }
            catch
            {
                // Keep the two system preferences together if either update fails.
                try
                {
                    WriteFolderHandler(previousFolderHandler ?? FinderBundleId);
                }
                finally
                {
                    WriteFileViewer(previousFileViewer);
                }
                throw;
            }

            return bundleId == AppBundleId
                ? (true, "默认文件管理器设置已保存。请重启电脑，使更改生效。\n\n重启后，通过系统打开文件夹及兼容应用的「在 Finder 中显示」将使用 MacExplorer。\n桌面和系统打开／保存对话框仍由 macOS 管理。")
                : (true, "恢复 Finder 的设置已保存。请重启电脑，使更改生效。");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] SetHandler failed: {ex}");
            return (false, $"{operationName}未完成：{ex.Message}");
        }
    }

    private static bool IsHandler(string? actual, string? expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    internal virtual string? ReadFolderHandler()
    {
        // Launch Services can keep the old handler cached until the next login.
        // The switch reflects the saved configuration that will apply after restart.
        var handlers = ReadFolderHandlers();
        var savedHandler = GetSavedFolderHandler(handlers);
        if (savedHandler != null) return savedHandler;

        var uti = CFStringCreateWithCString(IntPtr.Zero, FolderUti, 0x08000100);
        var result = IntPtr.Zero;
        try
        {
            result = LSCopyDefaultRoleHandlerForContentType(uti, LSRolesAll);
            return result == IntPtr.Zero ? null : ReadCfString(result);
        }
        finally
        {
            if (result != IntPtr.Zero) CFRelease(result);
            CFRelease(uti);
        }
    }

    internal virtual void WriteFolderHandler(string bundleId)
    {
        var handlers = UpdateFolderHandlers(ReadFolderHandlers(), bundleId);
        var xml = new XDocument(new XElement("plist", new XAttribute("version", "1.0"), handlers));
        var bytes = Encoding.UTF8.GetBytes(xml.ToString(SaveOptions.DisableFormatting));
        var data = CFDataCreate(IntPtr.Zero, bytes, bytes.Length);
        var value = IntPtr.Zero;
        var error = IntPtr.Zero;
        try
        {
            value = CFPropertyListCreateWithData(IntPtr.Zero, data, 0, IntPtr.Zero, out error);
            if (value == IntPtr.Zero)
                throw new InvalidOperationException("无法生成文件夹关联配置。");
            WithFolderHandlersPreference((key, domain) =>
            {
                CFPreferencesSetValue(key, value, domain, GlobalPreferences.User, GlobalPreferences.Host);
                if (!CFPreferencesSynchronize(domain, GlobalPreferences.User, GlobalPreferences.Host))
                    throw new InvalidOperationException("文件夹关联配置保存失败。");
            });
        }
        finally
        {
            if (error != IntPtr.Zero) CFRelease(error);
            if (value != IntPtr.Zero) CFRelease(value);
            CFRelease(data);
        }
    }

    private static XElement ReadFolderHandlers()
    {
        var handlers = new XElement("array");
        WithFolderHandlersPreference((key, domain) =>
        {
            if (!CFPreferencesSynchronize(domain, GlobalPreferences.User, GlobalPreferences.Host))
                throw new InvalidOperationException("文件夹关联配置读取失败。");
            var value = CFPreferencesCopyValue(key, domain, GlobalPreferences.User, GlobalPreferences.Host);
            if (value == IntPtr.Zero) return;
            var data = IntPtr.Zero;
            var error = IntPtr.Zero;
            try
            {
                data = CFPropertyListCreateData(IntPtr.Zero, value, 100 /* XML v1.0 */, 0, out error);
                if (data == IntPtr.Zero)
                    throw new InvalidOperationException("无法读取文件夹关联配置。");
                var bytes = new byte[checked((int)CFDataGetLength(data))];
                Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length);
                handlers = XDocument.Parse(Encoding.UTF8.GetString(bytes)).Root?.Element("array")
                    ?? throw new InvalidOperationException("LSHandlers 配置格式异常，未修改已有配置。");
            }
            finally
            {
                if (error != IntPtr.Zero) CFRelease(error);
                if (data != IntPtr.Zero) CFRelease(data);
                CFRelease(value);
            }
        });
        return handlers;
    }

    private static void WithFolderHandlersPreference(Action<IntPtr, IntPtr> action)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, "LSHandlers", 0x08000100);
        var domain = CFStringCreateWithCString(IntPtr.Zero, LaunchServicesDomain, 0x08000100);
        try { action(key, domain); }
        finally
        {
            CFRelease(key);
            CFRelease(domain);
        }
    }

    internal static string? GetSavedFolderHandler(XElement handlers) => handlers.Elements("dict")
        .Where(entry => DictionaryString(entry, "LSHandlerContentType") == FolderUti)
        .Select(entry => DictionaryString(entry, "LSHandlerRoleAll") ?? DictionaryString(entry, "LSHandlerRoleViewer"))
        .LastOrDefault(value => value != null);

    internal static XElement UpdateFolderHandlers(XElement handlers, string bundleId) => new("array",
        handlers.Elements().Where(entry => DictionaryString(entry, "LSHandlerContentType") != FolderUti)
            .Select(entry => new XElement(entry)),
        new XElement("dict",
            new XElement("key", "LSHandlerContentType"), new XElement("string", FolderUti),
            new XElement("key", "LSHandlerRoleAll"), new XElement("string", bundleId)));

    private static string? DictionaryString(XElement entry, string key) =>
        entry.Elements("key").FirstOrDefault(element => element.Value == key)?.NextNode is XElement value
        && value.Name == "string" ? value.Value : null;

    internal virtual string? ReadFileViewer()
    {
        SynchronizeFileViewer();
        var key = CFStringCreateWithCString(IntPtr.Zero, "NSFileViewer", 0x08000100);
        var result = IntPtr.Zero;
        try
        {
            result = CFPreferencesCopyValue(key, GlobalPreferences.Application, GlobalPreferences.User, GlobalPreferences.Host);
            if (result != IntPtr.Zero && CFGetTypeID(result) != CFStringGetTypeID())
                throw new InvalidOperationException("NSFileViewer 设置格式异常，请先移除该系统偏好后重试。");
            return result == IntPtr.Zero ? null : ReadCfString(result);
        }
        finally
        {
            if (result != IntPtr.Zero) CFRelease(result);
            CFRelease(key);
        }
    }

    internal virtual void WriteFileViewer(string? bundleId)
    {
        var key = CFStringCreateWithCString(IntPtr.Zero, "NSFileViewer", 0x08000100);
        var value = bundleId == null ? IntPtr.Zero : CFStringCreateWithCString(IntPtr.Zero, bundleId, 0x08000100);
        try
        {
            CFPreferencesSetValue(key, value, GlobalPreferences.Application, GlobalPreferences.User, GlobalPreferences.Host);
            SynchronizeFileViewer();
        }
        finally
        {
            if (value != IntPtr.Zero) CFRelease(value);
            CFRelease(key);
        }
    }

    private static void SynchronizeFileViewer()
    {
        if (!CFPreferencesSynchronize(GlobalPreferences.Application, GlobalPreferences.User, GlobalPreferences.Host))
            throw new InvalidOperationException("默认文件查看器设置同步失败。");
    }

    private static string ReadCfString(IntPtr value)
    {
        const uint utf8 = 0x08000100;
        if (value == IntPtr.Zero) return string.Empty;
        var length = CFStringGetLength(value);
        var bufferSize = CFStringGetMaximumSizeForEncoding(length, utf8) + 1;
        if (bufferSize <= 1 || bufferSize > int.MaxValue) return string.Empty;
        var buffer = new byte[(int)bufferSize];
        return CFStringGetCString(value, buffer, bufferSize, utf8)
            ? System.Text.Encoding.UTF8.GetString(buffer).TrimEnd('\0')
            : string.Empty;
    }
}
