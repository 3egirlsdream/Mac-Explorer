using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

internal static class FileKindNames
{
    private readonly record struct FileKind(string NameKey, string IconKey);

    internal static string? GetFileIconKey(string extension, string name = "")
        => (GetPackageKind(extension) ?? GetFileKind(extension, name))?.IconKey;

    internal static string? GetPackageIconKey(string extension) => GetPackageKind(extension)?.IconKey;

    public static string Get(FileSystemEntry entry)
    {
        if (entry.IsApplication)
            return LocalizationText.Get("应用程序");

        var extension = (entry.Extension.Length == 0 ? Path.GetExtension(entry.Name) : entry.Extension).ToLowerInvariant();
        if (GetPackageKind(extension) is { } package)
            return LocalizationText.Get(package.NameKey);

        if (entry.IconKey == "app-bundle")
            return LocalizationText.Get("应用程序");
        if (entry.IsDirectory)
            return LocalizationText.Get("文件夹");

        if (GetFileKind(extension, entry.Name) is { } file)
            return LocalizationText.Get(file.NameKey);

        var suffix = extension.TrimStart('.');
        return suffix.Length == 0
            ? LocalizationText.Get("file_kind.generic")
            : LocalizationText.Get("file_kind.unknown_extension", suffix.ToUpperInvariant());
    }

    private static FileKind? GetPackageKind(string extension) => extension switch
    {
        ".band" or ".garageband" => new("file_kind.garageband", "file-audio-project"),
        ".logicx" or ".logic" => new("file_kind.logic", "file-audio-project"),
        ".fcpbundle" => new("file_kind.final_cut", "file-video-project"),
        ".fcpproject" => new("file_kind.final_cut_project", "file-video-project"),
        ".photoslibrary" => new("file_kind.photos", "file-image"),
        ".photobooth" => new("file_kind.photo_booth", "file-image"),
        ".musiclibrary" => new("file_kind.music_library", "file-audio"),
        ".tvlibrary" => new("file_kind.tv_library", "file-video"),
        ".pages" => new("file_kind.pages", "file-text"),
        ".numbers" => new("file_kind.numbers", "file-table"),
        ".key" => new("file_kind.keynote", "file-slides"),
        ".rtfd" => new("file_kind.rtfd", "file-text"),
        ".xcodeproj" => new("file_kind.xcode_project", "file-code"),
        ".xcworkspace" => new("file_kind.xcode_workspace", "file-code"),
        ".playground" or ".playgroundbook" => new("file_kind.playground", "file-code"),
        ".pkg" or ".mpkg" => new("file_kind.installer", "file-installer"),
        ".pvm" or ".vmwarevm" or ".vbox" => new("file_kind.virtual_machine", "file-vm"),
        ".bundle" => new("file_kind.bundle", "file-package"),
        ".framework" => new("file_kind.framework", "file-package"),
        ".plugin" or ".vst" or ".vst3" or ".component" => new("file_kind.plugin", "file-plugin"),
        ".appex" => new("file_kind.app_extension", "file-package"),
        ".kext" => new("file_kind.kernel_extension", "file-package"),
        _ => null
    };

