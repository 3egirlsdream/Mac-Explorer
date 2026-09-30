using System.Runtime.InteropServices;
using System.Text;

namespace MacExplorer.Services.Impl;

internal sealed class RenameNameComparer(bool caseSensitive) : IEqualityComparer<string>
{
    private readonly StringComparer _strings = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
    public bool Equals(string? x, string? y) => _strings.Equals(x?.Normalize(NormalizationForm.FormC), y?.Normalize(NormalizationForm.FormC));
    public int GetHashCode(string obj) => _strings.GetHashCode(obj.Normalize(NormalizationForm.FormC));
    internal static RenameNameComparer ForDirectory(string path) => new(
        OperatingSystem.IsMacOS() ? PathConf(path, 11) == 1 : !OperatingSystem.IsWindows());
    [DllImport("libc", EntryPoint = "pathconf", SetLastError = true)]
    private static extern long PathConf([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int name);
}
