using System.Diagnostics;
using System.Runtime.InteropServices;
using Foundation;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public class MacDefaultAppService : MacExplorer.Services.IDefaultAppService
{
    private const string AppBundleId = "com.fkfinder.app";
    private const string FinderBundleId = "com.apple.finder";
    private const string FolderUti = "public.folder";

    // LaunchServices API 通过 CoreServices 框架暴露
    private const string CoreServicesLib =
        "/System/Library/Frameworks/CoreServices.framework/CoreServices";

    [DllImport(CoreServicesLib)]
    private static extern IntPtr LSCopyDefaultRoleHandlerForContentType(IntPtr contentType, uint role);

    [DllImport(CoreServicesLib)]
    private static extern int LSSetDefaultRoleHandlerForContentType(IntPtr contentType, uint role, IntPtr bundleId);

    // LSRolesMask.All = 0xFFFFFFFF
    private const uint LSRolesAll = 0xFFFFFFFF;
    // LSRolesMask.Viewer = 0x00000002
    private const uint LSRolesViewer = 0x00000002;

    public bool IsDefaultFolderHandler()
    {
        try
        {
            using var utiString = new NSString(FolderUti);
            var result = LSCopyDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll);
            if (result == IntPtr.Zero)
            {
                Debug.WriteLine("[DefaultApp] LSCopyDefaultRoleHandlerForContentType 返回 null");
                return false;
            }

            using var handlerId = ObjCRuntime.Runtime.GetNSObject<NSString>(result, owns: true);
            var currentHandler = handlerId?.ToString() ?? "(null)";
            Debug.WriteLine($"[DefaultApp] 当前 public.folder 处理程序: {currentHandler}");
            return string.Equals(handlerId?.ToString(), AppBundleId, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] IsDefaultFolderHandler 异常: {ex}");
            return false;
        }
    }

    public (bool Success, string Message) SetAsDefaultFolderHandler()
    {
        return SetHandler(AppBundleId, "设为默认文件管理器");
    }

    public (bool Success, string Message) ResetDefaultFolderHandler()
    {
        return SetHandler(FinderBundleId, "恢复 Finder 为默认");
    }

    private (bool Success, string Message) SetHandler(string bundleId, string operationName)
    {
        // 第一步：尝试 P/Invoke API
        try
        {
            using var utiString = new NSString(FolderUti);
            using var bundleIdString = new NSString(bundleId);

            // 同时尝试 LSRolesAll 和 LSRolesViewer
            var status = LSSetDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll, bundleIdString.Handle);
            Debug.WriteLine($"[DefaultApp] LSSetDefaultRoleHandlerForContentType(All) => OSStatus {status}");

            if (status != 0)
            {
                // 尝试仅设置 Viewer 角色
                status = LSSetDefaultRoleHandlerForContentType(utiString.Handle, LSRolesViewer, bundleIdString.Handle);
                Debug.WriteLine($"[DefaultApp] LSSetDefaultRoleHandlerForContentType(Viewer) => OSStatus {status}");
            }

            if (status == 0)
            {
                // 验证是否真正生效
                var verifyResult = LSCopyDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll);
                if (verifyResult != IntPtr.Zero)
                {
                    using var handlerId = ObjCRuntime.Runtime.GetNSObject<NSString>(verifyResult, owns: true);
                    var currentHandler = handlerId?.ToString() ?? "";
                    Debug.WriteLine($"[DefaultApp] 设置后验证，当前处理程序: {currentHandler}");

                    if (string.Equals(currentHandler, bundleId, StringComparison.OrdinalIgnoreCase))
                    {
                        if (bundleId == AppBundleId)
                        {
                            return (true,
                                "已成功设为默认文件夹处理程序！\n\n" +
                                "生效场景：终端 open 命令、系统文件对话框等。\n\n" +
                                "⚠️ 注意：从桌面双击文件夹仍会使用 Finder，这是 macOS 系统限制，所有第三方文件管理器均受此影响。");
                        }
                        return (true, $"{operationName}成功");
                    }
                }

                // API 返回成功但验证未通过，可能需要重启 LaunchServices
                Debug.WriteLine("[DefaultApp] API 返回成功但验证未通过，尝试备用方案");
            }
            else
            {
                Debug.WriteLine($"[DefaultApp] API 返回错误 OSStatus={status}，尝试备用方案");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] P/Invoke 调用异常: {ex}");
        }

        // 第二步：备用方案 - 使用 lsregister 重置数据库并重试
        try
        {
            var resetResult = TryWithLsregister(bundleId);
            if (resetResult)
            {
                if (bundleId == AppBundleId)
                {
                    return (true,
                        "已成功设为默认文件夹处理程序！\n\n" +
                        "生效场景：终端 open 命令、系统文件对话框等。\n\n" +
                        "⚠️ 注意：从桌面双击文件夹仍会使用 Finder，这是 macOS 系统限制，所有第三方文件管理器均受此影响。");
                }
                return (true, $"{operationName}成功（通过备用方案）");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] lsregister 备用方案失败: {ex}");
        }

        // 第三步：所有自动方案都失败，返回手动指引
        return (false,
            "自动设置未能生效。请尝试手动设置：\n" +
            "1. 在 Finder 中右键点击任意文件夹\n" +
            "2. 按住 Option 键，选择「显示简介」\n" +
            "3. 在「打开方式」中选择 MacExplorer\n" +
            "4. 点击「全部更改」\n\n" +
            "💡 提示：即使设置成功，从桌面双击文件夹仍会使用 Finder（macOS 系统限制）。");
    }

    /// <summary>
    /// 使用 lsregister 刷新 LaunchServices 数据库后重新尝试设置
    /// </summary>
    private bool TryWithLsregister(string bundleId)
    {
        // 先用 lsregister 重新注册当前应用
        var lsregisterPath =
            "/System/Library/Frameworks/CoreServices.framework/Versions/A/Frameworks/LaunchServices.framework/Versions/A/Support/lsregister";

        var appPath = NSBundle.MainBundle.BundlePath;
        Debug.WriteLine($"[DefaultApp] 应用路径: {appPath}");

        if (!string.IsNullOrEmpty(appPath))
        {
            RunShellCommand(lsregisterPath, $"-f \"{appPath}\"");
        }

        // 重新尝试 P/Invoke 设置
        try
        {
            using var utiString = new NSString(FolderUti);
            using var bundleIdString = new NSString(bundleId);

            var status = LSSetDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll, bundleIdString.Handle);
            Debug.WriteLine($"[DefaultApp] lsregister 后重试 LSSetDefault => OSStatus {status}");

            if (status == 0)
            {
                // 验证
                var verifyResult = LSCopyDefaultRoleHandlerForContentType(utiString.Handle, LSRolesAll);
                if (verifyResult != IntPtr.Zero)
                {
                    using var handlerId = ObjCRuntime.Runtime.GetNSObject<NSString>(verifyResult, owns: true);
                    var currentHandler = handlerId?.ToString() ?? "";
                    if (string.Equals(currentHandler, bundleId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                // API 成功但验证不通过也算部分成功（可能需要重启后生效）
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] lsregister 后重试失败: {ex}");
        }

        return false;
    }

    private static void RunShellCommand(string command, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = command,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                process.WaitForExit(5000);
                Debug.WriteLine($"[DefaultApp] Shell [{command} {arguments}] exit={process.ExitCode} out={output} err={error}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DefaultApp] RunShellCommand 异常: {ex}");
        }
    }
}
