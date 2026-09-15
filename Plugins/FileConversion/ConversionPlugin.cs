using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.FileConversion.Plugin;

public sealed class ConversionPlugin : IFileActionPlugin
{
    private readonly FileConversionService _service = new();

    private FileConversionFormat Validate(PluginInvocation invocation)
    {
        if (invocation.Files.Length != 1 || invocation.Files[0].Source != "local" || invocation.Files[0].IsDirectory)
            throw new InvalidOperationException("文件转换仅支持一个本地文件。");
        var format = invocation.CommandId switch
        {
            "to-docx" => FileConversionFormat.Docx, "to-pdf" => FileConversionFormat.Pdf,
            "to-png" => FileConversionFormat.Png, "to-jpg" => FileConversionFormat.Jpg,
            _ => throw new InvalidOperationException("未知的转换命令。")
        };
        if (!_service.GetAvailableFormats(invocation.Files[0].Path).Contains(format))
            throw new InvalidOperationException("此文件不支持所选转换格式。");
        if (!File.Exists(invocation.Files[0].Path)) throw new FileNotFoundException("源文件不存在。");
        return format;
    }

    public async Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken)
    {
        var format = Validate(invocation);
        if (format is not (FileConversionFormat.Png or FileConversionFormat.Jpg)) return new();
        var size = await _service.GetImageSizeAsync(invocation.Files[0].Path, cancellationToken);
        return new(new("image-size", "转为 " + format.ToString().ToUpperInvariant(), size.Width, size.Height, format.ToString()));
    }

    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress,
        CancellationToken cancellationToken)
    {
        var format = Validate(invocation);
        ConversionImageSize? size = null;
        if (invocation.Parameters is { } parameters && parameters.TryGetValue("width", out var width) && parameters.TryGetValue("height", out var height))
        {
            size = new(width.GetInt32(), height.GetInt32());
            size.Validate();
        }
        progress.Report(new("正在转换 " + Path.GetFileName(invocation.Files[0].Path)));
        var result = await _service.ConvertAsync(new(invocation.Files[0].Path, format, size), invocation.WorkDirectory, cancellationToken);
        return new([new(result.OutputPath, Path.GetFileNameWithoutExtension(invocation.Files[0].Path) + "." + format.ToString().ToLowerInvariant())], result.Warnings.ToArray());
    }
}
