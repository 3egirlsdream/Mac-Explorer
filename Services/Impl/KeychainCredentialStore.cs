using System.Runtime.InteropServices;

namespace MacExplorer.Services.Impl;

internal interface ICredentialStore
{
    string? Read(string account);
    void Save(string account, string secret);
    void Delete(string account);
}

internal sealed class KeychainCredentialStore(string service) : ICredentialStore
{
    // Tests never query or mutate the user's Keychain.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> TestSecrets = new();
    private string TestAccount(string account) => RuntimePaths.TestRoot + ":" + service + ":" + account;
    public string? Read(string account)
    {
        if (RuntimePaths.TestRoot != null) return TestSecrets.GetValueOrDefault(TestAccount(account));
        var result = me_secret_read(service, account, out var status);
        if (status == -25300) return null;
        Check(status);
        try { return Marshal.PtrToStringUTF8(result); }
        finally { if (result != IntPtr.Zero) me_string_free(result); }
    }
    public void Save(string account, string secret)
    {
        if (RuntimePaths.TestRoot != null) { TestSecrets[TestAccount(account)] = secret; return; }
        Check(me_secret_save(service, account, secret));
    }
    public void Delete(string account)
    {
        if (RuntimePaths.TestRoot != null) { TestSecrets.TryRemove(TestAccount(account), out _); return; }
        var status = me_secret_delete(service, account);
        if (status != -25300) Check(status);
    }
    private static void Check(int status)
    {
        if (status == 0) return;
        var reason = status switch
        {
            -25308 => "钥匙串已锁定或当前不允许交互，请解锁后重试",
            -25293 => "钥匙串访问被拒绝，请允许此应用访问后重试",
            -128 => "钥匙串访问已取消，可重新执行操作并允许访问",
            -25291 => "钥匙串服务暂不可用，请稍后重试",
            _ => "钥匙串访问失败，请检查钥匙串状态后重试"
        };
        throw new InvalidOperationException($"{reason}（Keychain {status}）。");
    }
    private const string Library = "libMacExplorerNativeDrag.dylib";
    [DllImport(Library)] private static extern IntPtr me_secret_read([MarshalAs(UnmanagedType.LPUTF8Str)] string service, [MarshalAs(UnmanagedType.LPUTF8Str)] string account, out int status);
    [DllImport(Library)] private static extern int me_secret_save([MarshalAs(UnmanagedType.LPUTF8Str)] string service, [MarshalAs(UnmanagedType.LPUTF8Str)] string account, [MarshalAs(UnmanagedType.LPUTF8Str)] string secret);
    [DllImport(Library)] private static extern int me_secret_delete([MarshalAs(UnmanagedType.LPUTF8Str)] string service, [MarshalAs(UnmanagedType.LPUTF8Str)] string account);
    [DllImport(Library)] private static extern void me_string_free(IntPtr value);
}
