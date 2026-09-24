using System.Runtime.InteropServices;
using System.Text;
using MacExplorer.Services;

namespace MacExplorer.Copilot;

public sealed class CopilotSettings(ISettingsService settings)
{
    public const string EndpointKey = "copilot.endpoint";
    public const string ModelKey = "copilot.model";
    public string Endpoint
    {
        get => settings.Get(EndpointKey) ?? "https://api.openai.com/v1";
        set => settings.Set(EndpointKey, value.Trim());
    }
    public string Model
    {
        get => settings.Get(ModelKey) ?? "gpt-4.1-mini";
        set => settings.Set(ModelKey, value.Trim());
    }
}

/// <summary>Stores only the API key in the user's macOS login Keychain.</summary>
public sealed class CopilotKeychain
{
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("com.macexplorer.copilot");
    private static readonly byte[] Account = Encoding.UTF8.GetBytes("api-key");
    private string? _testKey;

    public string? Read()
    {
        if (RuntimePaths.TestRoot != null)
            return _testKey ?? Environment.GetEnvironmentVariable("MACEXPLORER_COPILOT_TEST_KEY");
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Copilot 凭据需要 macOS Keychain。");
        var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)Account.Length, Account, out var length, out var data, out var item);
        if (status == -25300) return null;
        if (status != 0) throw new InvalidOperationException($"读取 Keychain 失败：{status}");
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) CFRelease(item);
        }
    }

    public void Save(string key)
    {
        if (RuntimePaths.TestRoot != null) { _testKey = key; return; }
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Copilot 凭据需要 macOS Keychain。");
        var bytes = Encoding.UTF8.GetBytes(key);
        var found = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
            (uint)Account.Length, Account, out _, out var data, out var item);
        if (found == 0)
        {
            SecKeychainItemFreeContent(IntPtr.Zero, data);
            try
            {
                var status = SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)bytes.Length, bytes);
                if (status != 0) throw new InvalidOperationException($"更新 Keychain 失败：{status}");
            }
            finally { CFRelease(item); }
        }
        else if (found == -25300)
        {
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service,
                (uint)Account.Length, Account, (uint)bytes.Length, bytes, out item);
            if (item != IntPtr.Zero) CFRelease(item);
            if (status != 0) throw new InvalidOperationException($"保存 Keychain 失败：{status}");
        }
        else throw new InvalidOperationException($"访问 Keychain 失败：{found}");
    }

    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
        uint accountLength, byte[] account, uint passwordLength, byte[] password, out IntPtr itemRef);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attributes,
        uint length, byte[] data);
    [DllImport("/System/Library/Frameworks/Security.framework/Security")]
    private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr item);
}
