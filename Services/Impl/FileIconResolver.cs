namespace MacExplorer.Services.Impl;

/// <summary>
/// Shared icon key resolution for file extensions.
/// Centralizes the mapping of file extensions to icon keys used throughout the app.
/// </summary>
public static class FileIconResolver
{
    /// <summary>
    /// Resolves the icon key for a given file extension.
    /// Returns the icon key string used for Theming/UI display.
    /// </summary>
    public static string ResolveIconKey(string? extension) => ResolveIconKey(extension, "");

    internal static string ResolveIconKey(string? extension, string name)
    {
        var normalized = extension?.ToLowerInvariant() ?? "";
        return FileKindNames.GetFileIconKey(normalized, name) ?? (normalized switch
        {
            ".app" => "app-bundle",
            ".raw" or ".bay" or ".dcr" or ".erf" or ".fff" or ".k25" or ".kdc" or ".mef" or ".mos" or ".mrw"
                or ".srw" or ".tga" or ".dds" => "file-image",
            ".mts" or ".mpv" or ".dv" or ".asf" or ".rm" or ".rmvb" or ".vob" => "file-video",
            ".log" or ".nfo" => "file-text",
            ".lua" or ".r" or ".m" or ".mm" or ".scala" or ".clj" or ".ex" or ".erl" or ".hs" or ".dart"
                or ".v" or ".zig" or ".less" or ".graphql" or ".gql" => "file-code",
            ".cer" or ".crt" or ".pem" or ".p12" or ".pfx" or ".der" or ".cert" or ".ca-bundle" => "file-certificate",
            ".appimage" or ".snap" => "file-installer",
            ".eot" or ".fon" => "file-font",
            ".db" or ".mdb" or ".accdb" or ".dbf" or ".realm" => "file-database",
            ".fb2" or ".ibooks" => "file-ebook",
            ".xd" or ".afdesign" or ".afphoto" => "file-design",
            ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow2" => "file-disk-image",
            ".obj" or ".3ds" or ".dae" => "file-3d",
            ".sub" or ".idx" or ".lrc" => "file-subtitle",
            ".sln" => "file-vs-solution",
            ".csproj" or ".vbproj" or ".fsproj" => "file-vs-project",
            ".razor" => "file-razor",
            ".pvs" or ".hdd" or ".vdi" or ".vmx" or ".ova" or ".ovf" => "file-vm",
            "" => "file",
            _ => "file-generic"
        });
    }
}
