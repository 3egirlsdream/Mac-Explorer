using System.Text;
using MacExplorer.Platforms.MacCatalyst.Services;
using MacExplorer.Services;
using MacExplorer.Services.Impl;
using SkiaSharp;
using Xunit;

namespace MacExplorer.Tests;

public sealed class StoreSandboxNativePipelineTests
{
    [Fact]
    public async Task AuthorizedNativeHelpersConvertDocumentsAndExtractPdfAndImageText()
    {
        Assert.SkipWhen(!DistributionChannel.IsAppStore || DirectoryAccess.Current.AuthorizedRoots.Count == 0,
            "Run inside a signed Store sandbox test bundle after selecting its isolated root.");
        var root = Path.Combine(RuntimePaths.TestRoot!, "native-pipeline");
        DirectoryAccess.Current.EnsureAccess(root);
        Directory.CreateDirectory(root);
        var token = TestContext.Current.CancellationToken;
        var source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "Native invoice 4826\nSandbox document conversion.", token);
        var conversion = new FileConversionService(start => DirectoryAccess.Current.ConfigureHelper(start));
        var docx = await conversion.ConvertAsync(new(source, FileConversionFormat.Docx), root, token);
        var pdf = await conversion.ConvertAsync(new(docx.OutputPath, FileConversionFormat.Pdf), root, token);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await File.ReadAllBytesAsync(pdf.OutputPath, token))[..4]));
        var extractor = new MacPdfTextExtractionService(Path.Combine(RuntimePaths.BundleExecutableDirectory, "MacExplorer.ImageAnalysis"), TimeSpan.FromSeconds(60));
        var text = await extractor.ExtractAsync(pdf.OutputPath, ct: token);
        Assert.Contains(text, item => item.Text.Contains("4826", StringComparison.Ordinal));

        var image = Path.Combine(root, "ocr.png");
        using (var bitmap = new SKBitmap(900, 180))
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Arial"), 68))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText("Native invoice 4826", 25, 110, font, paint);
            using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(image);
            png.SaveTo(file);
        }
        var analysis = new MacImageAnalysisService();
        var result = await analysis.AnalyzeImageAsync(image, ct: token);
        Assert.Contains(result.RecognizedTexts, item => item.Text.Contains("4826", StringComparison.Ordinal));
        Assert.Equal("Native invoice 4826\nSandbox document conversion.", await File.ReadAllTextAsync(source, token));
    }
}
