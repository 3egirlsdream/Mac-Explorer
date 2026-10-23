using System.Diagnostics;
using System.Text;
using System.Text.Json;
using MacExplorer.Models;
using MacExplorer.Services;
using MacExplorer.Services.Impl;

namespace MacExplorer.Platforms.MacCatalyst.Services;

public sealed class MacPdfTextExtractionService : IPdfTextExtractionService
{
    private readonly string _helperPath;
    private readonly TimeSpan _idleTimeout;
    private readonly SemaphoreSlim _extractionGate = new(1, 1);

    public MacPdfTextExtractionService()
        : this(Path.Combine(RuntimePaths.BundleExecutableDirectory, "MacExplorer.ImageAnalysis"), TimeSpan.FromSeconds(60)) { }

    internal MacPdfTextExtractionService(string helperPath, TimeSpan idleTimeout)
        => (_helperPath, _idleTimeout) = (helperPath, idleTimeout);

    public async Task<IReadOnlyList<RecognizedText>> ExtractAsync(string filePath,
        IProgress<PdfAnalysisProgress>? progress = null, CancellationToken ct = default)
    {
        await _extractionGate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ExtractCoreAsync(filePath, progress, ct).ConfigureAwait(false); }
        finally { _extractionGate.Release(); }
    }

    private async Task<IReadOnlyList<RecognizedText>> ExtractCoreAsync(string filePath,
        IProgress<PdfAnalysisProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_helperPath)
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--pdf");
        start.ArgumentList.Add(filePath);
        DirectoryAccess.Current.ConfigureHelper(start, filePath);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 PDF 分析程序。");
        using var timeout = new CancellationTokenSource(_idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var token = linked.Token;
        var error = new StringBuilder();
        // Decode incrementally: retaining the entire JSON string duplicates a large
        // document's text and keywords alongside the deserialized object graph.
        var outputTask = JsonSerializer.DeserializeAsync<ImageAnalysisResult>(process.StandardOutput.BaseStream,
            cancellationToken: token).AsTask();
        var progressTask = ReadProgressAsync();
        try
        {
            var pending = new List<Task> { process.WaitForExitAsync(token), progressTask, outputTask };
            while (pending.Count > 0)
            {
                var finished = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(finished);
                // A failed parser/progress reader must terminate the helper now,
                // rather than waiting while it blocks on an undrained pipe.
                await finished.ConfigureAwait(false);
            }
            if (process.ExitCode != 0)
                throw new IOException(error.Length == 0 ? "PDF 文字提取失败。" : error.ToString().Trim());
            var result = await outputTask.ConfigureAwait(false)
                ?? throw new IOException("PDF 分析程序返回了空结果。");
            return result.RecognizedTexts;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("PDF 分析超过 60 秒没有页面进展，已停止。");
        }
        catch (JsonException ex)
        {
            throw new IOException(error.Length == 0 ? LocalizationText.Get("pdf.invalid_result") : error.ToString().Trim(), ex);
        }
        finally
        {
            linked.Cancel();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await Task.WhenAll(progressTask, outputTask).ConfigureAwait(false); }
            catch { /* The primary failure/cancellation is already being propagated. */ }
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