    // Common format associations from FFmpeg, ExifTool, mime-db and GitHub Linguist.
    private static FileKind? GetFileKind(string extension, string name) => extension switch
    {
        // Audio and music
        ".caf" => new("file_kind.caf", "file-audio"),
        ".aif" or ".aiff" or ".aifc" => new("file_kind.aiff", "file-audio"),
        ".m4a" => new("file_kind.m4a", "file-audio"),
        ".m4b" => new("file_kind.m4b", "file-audio"),
        ".mp3" => new("file_kind.mp3", "file-audio"),
        ".wav" or ".wave" or ".bwf" => new("file_kind.wav", "file-audio"),
        ".w64" => new("file_kind.wave64", "file-audio"),
        ".flac" => new("file_kind.flac", "file-audio"),
        ".aac" or ".adts" => new("file_kind.aac", "file-audio"),
        ".alac" => new("file_kind.alac", "file-audio"),
        ".ogg" => new("file_kind.ogg", "file-audio"),
        ".oga" => new("file_kind.vorbis", "file-audio"),
        ".opus" => new("file_kind.opus", "file-audio"),
        ".wma" => new("file_kind.wma", "file-audio"),
        ".au" => new("file_kind.au", "file-audio"),
        ".amr" or ".awb" => new("file_kind.amr", "file-audio"),
        ".ac3" => new("file_kind.ac3", "file-audio"),
        ".eac3" or ".ec3" => new("file_kind.eac3", "file-audio"),
        ".dts" or ".dtshd" => new("file_kind.dts", "file-audio"),
        ".ape" => new("file_kind.ape", "file-audio"),
        ".wv" => new("file_kind.wavpack", "file-audio"),
        ".tak" => new("file_kind.tak", "file-audio"),
        ".dsf" or ".dff" => new("file_kind.dsd", "file-audio"),
        ".mid" or ".midi" or ".kar" => new("file_kind.midi", "file-audio-project"),
        ".sf2" or ".sf3" => new("file_kind.soundfont", "file-audio-project"),
        ".m3u" or ".m3u8" or ".pls" or ".xspf" => new("file_kind.playlist", "file-audio-project"),
        ".cue" => new("file_kind.cue", "file-audio"),
        ".mscz" or ".mscx" => new("file_kind.musicscore", "file-audio-project"),
        ".musicxml" or ".mxl" => new("file_kind.musicxml", "file-audio-project"),
        ".pkf" or ".pk" => new("file_kind.audition_peak", "file-waveform"),
        ".sesx" => new("file_kind.audition", "file-audio-project"),
        ".aup" or ".aup3" => new("file_kind.audacity", "file-audio-project"),
        ".als" => new("file_kind.ableton", "file-audio-project"),
        ".rpp" => new("file_kind.reaper", "file-audio-project"),
        ".reapeaks" => new("file_kind.reaper_peak", "file-waveform"),

        // Images and design
        ".jpg" or ".jpeg" or ".jpe" => new("file_kind.jpeg", "file-image"),
        ".jp2" or ".j2k" or ".jpf" or ".jpx" => new("file_kind.jpeg2000", "file-image"),
        ".jxl" => new("file_kind.jpegxl", "file-image"),
        ".png" => new("file_kind.png", "file-image"),
        ".apng" => new("file_kind.apng", "file-image"),
        ".gif" => new("file_kind.gif", "file-image"),
        ".tif" or ".tiff" => new("file_kind.tiff", "file-image"),
        ".bmp" or ".dib" => new("file_kind.bmp", "file-image"),
        ".webp" => new("file_kind.webp", "file-image"),
        ".heic" or ".heics" => new("file_kind.heic", "file-image"),
        ".heif" or ".heifs" => new("file_kind.heif", "file-image"),
        ".avif" or ".avifs" => new("file_kind.avif", "file-image"),
        ".svg" or ".svgz" => new("file_kind.svg", "file-image"),
        ".ico" => new("file_kind.ico", "file-image"),
        ".icns" => new("file_kind.icns", "file-image"),
        ".exr" => new("file_kind.openexr", "file-image"),
        ".hdr" => new("file_kind.hdr", "file-image"),
        ".pbm" or ".pgm" or ".ppm" or ".pnm" or ".pam" => new("file_kind.netpbm", "file-image"),
        ".dng" => new("file_kind.dng", "file-image"),
        ".cr2" or ".cr3" or ".crw" or ".nef" or ".nrw" or ".arw" or ".srf" or ".sr2" or ".raf" or ".orf" or ".rw2" or ".rwl" or ".pef" or ".x3f" or ".gpr" or ".3fr" or ".iiq" => new("file_kind.camera_raw", "file-image"),
        ".xmp" => new("file_kind.xmp", "file-metadata"),
        ".icc" or ".icm" => new("file_kind.icc", "file-metadata"),
        ".psd" or ".psb" => new("file_kind.photoshop", "file-design"),
        ".ai" => new("file_kind.illustrator", "file-design"),
        ".indd" or ".idml" => new("file_kind.indesign", "file-design"),
        ".sketch" => new("file_kind.sketch", "file-design"),
        ".fig" => new("file_kind.figma", "file-design"),
        ".xcf" => new("file_kind.xcf", "file-design"),
        ".kra" or ".krz" => new("file_kind.krita", "file-design"),
        ".ora" => new("file_kind.ora", "file-design"),

        // Video and subtitles
        ".mp4" or ".m4v" => new("file_kind.mp4", "file-video"),
        ".mov" or ".qt" => new("file_kind.quicktime", "file-video"),
        ".mkv" => new("file_kind.mkv", "file-video"),
        ".webm" => new("file_kind.webm", "file-video"),
        ".avi" => new("file_kind.avi", "file-video"),
        ".wmv" => new("file_kind.wmv", "file-video"),
        ".flv" or ".f4v" => new("file_kind.flv", "file-video"),
        ".mpg" or ".mpeg" or ".mpe" or ".m1v" or ".m2v" => new("file_kind.mpeg", "file-video"),
        ".m2ts" or ".m2t" => new("file_kind.transport_stream", "file-video"),
        ".mxf" => new("file_kind.mxf", "file-video"),
        ".ogv" => new("file_kind.ogv", "file-video"),
        ".3gp" or ".3g2" => new("file_kind.3gp", "file-video"),
        ".srt" or ".ass" or ".ssa" or ".vtt" or ".smi" => new("file_kind.subtitle", "file-subtitle"),
        ".prproj" => new("file_kind.premiere", "file-video-project"),
        ".aep" or ".aepx" => new("file_kind.after_effects", "file-video-project"),
        ".drp" => new("file_kind.resolve", "file-video-project"),
        ".fcpxml" => new("file_kind.fcpxml", "file-video-project"),

        // Documents and data
        ".txt" or ".text" => new("file_kind.text", "file-text"),
        ".rtf" => new("file_kind.rtf", "file-text"),
        ".pdf" => new("file_kind.pdf", "file-pdf"),
        ".doc" or ".docx" or ".docm" or ".dot" or ".dotx" or ".dotm" => new("file_kind.word", "file-word"),
        ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".xlt" or ".xltx" or ".xltm" => new("file_kind.excel", "file-excel"),
        ".ppt" or ".pptx" or ".pptm" or ".pps" or ".ppsx" or ".pot" or ".potx" => new("file_kind.powerpoint", "file-powerpoint"),
        ".odt" or ".ott" => new("file_kind.odt", "file-text"),
        ".ods" or ".ots" => new("file_kind.ods", "file-table"),
        ".odp" or ".otp" => new("file_kind.odp", "file-slides"),
        ".odg" or ".otg" => new("file_kind.odg", "file-design"),
        ".csv" => new("file_kind.csv", "file-table"),
        ".tsv" => new("file_kind.tsv", "file-table"),
        ".epub" => new("file_kind.epub", "file-ebook"),
        ".azw" or ".azw3" or ".kfx" => new("file_kind.kindle", "file-ebook"),
        ".mobi" => new("file_kind.mobi", "file-ebook"),
        ".djvu" or ".djv" => new("file_kind.djvu", "file-ebook"),
        ".cbz" or ".cbr" or ".cb7" => new("file_kind.comic", "file-ebook"),
        ".ps" or ".eps" or ".epsf" => new("file_kind.postscript", "file-text"),
        ".eml" or ".emlx" => new("file_kind.mail", "file-mail"),
        ".ics" or ".ical" => new("file_kind.ical", "file-calendar"),
        ".vcf" or ".vcard" => new("file_kind.vcard", "file-contact"),
        ".sqlite" or ".sqlite3" => new("file_kind.sqlite", "file-database"),
        ".parquet" => new("file_kind.parquet", "file-database"),
        ".geojson" => new("file_kind.geojson", "file-map"),
        ".gpkg" => new("file_kind.geopackage", "file-map"),
        ".gpx" => new("file_kind.gpx", "file-map"),
        ".kml" or ".kmz" => new("file_kind.kml", "file-map"),

        // Archives and installers
        ".zip" or ".zipx" => new("file_kind.zip", "file-archive"),
        ".rar" => new("file_kind.rar", "file-archive"),
        ".7z" => new("file_kind.7z", "file-archive"),
        ".tar" => new("file_kind.tar", "file-archive"),
        ".gz" when name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) => new("file_kind.tar_gzip", "file-archive"),
        ".tgz" => new("file_kind.tar_gzip", "file-archive"),
        ".bz2" when name.EndsWith(".tar.bz2", StringComparison.OrdinalIgnoreCase) => new("file_kind.tar_bzip2", "file-archive"),
        ".tbz" or ".tbz2" => new("file_kind.tar_bzip2", "file-archive"),
        ".xz" when name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) => new("file_kind.tar_xz", "file-archive"),
        ".txz" => new("file_kind.tar_xz", "file-archive"),
        ".zst" when name.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase) => new("file_kind.tar_zstd", "file-archive"),
        ".tzst" => new("file_kind.tar_zstd", "file-archive"),
        ".gz" => new("file_kind.gzip", "file-archive"),
        ".bz2" => new("file_kind.bzip2", "file-archive"),
        ".xz" => new("file_kind.xz", "file-archive"),
        ".zst" => new("file_kind.zstd", "file-archive"),
        ".br" => new("file_kind.brotli", "file-archive"),
        ".dmg" or ".sparseimage" => new("file_kind.dmg", "file-installer"),
        ".iso" => new("file_kind.iso", "file-disk-image"),
        ".apk" => new("file_kind.apk", "file-android-package"),
        ".ipa" => new("file_kind.ipa", "file-installer"),
        ".msi" => new("file_kind.msi", "file-installer"),
        ".exe" => new("file_kind.exe", "file-installer"),
        ".deb" => new("file_kind.deb", "file-installer"),
        ".rpm" => new("file_kind.rpm", "file-installer"),

        // Development and 3D
        ".md" or ".markdown" or ".mdown" or ".mkd" or ".mdx" => new("file_kind.markdown", "file-markdown"),
        ".html" or ".htm" or ".xhtml" => new("file_kind.html", "file-web"),
        ".css" => new("file_kind.css", "file-code"),
        ".sass" or ".scss" => new("file_kind.sass", "file-code"),
        ".json" or ".jsonc" or ".json5" or ".ndjson" or ".jsonl" => new("file_kind.json", "file-config"),
        ".xml" or ".xsd" or ".xsl" or ".xslt" => new("file_kind.xml", "file-config"),
        ".yaml" or ".yml" => new("file_kind.yaml", "file-config"),
        ".toml" => new("file_kind.toml", "file-config"),
        ".ini" or ".cfg" or ".conf" or ".env" or ".editorconfig" => new("file_kind.config", "file-config"),
        ".plist" => new("file_kind.plist", "file-config"),
        ".ipynb" => new("file_kind.ipynb", "file-code"),
        ".sql" => new("file_kind.sql", "file-code"),
        ".py" or ".pyw" or ".pyi" => new("file_kind.python", "file-code"),
        ".js" or ".jsx" or ".mjs" or ".cjs" => new("file_kind.javascript", "file-code"),
        ".ts" or ".tsx" or ".cts" => new("file_kind.typescript", "file-code"),
        ".vue" => new("file_kind.vue", "file-code"),
        ".svelte" => new("file_kind.svelte", "file-code"),
        ".swift" => new("file_kind.swift", "file-code"),
        ".c" => new("file_kind.c", "file-code"),
        ".h" or ".hh" or ".hpp" or ".hxx" => new("file_kind.header", "file-code"),
        ".cc" or ".cpp" or ".cxx" => new("file_kind.cpp", "file-code"),
        ".cs" => new("file_kind.csharp", "file-code"),
        ".java" => new("file_kind.java", "file-code"),
        ".kt" or ".kts" => new("file_kind.kotlin", "file-code"),
        ".go" => new("file_kind.go", "file-code"),
        ".rs" => new("file_kind.rust", "file-code"),
        ".rb" => new("file_kind.ruby", "file-code"),
        ".php" => new("file_kind.php", "file-code"),
        ".sh" or ".bash" or ".zsh" or ".fish" or ".command" => new("file_kind.shell", "file-code"),
        ".ps1" or ".psm1" or ".psd1" => new("file_kind.powershell", "file-code"),
        ".bat" or ".cmd" => new("file_kind.batch", "file-code"),
        ".diff" or ".patch" => new("file_kind.diff", "file-code"),
        ".wasm" => new("file_kind.wasm", "file-code"),
        ".ttf" or ".otf" or ".ttc" or ".otc" => new("file_kind.font", "file-font"),
        ".woff" or ".woff2" => new("file_kind.webfont", "file-font"),
        ".blend" => new("file_kind.blender", "file-3d"),
        ".gltf" or ".glb" => new("file_kind.gltf", "file-3d"),
        ".usd" or ".usda" or ".usdc" or ".usdz" => new("file_kind.usd", "file-3d"),
        ".fbx" => new("file_kind.fbx", "file-3d"),
        ".stl" => new("file_kind.stl", "file-3d"),
        ".3mf" => new("file_kind.3mf", "file-3d"),
        ".step" or ".stp" => new("file_kind.step", "file-3d"),
        ".dwg" or ".dxf" => new("file_kind.autocad", "file-3d"),

        ".gitignore" => new("file_kind.gitignore", "file-metadata"),
        ".gitattributes" => new("file_kind.gitattributes", "file-metadata"),
        "" => name.ToLowerInvariant() switch
        {
            "dockerfile" or "containerfile" => new("file_kind.dockerfile", "file-code"),
            "makefile" or "gnumakefile" => new("file_kind.makefile", "file-code"),
            _ => null
        },
        _ => null
    };
}
