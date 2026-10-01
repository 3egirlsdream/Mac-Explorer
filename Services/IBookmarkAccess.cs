using MacExplorer.Platforms.MacOS;

namespace MacExplorer.Services;

internal interface IBookmarkAccess
{
    string Create(string path, bool explicitScope);
    (IntPtr Scope, string? Path, string? Refreshed, bool Stale) Open(string bookmark);
    void Close(IntPtr scope);
    string RealPath(string path);
}

internal sealed class NativeBookmarkAccess : IBookmarkAccess
{
    public string Create(string path, bool explicitScope) => MacSandboxNative.CreateBookmark(path, explicitScope);
    public (IntPtr Scope, string? Path, string? Refreshed, bool Stale) Open(string bookmark) => MacSandboxNative.OpenBookmark(bookmark);
    public void Close(IntPtr scope) => MacSandboxNative.CloseBookmark(scope);
    public string RealPath(string path) => MacSandboxNative.RealPath(path);
}
