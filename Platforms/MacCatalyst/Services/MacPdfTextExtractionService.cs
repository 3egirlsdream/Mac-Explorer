using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public sealed class MacPdfTextExtractionService : IPdfTextExtractionService
{
    private readonly string _helperPath;
    private readonly TimeSpan _idleTimeout;

    public MacPdfTextExtractionService()
        : this(Path.Combine(AppContext.BaseDirectory, "MacExplorer.ImageAnalysis"), TimeSpan.FromSeconds(60)) { }

    internal MacPdfTextExtractionService(string helperPath, TimeSpan idleTimeout)
        => (_helperPath, _idleTimeout) = (helperPath, idleTimeout);

    public async Task<IReadOnlyList<RecognizedText>> ExtractAsync(string filePath,
        IProgress<PdfAnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_helperPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--pdf");
        start.ArgumentList.Add(filePath);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 PDF 分析程序。");
        using var timeout = new CancellationTokenSource(_idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;
        var error = new StringBuilder();
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var progressTask = ReadProgressAsync();
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(token), progressTask, outputTask).ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException(error.Length == 0 ? "PDF 文字提取失败。" : error.ToString().Trim());
            var result = JsonSerializer.Deserialize<ImageAnalysisResult>(await outputTask.ConfigureAwait(false))
                ?? throw new IOException("PDF 分析程序返回了空结果。");
            return result.RecognizedTexts;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("PDF 分析超过 60 秒没有页面进展，已停止。");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }

        async Task ReadProgressAsync()
        {
            var lastCompleted = -1;
            while (await process.StandardError.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                const string prefix = "PDF_PROGRESS ";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var update = JsonSerializer.Deserialize<PdfAnalysisProgress>(line[prefix.Length..]);
                    if (update.TotalPages > 0 && update.CompletedPages > lastCompleted
                        && update.CompletedPages <= update.TotalPages)
                    {
                        lastCompleted = update.CompletedPages;
                        timeout.CancelAfter(_idleTimeout);
                        progress?.Report(update);
                    }
                }
                else if (error.Length < 8192)
                    error.AppendLine(line);
            }
        }
    }
}
