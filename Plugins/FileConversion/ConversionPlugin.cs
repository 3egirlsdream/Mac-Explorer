using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.FileConversion.Plugin;

public sealed class ConversionPlugin : IFileActionPlugin
{
    private readonly FileConversionService _service = new();

    private static FileConversionFormat Format(PluginInvocation invocation) => invocation.CommandId switch
    {
        "to-docx" => FileConversionFormat.Docx, "to-pdf" => FileConversionFormat.Pdf,
        "to-png" => FileConversionFormat.Png, "to-jpg" => FileConversionFormat.Jpg,
        _ => throw new InvalidOperationException("未知的转换命令。")
    };

    private void Validate(PluginInvocation invocation)
    {
        var format = Format(invocation);
        if (invocation.Files.Length == 0) throw new InvalidOperationException("请至少选择一个本地文件。");
        if (format is FileConversionFormat.Png or FileConversionFormat.Jpg && invocation.Files.Length != 1)
            throw new InvalidOperationException("图像转换每次只能处理一个文件。");
        foreach (var file in invocation.Files)
        {
            if (file.Source != "local" || file.IsDirectory) throw new InvalidOperationException("文件转换仅支持本地文件。");
            if (!File.Exists(file.Path)) throw new FileNotFoundException("源文件不存在。", file.Path);
            if (!_service.GetAvailableFormats(file.Path).Contains(format))
                throw new InvalidOperationException("此文件不支持所选转换格式：" + Path.GetFileName(file.Path));
        }
    }

    public async Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken)
    {
        Validate(invocation);
        var format = Format(invocation);
        if (format is not (FileConversionFormat.Png or FileConversionFormat.Jpg)) return new();
        var size = await _service.GetImageSizeAsync(invocation.Files[0].Path, cancellationToken);
        return new(new("image-size", "转为 " + format.ToString().ToUpperInvariant(), size.Width, size.Height, format.ToString()));
    }

    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress,
        CancellationToken cancellationToken)
    {
        Validate(invocation);
        var format = Format(invocation);
        ConversionImageSize? size = null;
        if (invocation.Parameters is { } parameters && parameters.TryGetValue("width", out var width) && parameters.TryGetValue("height", out var height))
        {
            size = new(width.GetInt32(), height.GetInt32());
            size.Validate();
        }
        var extension = format.ToString().ToLowerInvariant();
        var batch = invocation.Files.Length > 1;
        var outputs = new List<PluginOutput>();
        var warnings = new List<string>();
        for (var index = 0; index < invocation.Files.Length; index++)
        {
            var file = invocation.Files[index];
            var name = Path.GetFileName(file.Path);
            progress.Report(new(batch ? $"正在转换 {name}（{index + 1}/{invocation.Files.Length}）" : "正在转换 " + name,
                index * 100.0 / invocation.Files.Length) { ShowInTaskPanel = batch });
            // 每个文件使用独立工作子目录，避免同名结果互相覆盖。
            var result = await _service.ConvertAsync(new(file.Path, format, size),
                Path.Combine(invocation.WorkDirectory, index.ToString()), cancellationToken);
            outputs.Add(new(result.OutputPath, Path.GetFileNameWithoutExtension(file.Path) + "." + extension) { SourcePath = file.Path });
            warnings.AddRange(result.Warnings);
        }
        return new(outputs.ToArray(), warnings.Distinct().ToArray());
    }
}
