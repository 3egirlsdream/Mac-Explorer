using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml.Linq;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

/// <summary>
/// Mac Catalyst implementation of IMetadataService.
/// Uses stat, mdls, and xattr commands to gather file metadata.
/// </summary>
public class MacMetadataService : IMetadataService
{
    private const string FinderTagsAttribute = "com.apple.metadata:_kMDItemUserTags";

    public async Task<FileMetadata> GetMetadataAsync(string path)
    {
        return await Task.Factory.StartNew(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
            string name = Path.GetFileName(path);
            long size = 0;
            string formattedSize = "--";
            DateTime created = default, lastModified = default, lastAccessed = default;
            string permissions = "";
            string owner = "--", group = "--";
            string contentType = "";
            string kind = "";
            List<string> extendedAttrs = [];
            List<string> tags = [];

            try
            {
                var info = new FileInfo(path);
                var isDir = info.Attributes.HasFlag(FileAttributes.Directory);

                size = isDir ? 0 : info.Length;
                formattedSize = isDir ? "--" : FormatSize(info.Length);
                created = info.CreationTime;
                lastModified = info.LastWriteTime;
                lastAccessed = info.LastAccessTime;
                permissions = GetPermissionsString(info.Attributes);

                (owner, group) = GetOwnerGroup(path);
                contentType = GetContentType(path);
                kind = GetKindDescription(path, isDir, info.Extension);
                extendedAttrs = GetExtendedAttributes(path);
                tags = GetTags(path);
            }
            catch
            {
                // Return partial metadata on error
            }

            ImageMetadata? imageInfo = null;
            if (IsImageContentType(contentType))
            {
                try { imageInfo = GetImageMetadata(path); }
                catch { }
            }

            return new FileMetadata
            {
                FullPath = path,
                Name = name,
                Size = size,
                FormattedSize = formattedSize,
                Created = created,
                LastModified = lastModified,
                LastAccessed = lastAccessed,
                Permissions = permissions,
                Owner = owner,
                Group = group,
                ContentType = contentType,
                Kind = kind,
                ExtendedAttributes = extendedAttrs,
                Tags = tags,
                ImageInfo = imageInfo
            };
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).ConfigureAwait(false);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unitIndex = 0;
        double size = bytes;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return $"{size:0.#} {units[unitIndex]}";
    }

    private static string GetPermissionsString(FileAttributes attributes)
    {
        var perms = "";
        perms += (attributes & FileAttributes.ReadOnly) != 0 ? "r-" : "rw";
        perms += (attributes & FileAttributes.Hidden) != 0 ? "h" : "-";
        perms += (attributes & FileAttributes.System) != 0 ? "s" : "-";
        return perms;
    }

    private static (string owner, string group) GetOwnerGroup(string path)
    {
        try
        {
            var output = RunCommand("stat", "-f", "%Su %Sg", path);
            var parts = output.Trim().Trim('"').Split(' ');
            if (parts.Length >= 2)
                return (parts[0], parts[1]);
        }
        catch { }
        return ("--", "--");
    }

    private static string GetContentType(string path)
    {
        try
        {
            var output = RunCommand("mdls", "-name", "kMDItemContentType", path);
            // Parse: kMDItemContentType = "public.folder"
            var idx = output.IndexOf('=');
            if (idx >= 0)
            {
                var value = output[(idx + 1)..].Trim();
                return value.Trim('"');
            }
        }
        catch { }
        return "";
    }

