using MacExplorer.Models;

namespace MacExplorer.Services.Impl;

/// <summary>Application-wide PDF scheduling. The gate covers both extraction and commit.</summary>
public sealed class PdfAnalysisService(IPdfTextExtractionService extractor, IAiTagService tags) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lock = new();
    private CancellationTokenSource _enabled = new();

    public void CancelPending()
    {
        lock (_lock)
        {
            _enabled.Cancel();
            _enabled.Dispose();
            _enabled = new CancellationTokenSource();
        }
    }

    public async Task AnalyzeAsync(string path, long modifiedTicks,
        IProgress<PdfAnalysisProgress>? progress, CancellationToken ct)
    {
        CancellationTokenSource linked;
        lock (_lock)
            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _enabled.Token);
        using (linked)
        {
            var token = linked.Token;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                // Another tab may have finished while this request waited for the gate.
                if (await tags.IsFileAnalyzedAsync(path, modifiedTicks).ConfigureAwait(false)) return;
                EnsureUnchanged(path, modifiedTicks);
                var texts = await extractor.ExtractAsync(path, progress, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                EnsureUnchanged(path, modifiedTicks);
                await tags.SaveAnalysisResultAsync(path, modifiedTicks,
                    new ImageAnalysisResult { RecognizedTexts = texts.ToList() }, token).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }

    private static void EnsureUnchanged(string path, long modifiedTicks)
    {
        if (!File.Exists(path) || File.GetLastWriteTime(path).Ticks != modifiedTicks)
            throw new IOException("文件在分析期间已修改或删除，请重新浏览文件夹后重试。");
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _enabled.Cancel();
            _enabled.Dispose();
        }
    }
}
