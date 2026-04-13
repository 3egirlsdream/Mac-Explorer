using System.Runtime.InteropServices;
using Foundation;

namespace FKFinder.Platforms.MacCatalyst.Services;

public class MacDefaultAppService : FKFinder.Services.IDefaultAppService
{
    private const string AppBundleId = "com.fkfinder.app";
    private const string FinderBundleId = "com.apple.finder";
    private const string FolderUti = "public.folder";

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    [DllImport("/System/Library/Frameworks/CoreServices.framework/CoreServices")]
    private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr bundleId);

    // LSRolesMask.All = 0xFFFFFFFF
    private const uint LSRolesAll = 0xFFFFFFFF;

    public bool IsDefaultFolderHandler()
    {
        try
        {
            using var utiString = new NSString(FolderUti);
            var result = LSCopyDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll);
            if (result == IntPtr.Zero)
                return false;

            using var handlerId = ObjCRuntime.Runtime.GetNSObject<NSString>(result, owns: true);
            return string.Equals(handlerId?.ToString(), AppBundleId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public void SetAsDefaultFolderHandler()
    {
        try
        {
            using var utiString = new NSString(FolderUti);
            using var bundleIdString = new NSString(AppBundleId);
            LSSetDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll, bundleIdString.Handle);
        }
        catch
        {
            // Silently fail if setting default handler fails
        }
    }

    public void ResetDefaultFolderHandler()
    {
        try
        {
            using var utiString = new NSString(FolderUti);
            using var bundleIdString = new NSString(FinderBundleId);
            LSSetDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll, bundleIdString.Handle);
        }
        catch
        {
            // Silently fail if resetting default handler fails
        }
    }
}
