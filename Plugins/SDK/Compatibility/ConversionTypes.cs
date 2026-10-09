namespace MacExplorer.Services;

public enum FileConversionFormat { Docx, Pdf, Png, Jpg }
public record ConversionImageSize(int Width, int Height)
{
    public void Validate()
    {
        if (Width is < 1 or > 8192 || Height is < 1 or > 8192 || (long)Width * Height > 32_000_000)
            throw new InvalidOperationException(System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? "Dimensions must be 1–8192 pixels, with no more than 32 million pixels in total."
                : "尺寸须在 1–8192 像素之间，且总像素不超过 3200 万。");
    }
}
public record FileConversionRequest(string SourcePath, FileConversionFormat Format, ConversionImageSize? ImageSize = null);
public record FileConversionResult(string OutputPath, IReadOnlyList<string> Warnings);

public interface IFileConversionService
{
    IReadOnlyList<FileConversionFormat> GetAvailableFormats(string path);
    Task<ConversionImageSize> GetImageSizeAsync(string path, CancellationToken cancellationToken = default);
    Task<FileConversionResult> ConvertAsync(FileConversionRequest request, CancellationToken cancellationToken = default);
}
