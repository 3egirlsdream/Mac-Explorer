namespace FKFinder.Models;

using System.Net;

/// <summary>
/// Renders rich SVG file type icons with extension labels.
/// Unified modern design system. ViewBox: 0 0 32 32.
/// All icons fill a consistent 28×28 visual area (2,2 → 30,30).
/// </summary>
public static class FileIconRenderer
{
    public static string Render(string iconKey, string extension, int size)
    {
        var ext = extension?.TrimStart('.').ToUpperInvariant() ?? "";
        var inner = iconKey switch
        {
            "file-text" => TextFile(ext),
            "file-markdown" => MarkdownFile(ext.Length > 0 ? ext : "MD"),
            "file-word" or "file-document" => WordIcon(),
            "file-excel" or "file-spreadsheet" => ExcelIcon(),
            "file-powerpoint" or "file-presentation" => PowerPointIcon(),
            "file-pdf" => PdfFile(),
            "file-archive" or "file-package" => ArchiveFile(ext),
            "file-certificate" => CertificateFile(),
            "file-installer" or "file-disk-image" => InstallerFile(ext),
            "file-image" => ImageFile(ext),
            "file-web" => WebFile(ext),
            "file-code" => CodeFile(ext),
            "file-config" => ConfigFile(ext),
            "file-video" => VideoFile(),
            "file-audio" => AudioFile(),
            "file-font" => FontFile(ext),
            "file-database" => DatabaseFile(ext),
            "file-ebook" => EbookFile(ext),
            "file-design" => DesignFile(ext),
            "file-3d" => ThreeDFile(ext),
            "file-subtitle" => SubtitleFile(ext),
            "file-executable" => ExecutableFile(ext),
            _ => GenericFile()
        };
        return $@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{inner}</svg>";
    }

    static string E(string s) => WebUtility.HtmlEncode(s);

    // ── Font sizing helpers ──
    static double BFs(string t) => t.Length switch { <= 2 => 6.5, 3 => 5.8, 4 => 5, _ => 4.2 };
    static double LFs(string t) => t.Length switch { <= 1 => 11, 2 => 9.5, 3 => 8, 4 => 6.8, _ => 5.5 };

    // ── Shared shapes ── all fill 2,2 → 30,30 (28×28) ──

    // Card container: 28×28 rounded rect
    static string Card(string fill, string stroke) =>
        $@"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""4.5"" fill=""{fill}"" stroke=""{stroke}"" stroke-width=""0.9""/>";

    // Document page: 28×28 white page with folded corner
    static string Page(string accent = "#C1CAD6") =>
        $@"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""4"" fill=""#fff"" stroke=""{accent}"" stroke-width=""0.9""/>" +
        $@"<path d=""M21 2v5.5c0 .55.45 1 1 1h5.5"" fill=""#F1F5F9"" stroke=""{accent}"" stroke-width=""0.8"" fill-rule=""evenodd""/>";

