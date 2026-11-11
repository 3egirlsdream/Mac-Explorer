using System.Text;

namespace MacExplorer.Services.Preview;

internal static class TextPreviewReader
{
    internal const int MaxBytes = 4 * 1024 * 1024;

    internal static Task<string> ReadAsync(string path, CancellationToken token) => Task.Run(async () =>
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxBytes) throw new TextPreviewTooLargeException();
        using var output = new MemoryStream((int)stream.Length);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(0,
                   (int)Math.Min(buffer.Length, MaxBytes + 1L - output.Length)), token).ConfigureAwait(false)) > 0)
        {
            output.Write(buffer, 0, read);
            if (output.Length > MaxBytes) throw new TextPreviewTooLargeException();
        }
        token.ThrowIfCancellationRequested();
        return Decode(output.ToArray());
    }, token);

    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        return Encoding.UTF8.GetString(bytes);
    }
}

internal sealed class TextPreviewTooLargeException : IOException;