    private static string GetKindDescription(string path, bool isDir, string extension)
    {
        if (isDir) return "文件夹";

        try
        {
            var output = RunCommand("mdls", "-name", "kMDItemKind", path);
            var idx = output.IndexOf('=');
            if (idx >= 0)
            {
                var value = output[(idx + 1)..].Trim().Trim('"');
                if (!string.IsNullOrEmpty(value)) return value;
            }
        }
        catch { }

        // Fallback: map extension to kind
        return extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".tiff" or ".bmp" or ".webp" => "图像",
            ".mp4" or ".mov" or ".avi" or ".mkv" => "影片",
            ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" => "音频",
            ".pdf" => "PDF 文档",
            ".doc" or ".docx" => "Word 文档",
            ".xls" or ".xlsx" => "Excel 表格",
            ".ppt" or ".pptx" => "演示文稿",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" or ".dmg" => "归档",
            ".app" => "应用程序",
            ".txt" or ".md" or ".log" => "文本",
            _ => extension.TrimStart('.').ToUpperInvariant() + " 文件"
        };
    }

    private static List<string> GetExtendedAttributes(string path)
    {
        var attrs = new List<string>();
        try
        {
            var output = RunCommand("xattr", path);
            if (!string.IsNullOrWhiteSpace(output))
            {
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var attr = line.Trim();
                    if (!string.IsNullOrEmpty(attr))
                        attrs.Add(attr);
                }
            }
        }
        catch { }
        return attrs;
    }

    private static List<string> GetTags(string path)
    {
        try
        {
            var xattrTags = GetFinderTagsFromXattr(path);
            if (xattrTags.Count > 0)
                return xattrTags;

            var output = RunCommand("mdls", "-name", "kMDItemUserTags", path);
            return ParseFinderTagsFromMdls(output);
        }
        catch { }
        return [];
    }

    private static List<string> GetFinderTagsFromXattr(string path)
    {
        var hex = RunCommand("xattr", "-px", FinderTagsAttribute, path);
        return ParseFinderTagsFromBinaryPlistHex(hex);
    }

    private static List<string> ParseFinderTagsFromBinaryPlistHex(string hex)
    {
        hex = new string(hex.Where(Uri.IsHexDigit).ToArray());
        if (string.IsNullOrWhiteSpace(hex) || hex.Length % 2 != 0)
            return [];

        var tempBase = Path.Combine(Path.GetTempPath(), $"macexplorer-read-tags-{Guid.NewGuid():N}");
        var binaryPath = tempBase + ".bin";
        var xmlPath = tempBase + ".plist";

        try
        {
            File.WriteAllBytes(binaryPath, Convert.FromHexString(hex));
            RunCommand("plutil", "-convert", "xml1", "-o", xmlPath, binaryPath);
            if (!File.Exists(xmlPath)) return [];

            var document = XDocument.Load(xmlPath);
            return document.Descendants("string")
                .Select(node => NormalizeFinderTagForDisplay(node.Value))
                .Where(t => !string.IsNullOrEmpty(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
        finally
        {
            TryDeleteFile(binaryPath);
            TryDeleteFile(xmlPath);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }

    private static List<string> ParseFinderTagsFromMdls(string output)
    {
        var idx = output.IndexOf('=');
        if (idx < 0) return [];

        var value = output[(idx + 1)..].Trim();
        if (value is "(null)" or "null") return [];
        if (value.StartsWith('(') && value.EndsWith(')'))
            value = value[1..^1];

        return SplitMdlsArrayItems(value)
            .Select(NormalizeFinderTagForDisplay)
            .Where(t => !string.IsNullOrEmpty(t) && !string.Equals(t, "null", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitMdlsArrayItems(string value)
    {
        var items = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var previous = '\0';

        foreach (var c in value)
        {
            if (c == '"' && previous != '\\')
                inQuote = !inQuote;

            if (c == ',' && !inQuote)
            {
                AddMdlsArrayItem(items, current);
            }
            else
            {
                current.Append(c);
            }

            previous = c;
        }

        AddMdlsArrayItem(items, current);
        return items;
    }

    private static void AddMdlsArrayItem(List<string> items, StringBuilder current)
    {
        var item = current.ToString().Trim().TrimEnd(',').Trim().Trim('"');
        current.Clear();
        if (!string.IsNullOrWhiteSpace(item))
            items.Add(item);
    }

    private static string NormalizeFinderTagForDisplay(string tag)
    {
        tag = tag.Trim().Replace("\\012", "\n", StringComparison.Ordinal);
        var suffixStart = tag.LastIndexOf('\n');
        if (suffixStart >= 0 && int.TryParse(tag[(suffixStart + 1)..], out _))
            tag = tag[..suffixStart];

        return tag.Trim();
    }

    private static bool IsImageContentType(string contentType)
    {
        return contentType.Contains("image") || contentType.Contains("public.jpeg")
            || contentType.Contains("public.png") || contentType.Contains("public.tiff")
            || contentType.Contains("public.heic") || contentType.Contains("public.heif")
            || contentType.Contains("com.adobe.raw-image") || contentType.Contains("public.camera-raw-image");
    }

    private static ImageMetadata? GetImageMetadata(string path)
    {
        var output = RunCommand("mdls", path);
        if (string.IsNullOrWhiteSpace(output)) return null;

        var props = ParseMdlsOutput(output);
        if (props.Count == 0) return null;

        return BuildImageMetadata(props);
    }

    private static Dictionary<string, string> ParseMdlsOutput(string output)
    {
        var props = new Dictionary<string, string>();
        var lines = output.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var eqIdx = line.IndexOf('=');
            if (eqIdx < 0) continue;

            var key = line[..eqIdx].Trim();
            var value = line[(eqIdx + 1)..].Trim();

            if (value == "(null)") continue;

            // Handle multi-line parenthesized arrays
            if (value == "(")
            {
                var arrayValues = new List<string>();
                i++;
                while (i < lines.Length)
                {
                    var arrLine = lines[i].Trim();
                    if (arrLine == ")")
                        break;
                    var item = arrLine.TrimEnd(',').Trim().Trim('"');
                    if (!string.IsNullOrEmpty(item))
                        arrayValues.Add(item);
                    i++;
                }
                if (arrayValues.Count > 0)
                    props[key] = string.Join(", ", arrayValues);
                continue;
            }

            value = value.Trim('"');
            if (!string.IsNullOrEmpty(value))
                props[key] = value;
        }

        return props;
    }

    private static ImageMetadata BuildImageMetadata(Dictionary<string, string> props)
    {
        return new ImageMetadata
        {
            PixelWidth = GetInt(props, "kMDItemPixelWidth"),
            PixelHeight = GetInt(props, "kMDItemPixelHeight"),
            DpiWidth = GetInt(props, "kMDItemResolutionWidthDPI"),
            DpiHeight = GetInt(props, "kMDItemResolutionHeightDPI"),
            BitsPerSample = GetInt(props, "kMDItemBitsPerSample"),
            HasAlpha = GetInt(props, "kMDItemHasAlphaChannel") == 1,
            ColorSpace = GetString(props, "kMDItemColorSpace"),
            ProfileName = GetString(props, "kMDItemProfileName"),
            CameraMake = GetString(props, "kMDItemAcquisitionMake"),
            CameraModel = GetString(props, "kMDItemAcquisitionModel"),
            LensModel = GetString(props, "kMDItemLensModel"),
            FocalLength = FormatFocalLength(GetDouble(props, "kMDItemFocalLength")),
            Aperture = FormatAperture(GetDouble(props, "kMDItemFNumber")),
            ExposureTime = FormatExposureTime(GetDouble(props, "kMDItemExposureTimeSeconds")),
            IsoSpeed = FormatIso(GetString(props, "kMDItemISOSpeed")),
            WhiteBalance = FormatWhiteBalance(GetInt(props, "kMDItemWhiteBalance")),
            Flash = FormatFlash(GetInt(props, "kMDItemFlashOnOff")),
            ExposureProgram = FormatExposureProgram(GetInt(props, "kMDItemExposureProgram")),
            MeteringMode = FormatMeteringMode(GetInt(props, "kMDItemMeteringMode")),
            Latitude = GetDouble(props, "kMDItemLatitude"),
            Longitude = GetDouble(props, "kMDItemLongitude"),
            Altitude = FormatAltitude(GetDouble(props, "kMDItemAltitude")),
            PhotoTakenDate = GetDateTime(props, "kMDItemContentCreationDate"),
            AllProperties = props
        };
    }

    private static string GetString(Dictionary<string, string> props, string key)
        => props.TryGetValue(key, out var v) ? v : string.Empty;

    private static int GetInt(Dictionary<string, string> props, string key)
    {
        if (props.TryGetValue(key, out var v) && int.TryParse(v, CultureInfo.InvariantCulture, out var i))
            return i;
        return 0;
    }

    private static double GetDouble(Dictionary<string, string> props, string key)
    {
        if (props.TryGetValue(key, out var v) && double.TryParse(v, CultureInfo.InvariantCulture, out var d))
            return d;
        return 0;
    }

    private static DateTime? GetDateTime(Dictionary<string, string> props, string key)
    {
        if (props.TryGetValue(key, out var v) &&
            DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return null;
    }

    private static string FormatFocalLength(double mm)
        => mm > 0 ? $"{mm:0.##} mm" : string.Empty;

    private static string FormatAperture(double fNumber)
        => fNumber > 0 ? $"f/{fNumber:0.#}" : string.Empty;

    private static string FormatExposureTime(double seconds)
    {
        if (seconds <= 0) return string.Empty;
        if (seconds >= 1) return $"{seconds:0.#} s";
        var denom = (int)Math.Round(1.0 / seconds);
        return $"1/{denom} s";
    }

    private static string FormatIso(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        return raw.Contains(',') ? raw.Split(',')[0].Trim() : raw;
    }

    private static string FormatWhiteBalance(int value) => value switch
    {
        0 => "自动",
        1 => "手动",
        _ when value > 0 => value.ToString(),
        _ => string.Empty
    };

    private static string FormatFlash(int value) => value switch
    {
        0 => "关闭",
        1 => "开启",
        _ when value > 0 => value.ToString(),
        _ => string.Empty
    };

    private static string FormatExposureProgram(int value) => value switch
    {
        0 => "未定义",
        1 => "手动",
        2 => "自动",
        3 => "光圈优先",
        4 => "快门优先",
        5 => "创意模式",
        6 => "运动模式",
        7 => "人像模式",
        8 => "风景模式",
        _ when value > 0 => value.ToString(),
        _ => string.Empty
    };

    private static string FormatMeteringMode(int value) => value switch
    {
        0 => "未知",
        1 => "平均",
        2 => "中央重点",
        3 => "点测光",
        4 => "多点测光",
        5 => "多区测光",
        6 => "局部测光",
        _ when value > 0 => value.ToString(),
        _ => string.Empty
    };

    private static string FormatAltitude(double meters)
        => meters != 0 ? $"{meters:0.#} m" : string.Empty;

    private static string RunCommand(string command, params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(command)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
                psi.ArgumentList.Add(argument);

            using var process = Process.Start(psi);
            if (process == null) return string.Empty;
            TrySetBelowNormalPriority(process);
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch { }
                return string.Empty;
            }

            try { errorTask.Wait(100); }
            catch { }
            return outputTask.GetAwaiter().GetResult();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TrySetBelowNormalPriority(Process process)
    {
        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch
        {
        }
    }
}
