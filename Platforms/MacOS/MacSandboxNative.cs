using System.Runtime.InteropServices;

namespace MacExplorer.Platforms.MacOS;

internal static class MacSandboxNative
{
    private const string Library = "libMacExplorerNativeDrag.dylib";
    internal static string UserHome => Take(me_user_home())!;
    internal static string TemporaryDirectory => Take(me_temporary_directory())!;
    internal static string ReadPlistValue(string path, string key) => Take(me_plist_value(path, key)) ?? "";
    internal static void OpenFile(string path, string? bundle = null)
    { if (!me_open_file(path, bundle)) throw new IOException("无法打开文件或找不到所选应用。"); }
    internal static void RevealFile(string path) => me_reveal_file(path);
    internal static void QuickLook(string path) => me_quicklook_show(path);
    internal static bool QuickLookVisible => me_quicklook_visible();
    internal static void CloseQuickLook() => me_quicklook_close();
    internal static string? ApplicationIcon(string path, int size) => Take(me_app_icon(path, size));
    internal static string? ApplicationPath(string bundle) => Take(me_app_path(bundle));
    internal static string? DefaultApplication(string path) => Take(me_default_app(path));
    internal static string RegisteredApplications(string path) => Take(me_registered_apps(path)) ?? "[]";
    internal static string Metadata(string path) => Take(me_metadata(path)) ?? "";
    internal static string OwnerGroup(string path) => Take(me_owner_group(path)) ?? "-- --";
    internal static string ExtendedAttributeNames(string path) => Take(me_xattr_names(path)) ?? "";
    internal static string StoragePath(bool cache) => Take(me_storage_path(cache))!;
    internal static string? PickTestDirectory(string path) => Take(me_pick_test_directory(path));
    internal static string RealPath(string path) => OperatingSystem.IsMacOS() ? Take(me_real_path(Path.GetFullPath(path))) ?? throw new IOException("无法解析文件路径。") : Path.GetFullPath(path);
    internal static string CreateBookmark(string path, bool explicitScope = true)
        => Take(me_bookmark_create(path, explicitScope)) ?? throw new UnauthorizedAccessException("无法保存目录授权，请从系统选择器重新选择。");
    internal static (IntPtr Scope, string? Path, string? Refreshed, bool Stale) OpenBookmark(string bookmark)
    {
        var scope = me_bookmark_open(bookmark, out var path, out var refreshed, out var stale);
        return (scope, Take(path), Take(refreshed), stale);
    }
    internal static void CloseBookmark(IntPtr scope) => me_bookmark_close(scope);
    internal static void Trash(string path)
    {
        var error = Take(me_trash(path));
        if (error != null) throw new IOException(error);
    }
    private static string? Take(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(pointer); }
        finally { me_string_free(pointer); }
    }
    [DllImport(Library)] private static extern IntPtr me_user_home();
    [DllImport(Library)] private static extern IntPtr me_temporary_directory();
    [DllImport(Library)] private static extern IntPtr me_plist_value([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string key);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool me_open_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string? bundle);
    [DllImport(Library)] private static extern void me_reveal_file([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern void me_quicklook_show([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool me_quicklook_visible();
    [DllImport(Library)] private static extern void me_quicklook_close();
    [DllImport(Library)] private static extern IntPtr me_app_icon([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int size);
    [DllImport(Library)] private static extern IntPtr me_app_path([MarshalAs(UnmanagedType.LPUTF8Str)] string bundle);
    [DllImport(Library)] private static extern IntPtr me_default_app([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_registered_apps([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_metadata([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_owner_group([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_xattr_names([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_pick_test_directory([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_storage_path([MarshalAs(UnmanagedType.I1)] bool cache);
    [DllImport(Library)] private static extern IntPtr me_real_path([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern IntPtr me_bookmark_create([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.I1)] bool explicitScope);
    [DllImport(Library)] private static extern IntPtr me_bookmark_open([MarshalAs(UnmanagedType.LPUTF8Str)] string bookmark, out IntPtr path, out IntPtr refreshed, [MarshalAs(UnmanagedType.I1)] out bool stale);
    [DllImport(Library)] private static extern void me_bookmark_close(IntPtr scope);
    [DllImport(Library)] private static extern IntPtr me_trash([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Library)] private static extern void me_string_free(IntPtr pointer);
}
