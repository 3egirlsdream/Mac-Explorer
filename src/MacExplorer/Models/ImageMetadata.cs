namespace MacExplorer.Models;

public class ImageMetadata
{
    // Image dimensions
    public int PixelWidth { get; init; }
    public int PixelHeight { get; init; }
    public int DpiWidth { get; init; }
    public int DpiHeight { get; init; }
    public int BitsPerSample { get; init; }
    public bool HasAlpha { get; init; }
    public string ColorSpace { get; init; } = string.Empty;
    public string ProfileName { get; init; } = string.Empty;

    // Camera / EXIF
    public string CameraMake { get; init; } = string.Empty;
    public string CameraModel { get; init; } = string.Empty;
    public string LensModel { get; init; } = string.Empty;
    public string FocalLength { get; init; } = string.Empty;
    public string Aperture { get; init; } = string.Empty;
    public string ExposureTime { get; init; } = string.Empty;
    public string IsoSpeed { get; init; } = string.Empty;
    public string WhiteBalance { get; init; } = string.Empty;
    public string Flash { get; init; } = string.Empty;
    public string ExposureProgram { get; init; } = string.Empty;
    public string MeteringMode { get; init; } = string.Empty;

    // Date
    public DateTime? PhotoTakenDate { get; init; }

    // GPS
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public string Altitude { get; init; } = string.Empty;

    // All raw mdls properties for the "所有属性" expandable section
    public Dictionary<string, string> AllProperties { get; init; } = [];
}
