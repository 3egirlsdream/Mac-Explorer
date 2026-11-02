using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using Svg.Skia;

var source = Path.GetFullPath(args.Length > 0 ? args[0] : "Assets/BackgroundThemes");
var output = Path.Combine(source, "Previews");
Directory.CreateDirectory(output);
var manifest = new List<object>();
var svgWatch = Stopwatch.StartNew();
foreach (var path in Directory.GetFiles(source, "*.svg").Order())
{
    using var svg = new SKSvg();
    svg.Load(path);
    var picture = svg.Picture ?? throw new InvalidDataException(path);
    var bounds = picture.CullRect;
    var scale = Math.Min(640 / bounds.Width, 192 / bounds.Height);
    using var bitmap = new SKBitmap(new SKImageInfo((int)Math.Ceiling(bounds.Width * scale),
        (int)Math.Ceiling(bounds.Height * scale), SKColorType.Bgra8888, SKAlphaType.Premul));
    using (var canvas = new SKCanvas(bitmap))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        canvas.Translate(-bounds.Left, -bounds.Top);
        canvas.DrawPicture(picture);
    }
    var destination = Path.Combine(output, Path.GetFileNameWithoutExtension(path) + ".png");
    using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    File.WriteAllBytes(destination, encoded.ToArray());
    manifest.Add(new { asset = Path.GetFileName(path), sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        width = bitmap.Width, height = bitmap.Height });
}
svgWatch.Stop();
File.WriteAllText(Path.Combine(output, "sources.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }) + "\n");
var pngWatch = Stopwatch.StartNew();
for (var repeat = 0; repeat < 20; repeat++)
    foreach (var path in Directory.GetFiles(output, "*.png"))
        using (var image = SKBitmap.Decode(path)) { }
pngWatch.Stop();
Console.WriteLine(JsonSerializer.Serialize(new { count = manifest.Count, svgMilliseconds = svgWatch.Elapsed.TotalMilliseconds,
    pngMilliseconds = pngWatch.Elapsed.TotalMilliseconds / 20, totalBytes = Directory.GetFiles(output, "*.png").Sum(p => new FileInfo(p).Length) }));