    // Extension tab at bottom-right
    static string Tab(string text, string bg, double ty = 23)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fs = BFs(text);
        var w = Math.Max(12, 4 + text.Length * 3.8);
        return $@"<rect x=""{28 - w:F1}"" y=""{ty:F1}"" width=""{w:F1}"" height=""7"" rx=""2"" fill=""{bg}""/>" +
               $@"<text x=""{28 - w / 2:F1}"" y=""{ty + 3.6:F1}"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Text','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""{fs:F1}"" font-weight=""700"" fill=""#fff"" letter-spacing=""0.3"">{E(text)}</text>";
    }

    // Large centered text on page body
    static string BigLabel(string text, string color, double cy = 18)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fs = LFs(text);
        return $@"<text x=""16"" y=""{cy:F1}"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Display','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""{fs:F1}"" font-weight=""800"" fill=""{color}"" opacity=""0.85"" letter-spacing=""0.3"">{E(text)}</text>";
    }

    // ── Icon types ──

    // ━━ Generic fallback ━━
    static string GenericFile() =>
        Page() +
        @"<line x1=""8"" y1=""14"" x2=""22"" y2=""14"" stroke=""#D1D5DB"" stroke-width=""0.8"" stroke-linecap=""round""/>" +
        @"<line x1=""8"" y1=""17.5"" x2=""19"" y2=""17.5"" stroke=""#D1D5DB"" stroke-width=""0.8"" stroke-linecap=""round""/>" +
        @"<line x1=""8"" y1=""21"" x2=""16"" y2=""21"" stroke=""#D1D5DB"" stroke-width=""0.8"" stroke-linecap=""round""/>";

    // ━━ Text files (txt, ini, cfg, log, etc.) ━━
    static string TextFile(string ext) =>
        Page("#94A3B8") +
        @"<line x1=""8"" y1=""11"" x2=""20"" y2=""11"" stroke=""#CBD5E1"" stroke-width=""0.8"" stroke-linecap=""round""/>" +
        @"<line x1=""8"" y1=""14"" x2=""17"" y2=""14"" stroke=""#CBD5E1"" stroke-width=""0.8"" stroke-linecap=""round""/>" +
        BigLabel(ext, "#475569", 22) +
        Tab(ext, "#64748B");

    // ━━ Markdown ━━
    static string MarkdownFile(string ext) =>
        Page("#818CF8") +
        @"<path d=""M8 12v7l2.8-2.8L13.5 19v-7"" fill=""none"" stroke=""#6366F1"" stroke-width=""1.3"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<path d=""M20 18l2.2-3.5L24.4 18"" fill=""none"" stroke=""#6366F1"" stroke-width=""1.3"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        BigLabel(ext, "#4338CA", 25);

    // ━━ Microsoft Word ━━
    static string WordIcon() =>
        // Right side: 28×28 document page with text lines
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""3"" fill=""#E8EFF9"" stroke=""#2B579A"" stroke-width=""0.5"" opacity=""0.7""/>" +
        @"<line x1=""14"" y1=""8"" x2=""27"" y2=""8"" stroke=""#B4C7E0"" stroke-width=""0.9"" stroke-linecap=""round""/>" +
        @"<line x1=""14"" y1=""12"" x2=""27"" y2=""12"" stroke=""#B4C7E0"" stroke-width=""0.9"" stroke-linecap=""round""/>" +
        @"<line x1=""14"" y1=""16"" x2=""27"" y2=""16"" stroke=""#B4C7E0"" stroke-width=""0.9"" stroke-linecap=""round""/>" +
        @"<line x1=""14"" y1=""20"" x2=""24"" y2=""20"" stroke=""#B4C7E0"" stroke-width=""0.9"" stroke-linecap=""round""/>" +
        @"<line x1=""14"" y1=""24"" x2=""21"" y2=""24"" stroke=""#B4C7E0"" stroke-width=""0.9"" stroke-linecap=""round""/>" +
        // Left panel: Word brand blue
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""3"" fill=""#2B579A""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""3"" fill=""#3568B5"" opacity=""0.5""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Display','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""14"" font-weight=""700"" fill=""#fff"">W</text>";

    // ━━ Microsoft Excel ━━
    static string ExcelIcon() =>
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""3"" fill=""#E8F5ED"" stroke=""#217346"" stroke-width=""0.5"" opacity=""0.7""/>" +
        @"<rect x=""13"" y=""7"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        @"<rect x=""20"" y=""7"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        @"<rect x=""13"" y=""12"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        @"<rect x=""20"" y=""12"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        @"<rect x=""13"" y=""17"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        @"<rect x=""20"" y=""17"" width=""7"" height=""5"" fill=""none"" stroke=""#A9D5BB"" stroke-width=""0.6""/>" +
        // Left panel: Excel brand green
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""3"" fill=""#217346""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""3"" fill=""#2D9158"" opacity=""0.5""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Display','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""14"" font-weight=""700"" fill=""#fff"">X</text>";

    // ━━ Microsoft PowerPoint ━━
    static string PowerPointIcon() =>
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""3"" fill=""#FDF0EC"" stroke=""#B7472A"" stroke-width=""0.5"" opacity=""0.7""/>" +
        @"<rect x=""14"" y=""7"" width=""12"" height=""7.5"" rx=""1.2"" fill=""none"" stroke=""#E0A999"" stroke-width=""0.7""/>" +
        @"<rect x=""14"" y=""17"" width=""12"" height=""7.5"" rx=""1.2"" fill=""none"" stroke=""#E0A999"" stroke-width=""0.7""/>" +
        // Left panel: PowerPoint brand red-orange
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""3"" fill=""#B7472A""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""3"" fill=""#D4563A"" opacity=""0.5""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Display','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""14"" font-weight=""700"" fill=""#fff"">P</text>";

    // ━━ PDF ━━
    static string PdfFile() =>
        Page("#E53E3E") +
        @"<rect x=""4"" y=""15"" width=""24"" height=""10"" rx=""3"" fill=""#DC2626""/>" +
        @"<text x=""16"" y=""20.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'SF Pro Display','SF Pro',-apple-system,'Helvetica Neue',sans-serif"" font-size=""7.5"" font-weight=""800"" fill=""#fff"" letter-spacing=""1"">PDF</text>" +
        @"<line x1=""8"" y1=""7"" x2=""20"" y2=""7"" stroke=""#FECACA"" stroke-width=""0.8"" stroke-linecap=""round""/>" +
        @"<line x1=""8"" y1=""10"" x2=""17"" y2=""10"" stroke=""#FECACA"" stroke-width=""0.8"" stroke-linecap=""round""/>";

    // ━━ Archive / compressed files (zip, 7z, rar, etc.) ━━
    static string ArchiveFile(string ext) =>
        Page("#A78BFA") +
        // Zipper teeth pattern
        @"<rect x=""14"" y=""5"" width=""3.5"" height=""2.2"" rx=""0.6"" fill=""#8B5CF6"" opacity=""0.3""/>" +
        @"<rect x=""14"" y=""8.5"" width=""3.5"" height=""2.2"" rx=""0.6"" fill=""#8B5CF6"" opacity=""0.4""/>" +
        @"<rect x=""14"" y=""12"" width=""3.5"" height=""2.2"" rx=""0.6"" fill=""#8B5CF6"" opacity=""0.5""/>" +
        // Zipper pull
        @"<rect x=""13"" y=""15.5"" width=""6"" height=""3.5"" rx=""1"" fill=""#7C3AED"" opacity=""0.7""/>" +
        @"<rect x=""15"" y=""16.2"" width=""2"" height=""2"" rx=""0.5"" fill=""#fff"" opacity=""0.6""/>" +
        BigLabel(ext, "#5B21B6", 24) +
        Tab(ext, "#7C3AED");

    // ━━ Certificate files ━━
    static string CertificateFile() =>
        Card("#FFFBEB", "#D97706") +
        // Decorative dashed inner border
        @"<rect x=""5"" y=""5"" width=""22"" height=""22"" rx=""2"" fill=""none"" stroke=""#F59E0B"" stroke-width=""0.5"" stroke-dasharray=""1.5 1""/>" +
        // Seal rosette
        @"<circle cx=""16"" cy=""13"" r=""4.5"" fill=""#FCD34D"" stroke=""#D97706"" stroke-width=""0.8""/>" +
        @"<circle cx=""16"" cy=""13"" r=""2.8"" fill=""#FBBF24"" stroke=""#B45309"" stroke-width=""0.5""/>" +
        // Star
        @"<path d=""M16 10.5l.9 1.9 2.1.3-1.5 1.5.4 2.1-1.9-1-1.9 1 .4-2.1-1.5-1.5 2.1-.3z"" fill=""#B45309""/>" +
        // Ribbon tails
        @"<path d=""M13 17.5l-2 6 3-1.5 1.5 2v-6.5"" fill=""#EF4444"" opacity=""0.7""/>" +
        @"<path d=""M19 17.5l2 6-3-1.5-1.5 2v-6.5"" fill=""#EF4444"" opacity=""0.7""/>" +
        @"<line x1=""9"" y1=""6.5"" x2=""23"" y2=""6.5"" stroke=""#D4A574"" stroke-width=""0.7"" stroke-linecap=""round""/>";

    // ━━ Installer / disk image / package files ━━
    static string InstallerFile(string ext) =>
        Card("#EFF6FF", "#3B82F6") +
        // Box flap / lid
        @"<path d=""M2 12l14-8 14 8"" fill=""#DBEAFE"" stroke=""#3B82F6"" stroke-width=""0.8"" stroke-linejoin=""round""/>" +
        // Center seam
        @"<line x1=""16"" y1=""12"" x2=""16"" y2=""30"" stroke=""#93C5FD"" stroke-width=""0.5"" opacity=""0.4""/>" +
        // Down arrow (install)
        @"<path d=""M16 14.5v7"" stroke=""#2563EB"" stroke-width=""1.6"" stroke-linecap=""round""/>" +
        @"<path d=""M12.5 19L16 22.5l3.5-3.5"" fill=""none"" stroke=""#2563EB"" stroke-width=""1.6"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        (ext.Length > 0 ? Tab(ext, "#2563EB") : "");

    // ━━ Image files ━━
    static string ImageFile(string ext) =>
        Card("#FFF7ED", "#EA580C") +
        // Sun
        @"<circle cx=""10"" cy=""10"" r=""3"" fill=""#FDBA74""/>" +
        @"<circle cx=""10"" cy=""10"" r=""1.8"" fill=""#FB923C""/>" +
        // Mountains
        @"<path d=""M2 22l7.5-8 5 5.5 3.5-3L30 23v4.5c0 1.38-1.12 2.5-2.5 2.5h-23C3.12 30 2 28.88 2 27.5V22z"" fill=""#F97316"" opacity=""0.25""/>" +
        @"<path d=""M15 19.5l3-2.5L30 23v4.5c0 1.38-1.12 2.5-2.5 2.5h-23C3.12 30 2 28.88 2 27.5v-.5L15 19.5z"" fill=""#EA580C"" opacity=""0.18""/>" +
        Tab(ext, "#C2410C");

    // ━━ Web / HTML files ━━
    static string WebFile(string ext) =>
        Card("#F0FDF4", "#16A34A") +
        // Title bar
        @"<rect x=""2"" y=""2"" width=""28"" height=""7"" rx=""4.5"" fill=""#DCFCE7""/>" +
        @"<rect x=""2"" y=""6"" width=""28"" height=""3"" fill=""#DCFCE7""/>" +
        // Traffic lights
        @"<circle cx=""6"" cy=""5.5"" r=""1"" fill=""#EF4444"" opacity=""0.6""/>" +
        @"<circle cx=""9.5"" cy=""5.5"" r=""1"" fill=""#EAB308"" opacity=""0.6""/>" +
        @"<circle cx=""13"" cy=""5.5"" r=""1"" fill=""#22C55E"" opacity=""0.6""/>" +
        // Angle brackets
        @"<path d=""M11 14L7 17.5 11 21"" fill=""none"" stroke=""#16A34A"" stroke-width=""1.4"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<path d=""M21 14l4 3.5L21 21"" fill=""none"" stroke=""#16A34A"" stroke-width=""1.4"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<line x1=""17.5"" y1=""13"" x2=""14.5"" y2=""22"" stroke=""#4ADE80"" stroke-width=""1.2"" stroke-linecap=""round""/>" +
        Tab(ext, "#16A34A");

    // ━━ Code files ━━
    static string CodeFile(string ext) =>
        Card("#1E1B4B", "#4338CA") +
        // Title bar dots
        @"<circle cx=""6"" cy=""5.5"" r=""1"" fill=""#EF4444"" opacity=""0.65""/>" +
        @"<circle cx=""9.5"" cy=""5.5"" r=""1"" fill=""#F59E0B"" opacity=""0.65""/>" +
        @"<circle cx=""13"" cy=""5.5"" r=""1"" fill=""#22C55E"" opacity=""0.65""/>" +
        // Code lines (colored)
        @"<line x1=""6"" y1=""10"" x2=""13"" y2=""10"" stroke=""#818CF8"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.7""/>" +
        @"<line x1=""15"" y1=""10"" x2=""24"" y2=""10"" stroke=""#67E8F9"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""8"" y1=""13"" x2=""18"" y2=""13"" stroke=""#A78BFA"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.6""/>" +
        @"<line x1=""20"" y1=""13"" x2=""26"" y2=""13"" stroke=""#FCA5A5"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""8"" y1=""16"" x2=""15"" y2=""16"" stroke=""#86EFAC"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""6"" y1=""19"" x2=""19"" y2=""19"" stroke=""#818CF8"" stroke-width=""1"" stroke-linecap=""round"" opacity=""0.5""/>" +
        Tab(ext, "#6366F1");

    // ━━ Config files (json, yaml, xml, toml, plist) ━━
    static string ConfigFile(string ext) =>
        Page("#D97706") +
        // Gear icon
        @"<circle cx=""16"" cy=""15"" r=""5"" fill=""none"" stroke=""#D97706"" stroke-width=""1"" opacity=""0.4""/>" +
        @"<circle cx=""16"" cy=""15"" r=""2"" fill=""#D97706"" opacity=""0.35""/>" +
        @"<line x1=""16"" y1=""8.5"" x2=""16"" y2=""10"" stroke=""#D97706"" stroke-width=""1.5"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""16"" y1=""20"" x2=""16"" y2=""21.5"" stroke=""#D97706"" stroke-width=""1.5"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""9.5"" y1=""15"" x2=""11"" y2=""15"" stroke=""#D97706"" stroke-width=""1.5"" stroke-linecap=""round"" opacity=""0.5""/>" +
        @"<line x1=""21"" y1=""15"" x2=""22.5"" y2=""15"" stroke=""#D97706"" stroke-width=""1.5"" stroke-linecap=""round"" opacity=""0.5""/>" +
        Tab(ext, "#B45309");

    // ━━ Video files ━━
    static string VideoFile() =>
        Card("#F5F3FF", "#7C3AED") +
        // Film perforations left
        @"<rect x=""3.5"" y=""5"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""3.5"" y=""10"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""3.5"" y=""15"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""3.5"" y=""20"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""3.5"" y=""25"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        // Film perforations right
        @"<rect x=""26"" y=""5"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""26"" y=""10"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""26"" y=""15"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""26"" y=""20"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<rect x=""26"" y=""25"" width=""2.5"" height=""2"" rx=""0.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        // Play triangle
        @"<path d=""M13 11v10l9-5z"" fill=""#7C3AED"" opacity=""0.65""/>";

    // ━━ Audio files ━━
    static string AudioFile() =>
        Card("#FDF2F8", "#DB2777") +
        // Vinyl record hints
        @"<circle cx=""16"" cy=""16"" r=""10"" fill=""#F9A8D4"" opacity=""0.15""/>" +
        @"<circle cx=""16"" cy=""16"" r=""6.5"" fill=""none"" stroke=""#DB2777"" stroke-width=""0.4"" opacity=""0.25""/>" +
        @"<circle cx=""16"" cy=""16"" r=""3"" fill=""#DB2777"" opacity=""0.12""/>" +
        // Music note
        @"<circle cx=""12.5"" cy=""21"" r=""2.5"" fill=""#EC4899"" opacity=""0.6"" stroke=""#DB2777"" stroke-width=""0.6""/>" +
        @"<path d=""M15 21V9.5l8-2v9"" fill=""none"" stroke=""#DB2777"" stroke-width=""1.2"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<circle cx=""23"" cy=""16.5"" r=""2.5"" fill=""#EC4899"" opacity=""0.6"" stroke=""#DB2777"" stroke-width=""0.6""/>";

    // ━━ Font files ━━
    static string FontFile(string ext) =>
        Page("#6B7280") +
        @"<text x=""16"" y=""15"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""Georgia,'Times New Roman',serif"" font-size=""13"" font-weight=""700"" fill=""#374151"" font-style=""italic"">Aa</text>" +
        Tab(ext, "#6B7280");

    // ━━ Database files ━━
    static string DatabaseFile(string ext) =>
        Card("#ECFDF5", "#059669") +
        // Cylinder top ellipse
        @"<ellipse cx=""16"" cy=""8"" rx=""12"" ry=""5"" fill=""#D1FAE5"" stroke=""#059669"" stroke-width=""0.8""/>" +
        // Cylinder body
        @"<path d=""M4 8v16c0 2.76 5.37 5 12 5s12-2.24 12-5V8"" fill=""none"" stroke=""#059669"" stroke-width=""0.8""/>" +
        // Layer lines
        @"<ellipse cx=""16"" cy=""15"" rx=""12"" ry=""3.5"" fill=""none"" stroke=""#059669"" stroke-width=""0.4"" opacity=""0.3""/>" +
        @"<ellipse cx=""16"" cy=""21"" rx=""12"" ry=""3.5"" fill=""none"" stroke=""#059669"" stroke-width=""0.4"" opacity=""0.3""/>" +
        Tab(ext, "#059669");

    // ━━ eBook files ━━
    static string EbookFile(string ext) =>
        // Book cover filling 28×28
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""3"" fill=""#FEF3C7"" stroke=""#92400E"" stroke-width=""0.9""/>" +
        // Book spine
        @"<rect x=""2"" y=""2"" width=""5"" height=""28"" rx=""3"" fill=""#D97706"" opacity=""0.25""/>" +
        @"<line x1=""7"" y1=""2"" x2=""7"" y2=""30"" stroke=""#92400E"" stroke-width=""0.4"" opacity=""0.5""/>" +
        // Text lines on cover
        @"<line x1=""10"" y1=""9"" x2=""26"" y2=""9"" stroke=""#B45309"" stroke-width=""0.9"" stroke-linecap=""round"" opacity=""0.4""/>" +
        @"<line x1=""10"" y1=""13"" x2=""22"" y2=""13"" stroke=""#B45309"" stroke-width=""0.9"" stroke-linecap=""round"" opacity=""0.3""/>" +
        Tab(ext, "#92400E");

    // ━━ Design files (psd, ai, sketch, figma, etc.) ━━
    static string DesignFile(string ext) =>
        Card("#FDF2F8", "#DB2777") +
        // Checkerboard transparency hint
        @"<rect x=""5"" y=""5"" width=""5.5"" height=""5.5"" fill=""#FBCFE8"" opacity=""0.3""/>" +
        @"<rect x=""10.5"" y=""10.5"" width=""5.5"" height=""5.5"" fill=""#FBCFE8"" opacity=""0.3""/>" +
        // Pen bezier path
        @"<path d=""M7 21c4-14 14-14 18 0"" fill=""none"" stroke=""#DB2777"" stroke-width=""1.3"" stroke-linecap=""round""/>" +
        // Control handles
        @"<line x1=""7"" y1=""21"" x2=""11"" y2=""7"" stroke=""#F9A8D4"" stroke-width=""0.5"" stroke-dasharray=""1.5 1""/>" +
        @"<line x1=""25"" y1=""21"" x2=""21"" y2=""7"" stroke=""#F9A8D4"" stroke-width=""0.5"" stroke-dasharray=""1.5 1""/>" +
        @"<circle cx=""7"" cy=""21"" r=""1.5"" fill=""#EC4899""/>" +
        @"<circle cx=""25"" cy=""21"" r=""1.5"" fill=""#EC4899""/>" +
        @"<circle cx=""11"" cy=""7"" r=""1.2"" fill=""#F472B6""/>" +
        @"<circle cx=""21"" cy=""7"" r=""1.2"" fill=""#F472B6""/>" +
        Tab(ext, "#DB2777");

    // ━━ 3D model files ━━
    static string ThreeDFile(string ext) =>
        Card("#F5F3FF", "#7C3AED") +
        // 3D cube wireframe - fills the card
        @"<path d=""M16 4l-10 5.5v11L16 26l10-5.5v-11L16 4z"" fill=""none"" stroke=""#7C3AED"" stroke-width=""0.9"" stroke-linejoin=""round""/>" +
        @"<path d=""M6 9.5l10 5.5 10-5.5"" fill=""none"" stroke=""#7C3AED"" stroke-width=""0.7"" stroke-linejoin=""round""/>" +
        @"<line x1=""16"" y1=""15"" x2=""16"" y2=""26"" stroke=""#7C3AED"" stroke-width=""0.7""/>" +
        // Shading
        @"<path d=""M6 9.5l10 5.5v11l-10-5.5z"" fill=""#8B5CF6"" opacity=""0.1""/>" +
        @"<path d=""M26 9.5l-10 5.5v11l10-5.5z"" fill=""#8B5CF6"" opacity=""0.06""/>" +
        Tab(ext, "#7C3AED");

    // ━━ Subtitle files ━━
    static string SubtitleFile(string ext) =>
        Page("#64748B") +
        @"<rect x=""6"" y=""12"" width=""20"" height=""4"" rx=""1.5"" fill=""#CBD5E1"" opacity=""0.5""/>" +
        @"<rect x=""8"" y=""18"" width=""16"" height=""4"" rx=""1.5"" fill=""#CBD5E1"" opacity=""0.35""/>" +
        Tab(ext, "#64748B");

    // ━━ Executable files ━━
    static string ExecutableFile(string ext) =>
        Page("#475569") +
        @"<path d=""M9 13l3.5 3.5L9 20"" fill=""none"" stroke=""#475569"" stroke-width=""1.3"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<line x1=""15"" y1=""20"" x2=""23"" y2=""20"" stroke=""#475569"" stroke-width=""1.2"" stroke-linecap=""round""/>" +
        Tab(ext, "#475569");
}