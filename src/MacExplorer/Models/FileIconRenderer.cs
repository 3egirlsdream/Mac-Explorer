namespace MacExplorer.Models;

using System.Net;

/// <summary>
/// Renders SVG file type icons in Microsoft Fluent Design style.
/// ViewBox: 0 0 32 32. All icons fill a consistent 28×28 visual area.
/// Fluent characteristics: rounded shapes, layered depth, vibrant fills, minimal strokes.
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
            "file-vm" => VirtualMachineFile(ext),
            "file-vs-solution" => VsSolutionFile(),
            "file-vs-project" => VsProjectFile(ext),
            "file-razor" => RazorFile(),
            _ => GenericFile()
        };
        return $@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{inner}</svg>";
    }

    /// <summary>Renders a Fluent-style folder icon at given size.</summary>
    public static string RenderFolder(int size) =>
        $@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{FolderIcon()}</svg>";

    static string FolderIcon() =>
        // Soft shadow (consistent with Doc/Card)
        @"<path d=""M3 9.5A4.5 4.5 0 0 1 7.5 5h3.964a3.5 3.5 0 0 1 2.475 1.025L16 8.085l-3.475 3.476a1.5 1.5 0 0 1-1.06.439H3zM3 14v10.5A4.5 4.5 0 0 0 7.5 29h19a4.5 4.5 0 0 0 4.5-4.5V13a4.5 4.5 0 0 0-4.5-4.5h-8.086l-4.475 4.475A3.5 3.5 0 0 1 11.464 14z"" fill=""#000"" opacity=""0.04""/>" +
        // Back panel (deep amber) — Fluent folder silhouette
        @"<path d=""M2 8.5A4.5 4.5 0 0 1 6.5 4h3.964a3.5 3.5 0 0 1 2.475 1.025L15 7.085l-3.475 3.476a1.5 1.5 0 0 1-1.06.439H2z"" fill=""#D4A017""/>" +
        // Front body (warm amber)
        @"<path d=""M2 13v10.5A4.5 4.5 0 0 0 6.5 28h19a4.5 4.5 0 0 0 4.5-4.5V12a4.5 4.5 0 0 0-4.5-4.5h-8.086l-4.475 4.475A3.5 3.5 0 0 1 10.464 13z"" fill=""#F5C731""/>" +
        // Subtle top-edge highlight
        @"<rect x=""2"" y=""13"" width=""28"" height=""1"" rx=""0.5"" fill=""#FFF"" opacity=""0.15""/>";

    static string E(string s) => WebUtility.HtmlEncode(s);

    // ── Font sizing ──
    static double TFs(string t) => t.Length switch { <= 2 => 6.2, 3 => 5.5, 4 => 4.8, _ => 4 };
    static double LFs(string t) => t.Length switch { <= 1 => 11, 2 => 9, 3 => 7.5, 4 => 6.5, _ => 5.2 };

    // ── Fluent shared shapes ──

    // Fluent document base: soft white card with subtle shadow layer + folded corner
    static string Doc(string tint = "#E8ECF0") =>
        // Shadow layer for depth
        $@"<rect x=""3"" y=""3"" width=""28"" height=""28"" rx=""5"" fill=""#000"" opacity=""0.04""/>" +
        // Main page
        $@"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""5"" fill=""#FAFBFC""/>" +
        // Top-right tinted fold area
        $@"<path d=""M22 2h3c2.76 0 5 2.24 5 5v0h-5c-1.66 0-3-1.34-3-3V2z"" fill=""{tint}""/>";

    // Fluent rounded card (for non-document icons)
    static string Card(string fill, string tint) =>
        $@"<rect x=""3"" y=""3"" width=""28"" height=""28"" rx=""6"" fill=""#000"" opacity=""0.04""/>" +
        $@"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""{fill}""/>" +
        $@"<rect x=""2"" y=""2"" width=""28"" height=""14"" rx=""6"" fill=""{tint}"" opacity=""0.35""/>";

    // Fluent floating extension badge (bottom-right, rounded pill)
    static string Badge(string text, string bg)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fs = TFs(text);
        var w = Math.Max(11, 3.5 + text.Length * 3.6);
        var x = 29 - w;
        return $@"<rect x=""{x:F1}"" y=""22.5"" width=""{w:F1}"" height=""7.5"" rx=""3"" fill=""{bg}""/>" +
               $@"<text x=""{x + w / 2:F1}"" y=""26.4"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Text',-apple-system,sans-serif"" font-size=""{fs:F1}"" font-weight=""700"" fill=""#fff"" letter-spacing=""0.2"">{E(text)}</text>";
    }

    // Centered extension label (for doc-type icons that show ext in body)
    static string CenterExt(string text, string color, double cy = 19)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var fs = LFs(text);
        return $@"<text x=""16"" y=""{cy:F1}"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""{fs:F1}"" font-weight=""700"" fill=""{color}"" letter-spacing=""0.2"">{E(text)}</text>";
    }

    // Fluent text lines (filled rounded rects instead of strokes)
    static string TextLines(double x, double y, double w, int count, string color, double opacity = 0.12)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < count; i++)
            sb.Append($@"<rect x=""{x}"" y=""{y + i * 3.2:F1}"" width=""{w - i * 2.5:F1}"" height=""1.6"" rx=""0.8"" fill=""{color}"" opacity=""{opacity}""/>");
        return sb.ToString();
    }

    // ── Icon types ──

    // ━━ Generic fallback ━━
    static string GenericFile() =>
        Doc() + TextLines(8, 12, 16, 4, "#94A3B8", 0.2);

    // ━━ Text files (txt, ini, cfg, log, etc.) ━━
    static string TextFile(string ext) =>
        Doc("#CBD5E1") +
        TextLines(8, 10, 16, 2, "#94A3B8", 0.18) +
        CenterExt(ext, "#64748B", 22);

    // ━━ Markdown ━━
    static string MarkdownFile(string ext) =>
        Doc("#C7D2FE") +
        TextLines(8, 10, 16, 2, "#818CF8", 0.18) +
        CenterExt(ext, "#4338CA", 22);

    // ━━ Microsoft Word (Fluent file icon style) ━━
    static string WordIcon() =>
        // Background doc page
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""4"" fill=""#D6E4F5""/>" +
        @"<rect x=""10"" y=""2"" width=""20"" height=""14"" rx=""4"" fill=""#E8EFF9"" opacity=""0.7""/>" +
        TextLines(14, 8, 13, 5, "#2B579A", 0.12) +
        // Brand panel with Fluent layered depth
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""4"" fill=""#185ABD""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""4"" fill=""#2B7CD3"" opacity=""0.6""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""13"" font-weight=""700"" fill=""#fff"">W</text>";

    // ━━ Microsoft Excel ━━
    static string ExcelIcon() =>
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""4"" fill=""#D0E8D8""/>" +
        @"<rect x=""10"" y=""2"" width=""20"" height=""14"" rx=""4"" fill=""#E3F2E8"" opacity=""0.7""/>" +
        // Simplified grid cells (filled)
        @"<rect x=""14"" y=""8"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.08""/>" +
        @"<rect x=""21"" y=""8"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.08""/>" +
        @"<rect x=""14"" y=""13.5"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.06""/>" +
        @"<rect x=""21"" y=""13.5"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.06""/>" +
        @"<rect x=""14"" y=""19"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.04""/>" +
        @"<rect x=""21"" y=""19"" width=""6"" height=""4.5"" rx=""1"" fill=""#217346"" opacity=""0.04""/>" +
        // Brand panel
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""4"" fill=""#107C41""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""4"" fill=""#21A366"" opacity=""0.6""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""13"" font-weight=""700"" fill=""#fff"">X</text>";

    // ━━ Microsoft PowerPoint ━━
    static string PowerPointIcon() =>
        @"<rect x=""10"" y=""2"" width=""20"" height=""28"" rx=""4"" fill=""#F2D9D0""/>" +
        @"<rect x=""10"" y=""2"" width=""20"" height=""14"" rx=""4"" fill=""#F8E8E2"" opacity=""0.7""/>" +
        // Slide previews (filled rects)
        @"<rect x=""14"" y=""8"" width=""12"" height=""7"" rx=""2"" fill=""#C43E1C"" opacity=""0.08""/>" +
        @"<rect x=""14"" y=""17"" width=""12"" height=""7"" rx=""2"" fill=""#C43E1C"" opacity=""0.06""/>" +
        // Brand panel
        @"<rect x=""2"" y=""4"" width=""16"" height=""24"" rx=""4"" fill=""#C43E1C""/>" +
        @"<rect x=""2"" y=""4"" width=""16"" height=""12"" rx=""4"" fill=""#E04E2C"" opacity=""0.6""/>" +
        @"<text x=""10"" y=""17.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""13"" font-weight=""700"" fill=""#fff"">P</text>";

    // ━━ PDF ━━
    static string PdfFile() =>
        Doc("#FECACA") +
        TextLines(8, 7, 14, 2, "#DC2626", 0.1) +
        // Fluent red banner
        @"<rect x=""4"" y=""15"" width=""24"" height=""10"" rx=""4"" fill=""#DC2626""/>" +
        @"<rect x=""4"" y=""15"" width=""24"" height=""5"" rx=""4"" fill=""#EF4444"" opacity=""0.4""/>" +
        @"<text x=""16"" y=""20.8"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""7"" font-weight=""700"" fill=""#fff"" letter-spacing=""0.8"">PDF</text>";

    // ━━ Archive / compressed files ━━
    static string ArchiveFile(string ext) =>
        Doc("#DDD6FE") +
        // Zipper track (alternating filled blocks)
        @"<rect x=""14"" y=""5"" width=""4"" height=""2.4"" rx=""1.2"" fill=""#8B5CF6"" opacity=""0.15""/>" +
        @"<rect x=""14"" y=""8.5"" width=""4"" height=""2.4"" rx=""1.2"" fill=""#8B5CF6"" opacity=""0.22""/>" +
        @"<rect x=""14"" y=""12"" width=""4"" height=""2.4"" rx=""1.2"" fill=""#8B5CF6"" opacity=""0.28""/>" +
        // Zipper pull (rounded)
        @"<rect x=""13"" y=""15.5"" width=""6"" height=""4"" rx=""2"" fill=""#7C3AED"" opacity=""0.55""/>" +
        @"<rect x=""15"" y=""16.5"" width=""2"" height=""2"" rx=""1"" fill=""#fff"" opacity=""0.5""/>" +
        CenterExt(ext, "#6D28D9", 25);

    // ━━ Certificate files ━━
    static string CertificateFile() =>
        Card("#FFFBEB", "#FEF3C7") +
        // Seal circle with layered depth
        @"<circle cx=""16"" cy=""13.5"" r=""5.5"" fill=""#FDE68A"" opacity=""0.6""/>" +
        @"<circle cx=""16"" cy=""13.5"" r=""4"" fill=""#FBBF24""/>" +
        @"<circle cx=""16"" cy=""13.5"" r=""2.5"" fill=""#F59E0B""/>" +
        // Checkmark
        @"<path d=""M14 13.5l1.5 1.5 3-3"" fill=""none"" stroke=""#fff"" stroke-width=""1.3"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        // Ribbon
        @"<path d=""M12.5 18.5l-2 7 3.5-1.5 2 2.5v-8"" fill=""#EF4444"" opacity=""0.55""/>" +
        @"<path d=""M19.5 18.5l2 7-3.5-1.5-2 2.5v-8"" fill=""#EF4444"" opacity=""0.55""/>";

    // ━━ Installer / package files ━━
    static string InstallerFile(string ext) =>
        Card("#DBEAFE", "#BFDBFE") +
        // Package box (filled, layered)
        @"<rect x=""6"" y=""11"" width=""20"" height=""14"" rx=""3"" fill=""#3B82F6"" opacity=""0.12""/>" +
        // Box flap
        @"<path d=""M6 14l10-7 10 7"" fill=""#60A5FA"" opacity=""0.2""/>" +
        // Down arrow (rounded)
        @"<circle cx=""16"" cy=""19"" r=""5"" fill=""#3B82F6"" opacity=""0.15""/>" +
        @"<path d=""M16 15v6m-2.5-2.5L16 21l2.5-2.5"" fill=""none"" stroke=""#2563EB"" stroke-width=""1.5"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        (ext.Length > 0 ? Badge(ext, "#2563EB") : "");

    // ━━ Image files ━━
    static string ImageFile(string ext) =>
        Card("#FFF7ED", "#FFEDD5") +
        // Sun (layered circles)
        @"<circle cx=""10.5"" cy=""10"" r=""3.5"" fill=""#FDBA74"" opacity=""0.5""/>" +
        @"<circle cx=""10.5"" cy=""10"" r=""2.2"" fill=""#FB923C""/>" +
        // Mountains (soft filled shapes)
        @"<path d=""M2 23l8-9 5.5 6 3.5-3.5L30 24v3.5c0 1.38-1.12 2.5-2.5 2.5h-23C3.12 30 2 28.88 2 27.5V23z"" fill=""#FB923C"" opacity=""0.2""/>" +
        @"<path d=""M16 20l3-3L30 24v3.5c0 1.38-1.12 2.5-2.5 2.5h-23C3.12 30 2 28.88 2 27.5v-1L16 20z"" fill=""#EA580C"" opacity=""0.15""/>" +
        Badge(ext, "#C2410C");

    // ━━ Web / HTML files ━━
    static string WebFile(string ext) =>
        Card("#F0FDF4", "#DCFCE7") +
        // Browser chrome (Fluent rounded)
        @"<rect x=""5"" y=""5"" width=""22"" height=""4"" rx=""2"" fill=""#16A34A"" opacity=""0.1""/>" +
        @"<circle cx=""8"" cy=""7"" r=""0.8"" fill=""#EF4444"" opacity=""0.5""/>" +
        @"<circle cx=""10.5"" cy=""7"" r=""0.8"" fill=""#EAB308"" opacity=""0.5""/>" +
        @"<circle cx=""13"" cy=""7"" r=""0.8"" fill=""#22C55E"" opacity=""0.5""/>" +
        // HTML brackets (Fluent rounded)
        @"<path d=""M12 13L8 17 12 21"" fill=""none"" stroke=""#16A34A"" stroke-width=""1.5"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<path d=""M20 13l4 4-4 4"" fill=""none"" stroke=""#16A34A"" stroke-width=""1.5"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<line x1=""17.5"" y1=""12"" x2=""14.5"" y2=""22"" stroke=""#4ADE80"" stroke-width=""1.2"" stroke-linecap=""round""/>" +
        Badge(ext, "#16A34A");

    // ━━ Code files ━━
    static string CodeFile(string ext) =>
        // Dark Fluent card
        $@"<rect x=""3"" y=""3"" width=""28"" height=""28"" rx=""6"" fill=""#000"" opacity=""0.06""/>" +
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#1E1B4B""/>" +
        @"<rect x=""2"" y=""2"" width=""28"" height=""14"" rx=""6"" fill=""#312E81"" opacity=""0.5""/>" +
        // Window dots
        @"<circle cx=""6.5"" cy=""6"" r=""1"" fill=""#EF4444"" opacity=""0.55""/>" +
        @"<circle cx=""10"" cy=""6"" r=""1"" fill=""#F59E0B"" opacity=""0.55""/>" +
        @"<circle cx=""13.5"" cy=""6"" r=""1"" fill=""#22C55E"" opacity=""0.55""/>" +
        // Syntax-colored code lines (Fluent: rounded rects, not strokes)
        @"<rect x=""6"" y=""10"" width=""8"" height=""1.4"" rx=""0.7"" fill=""#818CF8"" opacity=""0.7""/>" +
        @"<rect x=""16"" y=""10"" width=""10"" height=""1.4"" rx=""0.7"" fill=""#67E8F9"" opacity=""0.4""/>" +
        @"<rect x=""8"" y=""13.5"" width=""11"" height=""1.4"" rx=""0.7"" fill=""#A78BFA"" opacity=""0.5""/>" +
        @"<rect x=""21"" y=""13.5"" width=""5"" height=""1.4"" rx=""0.7"" fill=""#FCA5A5"" opacity=""0.4""/>" +
        @"<rect x=""8"" y=""17"" width=""7"" height=""1.4"" rx=""0.7"" fill=""#86EFAC"" opacity=""0.45""/>" +
        @"<rect x=""6"" y=""20.5"" width=""14"" height=""1.4"" rx=""0.7"" fill=""#818CF8"" opacity=""0.4""/>" +
        Badge(ext, "#6366F1");

    // ━━ Config files (json, yaml, xml, toml, plist) ━━
    static string ConfigFile(string ext) =>
        Doc("#FDE68A") +
        // Fluent toggle sliders (filled rounded rects)
        @"<rect x=""8"" y=""10"" width=""16"" height=""3"" rx=""1.5"" fill=""#D97706"" opacity=""0.12""/>" +
        @"<circle cx=""12"" cy=""11.5"" r=""2"" fill=""#F59E0B"" opacity=""0.7""/>" +
        @"<rect x=""8"" y=""16"" width=""16"" height=""3"" rx=""1.5"" fill=""#D97706"" opacity=""0.12""/>" +
        @"<circle cx=""20"" cy=""17.5"" r=""2"" fill=""#F59E0B"" opacity=""0.7""/>" +
        Badge(ext, "#B45309");

    // ━━ Video files ━━
    static string VideoFile() =>
        Card("#F5F3FF", "#EDE9FE") +
        // Play button (layered circle + triangle)
        @"<circle cx=""16"" cy=""16"" r=""8"" fill=""#7C3AED"" opacity=""0.12""/>" +
        @"<circle cx=""16"" cy=""16"" r=""5.5"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<path d=""M14 13v6l5.5-3z"" fill=""#7C3AED"" opacity=""0.75""/>";

    // ━━ Audio files ━━
    static string AudioFile() =>
        Card("#FDF2F8", "#FCE7F3") +
        // Sound wave bars (Fluent rounded)
        @"<rect x=""7"" y=""14"" width=""2"" height=""4"" rx=""1"" fill=""#DB2777"" opacity=""0.3""/>" +
        @"<rect x=""10.5"" y=""11"" width=""2"" height=""10"" rx=""1"" fill=""#DB2777"" opacity=""0.4""/>" +
        @"<rect x=""14"" y=""8"" width=""2"" height=""16"" rx=""1"" fill=""#DB2777"" opacity=""0.5""/>" +
        @"<rect x=""17.5"" y=""10"" width=""2"" height=""12"" rx=""1"" fill=""#DB2777"" opacity=""0.4""/>" +
        @"<rect x=""21"" y=""12"" width=""2"" height=""8"" rx=""1"" fill=""#DB2777"" opacity=""0.35""/>" +
        @"<rect x=""24.5"" y=""14"" width=""2"" height=""4"" rx=""1"" fill=""#DB2777"" opacity=""0.25""/>";

    // ━━ Font files ━━
    static string FontFile(string ext) =>
        Doc("#D1D5DB") +
        @"<text x=""16"" y=""16"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""Georgia,'Times New Roman',serif"" font-size=""14"" font-weight=""700"" fill=""#374151"" opacity=""0.7"">Aa</text>" +
        Badge(ext, "#6B7280");

    // ━━ Database files ━━
    static string DatabaseFile(string ext) =>
        Card("#ECFDF5", "#D1FAE5") +
        // Cylinder (layered fills)
        @"<ellipse cx=""16"" cy=""9"" rx=""10"" ry=""4.5"" fill=""#34D399"" opacity=""0.25""/>" +
        @"<rect x=""6"" y=""9"" width=""20"" height=""14"" fill=""#059669"" opacity=""0.08""/>" +
        @"<ellipse cx=""16"" cy=""23"" rx=""10"" ry=""4"" fill=""#34D399"" opacity=""0.12""/>" +
        // Layer separators
        @"<ellipse cx=""16"" cy=""15"" rx=""10"" ry=""3"" fill=""none"" stroke=""#059669"" stroke-width=""0.5"" opacity=""0.2""/>" +
        @"<ellipse cx=""16"" cy=""19.5"" rx=""10"" ry=""3"" fill=""none"" stroke=""#059669"" stroke-width=""0.5"" opacity=""0.15""/>" +
        Badge(ext, "#059669");

    // ━━ eBook files ━━
    static string EbookFile(string ext) =>
        // Book shape with Fluent depth
        @"<rect x=""3"" y=""3"" width=""28"" height=""28"" rx=""4"" fill=""#000"" opacity=""0.04""/>" +
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""4"" fill=""#FEF3C7""/>" +
        // Spine gradient
        @"<rect x=""2"" y=""2"" width=""6"" height=""28"" rx=""4"" fill=""#D97706"" opacity=""0.2""/>" +
        // Cover lines
        @"<rect x=""11"" y=""9"" width=""15"" height=""1.6"" rx=""0.8"" fill=""#92400E"" opacity=""0.15""/>" +
        @"<rect x=""11"" y=""13"" width=""11"" height=""1.6"" rx=""0.8"" fill=""#92400E"" opacity=""0.1""/>" +
        Badge(ext, "#92400E");

    // ━━ Design files (psd, ai, sketch, figma, etc.) ━━
    static string DesignFile(string ext) =>
        Card("#FDF2F8", "#FCE7F3") +
        // Pen bezier path (Fluent smooth curves)
        @"<path d=""M8 22Q12 6 16 14Q20 22 24 10"" fill=""none"" stroke=""#DB2777"" stroke-width=""1.5"" stroke-linecap=""round""/>" +
        // Anchor dots
        @"<circle cx=""8"" cy=""22"" r=""2"" fill=""#EC4899"" opacity=""0.6""/>" +
        @"<circle cx=""16"" cy=""14"" r=""1.5"" fill=""#EC4899"" opacity=""0.5""/>" +
        @"<circle cx=""24"" cy=""10"" r=""2"" fill=""#EC4899"" opacity=""0.6""/>" +
        Badge(ext, "#DB2777");

    // ━━ 3D model files ━━
    static string ThreeDFile(string ext) =>
        Card("#F5F3FF", "#EDE9FE") +
        // 3D cube (filled faces with layered opacity)
        @"<path d=""M16 5L6 10.5 16 16l10-5.5z"" fill=""#8B5CF6"" opacity=""0.15""/>" +
        @"<path d=""M6 10.5v11L16 27V16z"" fill=""#8B5CF6"" opacity=""0.1""/>" +
        @"<path d=""M26 10.5v11L16 27V16z"" fill=""#8B5CF6"" opacity=""0.06""/>" +
        // Edges
        @"<path d=""M16 5L6 10.5 16 16l10-5.5zM6 10.5v11L16 27V16M26 10.5v11L16 27"" fill=""none"" stroke=""#7C3AED"" stroke-width=""0.7"" stroke-linejoin=""round""/>" +
        Badge(ext, "#7C3AED");

    // ━━ Subtitle files ━━
    static string SubtitleFile(string ext) =>
        Doc("#CBD5E1") +
        // Subtitle bars (Fluent filled rects)
        @"<rect x=""6"" y=""12"" width=""20"" height=""3.5"" rx=""1.75"" fill=""#64748B"" opacity=""0.15""/>" +
        @"<rect x=""8"" y=""18"" width=""16"" height=""3.5"" rx=""1.75"" fill=""#64748B"" opacity=""0.1""/>" +
        Badge(ext, "#64748B");

    // ━━ Executable files ━━
    static string ExecutableFile(string ext) =>
        Doc("#CBD5E1") +
        // Terminal prompt (Fluent rounded)
        @"<path d=""M10 13l3.5 3.5L10 20"" fill=""none"" stroke=""#475569"" stroke-width=""1.5"" stroke-linecap=""round"" stroke-linejoin=""round""/>" +
        @"<rect x=""16"" y=""19"" width=""8"" height=""1.6"" rx=""0.8"" fill=""#475569"" opacity=""0.4""/>" +
        Badge(ext, "#475569");

    // Scaled VS ribbon logo (from official Visual Studio icon, mapped to 32×32)
    static string VsLogo() =>
        @"<g transform=""translate(6,4) scale(0.019)"">" +
        @"<path d=""M166 791l-18 15a43 43 0 01-44 6l-78-32A43 43 0 010 741V283a43 43 0 0126-39l78-32a43 43 0 0144 7l18 15a24 24 0 00-33 6 21 21 0 00-5 13v519a24 24 0 0024 24 24 24 0 0014-5z"" fill=""#52218A""/>" +
        @"<path d=""M1029 167v2a41 41 0 00-66-31L166 791l-18 15a43 43 0 01-44 6l-78-32A43 43 0 010 741v-4a25 25 0 0025 25 24 24 0 0018-8L708 19a64 64 0 0173-13l212 102a64 64 0 0136 58z"" fill=""#6C33AF""/>" +
        @"<path d=""M1029 855v3a64 64 0 01-36 58l-212 102a64 64 0 01-73-12L43 270a25 25 0 00-35-2 25 25 0 00-8 18v-4a43 43 0 0126-39l78-32a43 43 0 0144 7l18 15 797 653a41 41 0 0066-31z"" fill=""#854CC7""/>" +
        @"<path d=""M993 108L781 6a64 64 0 00-68 8 38 38 0 0153 9 37 37 0 017 21v934a38 38 0 01-60 31 64 64 0 0068 8l212-102a64 64 0 0036-58V166a64 64 0 00-36-58z"" fill=""#B179F1""/>" +
        @"</g>";

    // ━━ Visual Studio Solution (.sln) ━━
    static string VsSolutionFile() =>
        Card("#F3E8FF", "#E9D5FF") + VsLogo() + Badge("SLN", "#6D28D9");

    // ━━ Visual Studio Project (.csproj, etc.) ━━
    static string VsProjectFile(string ext) =>
        Card("#F3E8FF", "#E9D5FF") + VsLogo() + Badge(ext, "#6D28D9");

    // ━━ Razor component files (.razor) ━━
    static string RazorFile() =>
        // Blazor-inspired card with purple-blue tones
        $@"<rect x=""3"" y=""3"" width=""28"" height=""28"" rx=""6"" fill=""#000"" opacity=""0.04""/>" +
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#EDE9FE""/>" +
        @"<rect x=""2"" y=""2"" width=""28"" height=""14"" rx=""6"" fill=""#DDD6FE"" opacity=""0.5""/>" +
        // Blazor flame/razor shape
        @"<path d=""M16 6c-1.5 4 1 6 1 6s-3.5-1-4.5 3c-.7 2.8 1.5 5 4 5.5"" fill=""none"" stroke=""#7C3AED"" stroke-width=""1.3"" stroke-linecap=""round""/>" +
        @"<path d=""M16 6c1.5 4-1 6-1 6s3.5-1 4.5 3c.7 2.8-1.5 5-4 5.5"" fill=""none"" stroke=""#7C3AED"" stroke-width=""1.3"" stroke-linecap=""round""/>" +
        @"<circle cx=""16"" cy=""15"" r=""3"" fill=""#7C3AED"" opacity=""0.2""/>" +
        @"<circle cx=""16"" cy=""15"" r=""1.8"" fill=""#7C3AED"" opacity=""0.4""/>" +
        // @ symbol (Razor identifier)
        @"<text x=""16"" y=""15.5"" text-anchor=""middle"" dominant-baseline=""central"" font-family=""'Segoe UI','SF Pro Display',-apple-system,sans-serif"" font-size=""5"" font-weight=""700"" fill=""#fff"">@</text>" +
        Badge("RAZOR", "#6D28D9");

    // ━━ Virtual machine files (Parallels, VMware, VirtualBox, etc.) ━━
    static string VirtualMachineFile(string ext) =>
        Card("#EFF6FF", "#DBEAFE") +
        // Monitor frame
        @"<rect x=""5"" y=""6"" width=""22"" height=""14"" rx=""3"" fill=""#3B82F6"" opacity=""0.1""/>" +
        @"<rect x=""7"" y=""8"" width=""18"" height=""10"" rx=""2"" fill=""#2563EB"" opacity=""0.08""/>" +
        // VM chip symbol inside screen
        @"<rect x=""12"" y=""10.5"" width=""8"" height=""5"" rx=""1.5"" fill=""#3B82F6"" opacity=""0.25""/>" +
        @"<rect x=""14"" y=""12"" width=""4"" height=""2"" rx=""0.5"" fill=""#2563EB"" opacity=""0.5""/>" +
        // Chip pins (left/right)
        @"<rect x=""10.5"" y=""11.5"" width=""1.5"" height=""1"" rx=""0.5"" fill=""#3B82F6"" opacity=""0.3""/>" +
        @"<rect x=""10.5"" y=""13.5"" width=""1.5"" height=""1"" rx=""0.5"" fill=""#3B82F6"" opacity=""0.3""/>" +
        @"<rect x=""20"" y=""11.5"" width=""1.5"" height=""1"" rx=""0.5"" fill=""#3B82F6"" opacity=""0.3""/>" +
        @"<rect x=""20"" y=""13.5"" width=""1.5"" height=""1"" rx=""0.5"" fill=""#3B82F6"" opacity=""0.3""/>" +
        // Stand
        @"<rect x=""13"" y=""21"" width=""6"" height=""1.2"" rx=""0.6"" fill=""#3B82F6"" opacity=""0.15""/>" +
        @"<rect x=""11"" y=""23"" width=""10"" height=""1.2"" rx=""0.6"" fill=""#3B82F6"" opacity=""0.1""/>" +
        Badge(ext, "#2563EB");

    // ── AI Virtual Folder Icons ──

    public static string RenderAiIcon(string iconKey, int size)
    {
        var svg = iconKey switch
        {
            "ai-face" => AiFaceIcon(),
            "ai-scene" => AiSceneIcon(),
            "ai-object" => AiObjectIcon(),
            "ai-animal" => AiAnimalIcon(),
            "ai-location" => AiLocationIcon(),
            "ai-date" => AiDateIcon(),
            _ => AiFaceIcon()
        };
        return $@"<svg width=""{size}"" height=""{size}"" viewBox=""0 0 32 32"" xmlns=""http://www.w3.org/2000/svg"">{svg}</svg>";
    }

    static string AiFaceIcon() =>
        @"<circle cx=""16"" cy=""16"" r=""14"" fill=""#FDF2F8""/>" +
        @"<circle cx=""16"" cy=""12"" r=""4.5"" fill=""#EC4899""/>" +
        @"<path d=""M8 24c0-4.42 3.58-7 8-7s8 2.58 8 7"" fill=""#EC4899"" opacity=""0.6""/>";

    static string AiSceneIcon() =>
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#EFF6FF""/>" +
        @"<path d=""M2 22l8-8 6 6 4-4 10 10H6a4 4 0 01-4-4z"" fill=""#3B82F6"" opacity=""0.5""/>" +
        @"<circle cx=""10"" cy=""10"" r=""3"" fill=""#3B82F6"" opacity=""0.7""/>";

    static string AiObjectIcon() =>
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#FFFBEB""/>" +
        @"<path d=""M16 4l10 5.5v13L16 28 6 22.5v-13z"" fill=""#F59E0B"" opacity=""0.3""/>" +
        @"<path d=""M16 4l10 5.5L16 15 6 9.5z"" fill=""#F59E0B"" opacity=""0.6""/>" +
        @"<path d=""M16 15v13l10-5.5v-13z"" fill=""#F59E0B"" opacity=""0.45""/>";

    static string AiAnimalIcon() =>
        @"<circle cx=""16"" cy=""16"" r=""14"" fill=""#ECFDF5""/>" +
        @"<circle cx=""16"" cy=""16"" r=""10"" fill=""#10B981"" opacity=""0.2""/>" +
        @"<circle cx=""12"" cy=""13"" r=""1.5"" fill=""#10B981""/>" +
        @"<circle cx=""20"" cy=""13"" r=""1.5"" fill=""#10B981""/>" +
        @"<path d=""M12 19s1.5 2.5 4 2.5 4-2.5 4-2.5"" stroke=""#10B981"" stroke-width=""1.5"" fill=""none"" stroke-linecap=""round""/>";

    static string AiLocationIcon() =>
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#FEF2F2""/>" +
        @"<path d=""M16 4a8 8 0 018 8c0 6-8 16-8 16S8 18 8 12a8 8 0 018-8z"" fill=""#EF4444"" opacity=""0.6""/>" +
        @"<circle cx=""16"" cy=""12"" r=""3"" fill=""#FEF2F2""/>";

    static string AiDateIcon() =>
        @"<rect x=""2"" y=""2"" width=""28"" height=""28"" rx=""6"" fill=""#F5F3FF""/>" +
        @"<rect x=""5"" y=""6"" width=""22"" height=""20"" rx=""3"" fill=""#8B5CF6"" opacity=""0.2""/>" +
        @"<rect x=""5"" y=""6"" width=""22"" height=""6"" rx=""3"" fill=""#8B5CF6"" opacity=""0.5""/>" +
        @"<rect x=""11"" y=""4"" width=""2"" height=""4"" rx=""1"" fill=""#8B5CF6""/>" +
        @"<rect x=""19"" y=""4"" width=""2"" height=""4"" rx=""1"" fill=""#8B5CF6""/>" +
        @"<circle cx=""11"" cy=""17"" r=""1.2"" fill=""#8B5CF6"" opacity=""0.5""/>" +
        @"<circle cx=""16"" cy=""17"" r=""1.2"" fill=""#8B5CF6"" opacity=""0.5""/>" +
        @"<circle cx=""21"" cy=""17"" r=""1.2"" fill=""#8B5CF6"" opacity=""0.5""/>" +
        @"<circle cx=""11"" cy=""22"" r=""1.2"" fill=""#8B5CF6"" opacity=""0.3""/>" +
        @"<circle cx=""16"" cy=""22"" r=""1.2"" fill=""#8B5CF6"" opacity=""0.3""/>";

    public static string RenderGitBadge(GitFileStatus status, int iconSize)
    {
        var (color, letter) = status switch
        {
            GitFileStatus.Modified => ("#E2B714", "M"),
            GitFileStatus.Staged => ("#4CAF50", "M"),
            GitFileStatus.Added => ("#4CAF50", "A"),
            GitFileStatus.Deleted => ("#F44336", "D"),
            GitFileStatus.Renamed => ("#2196F3", "R"),
            GitFileStatus.Untracked => ("#9E9E9E", "?"),
            GitFileStatus.Conflicted => ("#FF5722", "!"),
            GitFileStatus.Unmodified => ("#4CAF50", ""),
            _ => ("", "")
        };
        if (status == GitFileStatus.Unmodified)
        {
            // Small green dot for tracked-but-clean files
            return $@"<circle cx=""26"" cy=""26"" r=""3"" fill=""{color}"" stroke=""rgba(255,255,255,0.9)"" stroke-width=""1""/>";
        }
        if (string.IsNullOrEmpty(letter)) return "";

        // viewBox is 0 0 32 32 — badge must be within this range
        var cx = 26;
        var cy = 26;
        var badgeSize = 5;
        var fontSize = 7;
        return $@"<circle cx=""{cx}"" cy=""{cy}"" r=""{badgeSize}"" fill=""{color}"" stroke=""rgba(255,255,255,0.9)"" stroke-width=""1.5""/><text x=""{cx}"" y=""{cy}"" fill=""#fff"" font-size=""{fontSize}"" font-weight=""bold"" text-anchor=""middle"" dominant-baseline=""central"">{letter}</text>";
    }

    public static string RenderGitFolderBadge(int iconSize)
    {
        var cx = 26;
        var cy = 26;
        var r = 3;
        return $@"<circle cx=""{cx}"" cy=""{cy}"" r=""{r}"" fill=""#E2B714"" stroke=""rgba(255,255,255,0.9)"" stroke-width=""1""/>";
    }
}