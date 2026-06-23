using System.Diagnostics;
using System.Runtime.InteropServices;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacDefaultAppService : IDefaultAppService
{
    private const string AppBundleId = "com.macexplorer.app";
    private const string FinderBundleId = "com.apple.finder";
    private const string FolderUti = "public.folder";

    private const string CoreServicesLib =
        "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    [DllImport(CoreServicesLib)]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    [DllImport(CoreServicesLib)]
    private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr bundleId);

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

    private const uint LSRolesAll = 0xFFFFFFFF;
    private const uint LSRolesViewer = 0x00000002;

    public bool IsDefaultFolderHandler()
    {
        try
        {
            var utiHandle = CFStringCreateWithCString(IntPtr.Zero, FolderUti, 0x08000100); // UTF8
            var result = LSCopyDefaultRoleHandlerForContentType(utiHandle, LSRolesAll);
            CFRelease(utiHandle);

            if (result == IntPtr.Zero) return false;
            var currentHandler = ReadCfString(result);
            CFRelease(result);

            return string.Equals(currentHandler, AppBundleId, StringComparison.OrdinalIgnoreCase);
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
            var utiHandle = CFStringCreateWithCString(IntPtr.Zero, FolderUti, 0x08000100);
            var bundleHandle = CFStringCreateWithCString(IntPtr.Zero, bundleId, 0x08000100);

            var status = LSSetDefaultRoleHandlerForContentType(utiHandle, LSRolesAll, bundleHandle);
            if (status != 0)
                status = LSSetDefaultRoleHandlerForContentType(utiHandle, LSRolesViewer, bundleHandle);

            CFRelease(utiHandle);
            CFRelease(bundleHandle);

            if (status == 0)
            {
                return bundleId == AppBundleId
                    ? (true, "已成功设为默认文件夹处理程序！\n\n生效场景：终端 open 命令、系统文件对话框等。\n\n⚠️ 注意：从桌面双击文件夹仍会使用 Finder，这是 macOS 系统限制。")
                    : (true, $"{operationName}成功");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] SetHandler failed: {ex}");
        }

        return (false, "自动设置未能生效。请尝试手动设置：\n1. 在 Finder 中右键点击任意文件夹\n2. 按住 Option 键，选择「显示简介」\n3. 在「打开方式」中选择 MacExplorer\n4. 点击「全部更改」");
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
