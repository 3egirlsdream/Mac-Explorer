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
    public static string ResolveIconKey(string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return "file";

        return extension.ToLowerInvariant() switch
        {
            // App bundle
            ".app" => "app-bundle",
            // Archive / compressed
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".bz2" or ".xz" or ".tgz" or ".zst" => "file-archive",
            // Image (including RAW formats)
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".tiff" or ".tif" or ".svg" or ".webp" or ".heic" or ".ico"
                or ".raw" or ".dng" or ".cr2" or ".cr3" or ".nef" or ".arw" or ".orf" or ".rw2" or ".pef" => "file-image",
            // Video
            ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" or ".flv" or ".webm" => "file-video",
            // Audio
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".wma" => "file-audio",
            // PDF
            ".pdf" => "file-pdf",
            // Microsoft Word style
            ".doc" or ".docx" or ".odt" or ".pages" => "file-word",
            // Microsoft Excel style
            ".xls" or ".xlsx" or ".csv" or ".numbers" => "file-excel",
            // Microsoft PowerPoint style
            ".ppt" or ".pptx" or ".key" => "file-powerpoint",
            // Markdown
            ".md" or ".mdx" or ".markdown" => "file-markdown",
            // Plain text files
            ".txt" or ".rtf" or ".log" or ".nfo" or ".ini" or ".cfg" or ".conf" or ".env" => "file-text",
            // Config / data files
            ".json" or ".yaml" or ".yml" or ".toml" or ".plist" or ".xml" => "file-config",
            // Web pages
            ".html" or ".htm" => "file-web",
            // Code (programming languages)
            ".cs" or ".js" or ".ts" or ".tsx" or ".jsx" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".hpp"
                or ".go" or ".rs" or ".swift" or ".kt" or ".rb" or ".php" or ".lua" or ".r" or ".m" or ".mm"
                or ".scala" or ".clj" or ".ex" or ".erl" or ".hs" or ".dart" or ".v" or ".zig"
                or ".css" or ".scss" or ".sass" or ".less" or ".vue" or ".svelte"
                or ".sh" or ".bash" or ".zsh" or ".fish" or ".bat" or ".cmd" or ".ps1"
                or ".sql" or ".graphql" or ".gql" => "file-code",
            // Certificate files
            ".cer" or ".crt" or ".pem" or ".p12" or ".pfx" or ".der" or ".cert" or ".ca-bundle" => "file-certificate",
            // System installers / packages
            ".pkg" or ".dmg" or ".msi" or ".exe" or ".deb" or ".rpm" or ".appimage" or ".snap" => "file-installer",
            // Font files
            ".ttf" or ".otf" or ".woff" or ".woff2" or ".eot" or ".fon" or ".ttc" => "file-font",
            // Database files
            ".db" or ".sqlite" or ".sqlite3" or ".mdb" or ".accdb" or ".dbf" or ".realm" => "file-database",
            // eBook files
            ".epub" or ".mobi" or ".azw" or ".azw3" or ".fb2" or ".djvu" or ".cbz" or ".cbr" => "file-ebook",
            // Design / vector files
            ".psd" or ".psb" or ".ai" or ".eps" or ".sketch" or ".fig" or ".xd" or ".indd" or ".afdesign" or ".afphoto" => "file-design",
            // Disk image / ISO
            ".iso" or ".img" or ".vhd" or ".vhdx" or ".vmdk" or ".qcow2" => "file-disk-image",
            // 3D model files
            ".obj" or ".fbx" or ".stl" or ".blend" or ".3ds" or ".dae" or ".gltf" or ".glb" or ".usdz" or ".step" or ".stp" => "file-3d",
            // Subtitle files
            ".srt" or ".ass" or ".ssa" or ".sub" or ".vtt" or ".idx" or ".lrc" => "file-subtitle",
            // Visual Studio / C# project files
            ".sln" => "file-vs-solution",
            ".csproj" or ".vbproj" or ".fsproj" => "file-vs-project",
            ".razor" => "file-razor",
            // Virtual machine files
            ".pvm" or ".pvs" or ".hdd" or ".vdi" or ".vmx" or ".vmwarevm" or ".ova" or ".ovf" or ".vbox" => "file-vm",
            _ => "file-generic"
        };
    }
}