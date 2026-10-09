using MacExplorer.PluginSdk;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.FileConversion.Plugin;

public sealed class ConversionPlugin : IFileActionPlugin
{
    private readonly FileConversionService _service;
    public ConversionPlugin() : this(null) { }
    public ConversionPlugin(Action<System.Diagnostics.ProcessStartInfo>? configureHelper)
        => _service = new FileConversionService(configureHelper);

    private static FileConversionFormat Format(PluginInvocation invocation) => invocation.CommandId switch
    {
        "to-docx" => FileConversionFormat.Docx, "to-pdf" => FileConversionFormat.Pdf,
        "to-png" => FileConversionFormat.Png, "to-jpg" => FileConversionFormat.Jpg,
        _ => throw new InvalidOperationException(L(invocation, "conversion.unknown_command"))
    };

    private void Validate(PluginInvocation invocation)
    {
        var format = Format(invocation);
        if (invocation.Files.Length == 0) throw new InvalidOperationException(L(invocation, "conversion.select_local_file"));
        if (format is FileConversionFormat.Png or FileConversionFormat.Jpg && invocation.Files.Length != 1)
            throw new InvalidOperationException(L(invocation, "conversion.image_single_file"));
        foreach (var file in invocation.Files)
        {
            if (file.Source != "local" || file.IsDirectory) throw new InvalidOperationException(L(invocation, "conversion.local_only"));
            if (!File.Exists(file.Path)) throw new FileNotFoundException(L(invocation, "conversion.source_missing"), file.Path);
            if (!_service.GetAvailableFormats(file.Path).Contains(format))
                throw new InvalidOperationException(L(invocation, "conversion.unsupported_file", Path.GetFileName(file.Path)));
        }
    }

    public async Task<PluginPreparation> PrepareAsync(PluginInvocation invocation, CancellationToken cancellationToken)
    {
        using var culture = ConversionLocalization.Use(invocation.Culture);
        Validate(invocation);
        var format = Format(invocation);
        if (format is not (FileConversionFormat.Png or FileConversionFormat.Jpg)) return new();
        var size = await _service.GetImageSizeAsync(invocation.Files[0].Path, cancellationToken);
        return new(new("image-size", L(invocation, "conversion.to_format", format.ToString().ToUpperInvariant()), size.Width, size.Height, format.ToString()));
    }

    public async Task<PluginResult> ExecuteAsync(PluginInvocation invocation, IProgress<PluginProgress> progress,
        CancellationToken cancellationToken)
    {
        using var culture = ConversionLocalization.Use(invocation.Culture);
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
            progress.Report(new(batch ? L(invocation, "conversion.processing_batch", name, index + 1, invocation.Files.Length) : L(invocation, "conversion.processing", name),
                index * 100.0 / invocation.Files.Length) { ShowInTaskPanel = batch });
            // 每个文件使用独立工作子目录，避免同名结果互相覆盖。
            var result = await _service.ConvertAsync(new(file.Path, format, size),
                Path.Combine(invocation.WorkDirectory, index.ToString()), cancellationToken);
            outputs.Add(new(result.OutputPath, Path.GetFileNameWithoutExtension(file.Path) + "." + extension) { SourcePath = file.Path });
            warnings.AddRange(result.Warnings);
        }
        return new(outputs.ToArray(), warnings.Distinct().ToArray());
    }

    private static string L(PluginInvocation invocation, string key, params object?[] arguments)
    {
        var english = (invocation.Culture ?? "zh-CN").StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var template = key switch
        {
            "conversion.unknown_command" => english ? "Unknown conversion command." : "未知的转换命令。",
            "conversion.select_local_file" => english ? "Select at least one local file." : "请至少选择一个本地文件。",
            "conversion.image_single_file" => english ? "Image conversion processes one file at a time." : "图像转换每次只能处理一个文件。",
            "conversion.local_only" => english ? "Conversion supports local files only." : "文件转换仅支持本地文件。",
            "conversion.source_missing" => english ? "The source file does not exist." : "源文件不存在。",
            "conversion.unsupported_file" => english ? "This file does not support the selected format: {0}" : "此文件不支持所选转换格式：{0}",
            "conversion.to_format" => english ? "Convert to {0}" : "转为 {0}",
            "conversion.processing" => english ? "Converting {0}" : "正在转换 {0}",
            "conversion.processing_batch" => english ? "Converting {0} ({1}/{2})" : "正在转换 {0}（{1}/{2}）",
            _ => key
        };
        return arguments.Length == 0 ? template : string.Format(System.Globalization.CultureInfo.GetCultureInfo(english ? "en-US" : "zh-CN"), template, arguments);
    }
}
