using System.Text;
using MacExplorer.Services.Impl;

namespace MacExplorer.Models;

public sealed record TextContentPage(string Text, long NextOffset, bool HasMore)
{
    public const int MaxUtf8Bytes = 50 * 1024;

    internal static TextContentPage Read(IEnumerable<TextReader> segments, long offset, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        var buffer = new char[MaxUtf8Bytes + 2];
        var chunk = new char[4096];
        var remaining = offset;
        var count = 0;
        var first = true;
        foreach (var reader in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (!first) Append("\n");
            first = false;
            while (count < buffer.Length)
            {
                ct.ThrowIfCancellationRequested();
                var read = reader.Read(chunk, 0, remaining > chunk.Length
                    ? chunk.Length : Math.Min(chunk.Length, (int)remaining + buffer.Length - count));
                if (read == 0) break;
                Append(chunk.AsSpan(0, read));
            }
            if (count == buffer.Length) break;
        }
        if (remaining > 0)
            throw new ArgumentOutOfRangeException(nameof(offset), LocalizationText.Get("content.offset_out_of_range"));

        var length = 0;
        var bytes = 0;
        foreach (var rune in buffer.AsSpan(0, count).EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxUtf8Bytes) break;
            bytes += rune.Utf8SequenceLength;
            length += rune.Utf16SequenceLength;
        }
        return new(new string(buffer, 0, length), offset + length, length < count);

        void Append(ReadOnlySpan<char> text)
        {
            var skip = (int)Math.Min(remaining, text.Length);
            remaining -= skip;
            text = text[skip..];
            var take = Math.Min(text.Length, buffer.Length - count);
            text[..take].CopyTo(buffer.AsSpan(count));
            count += take;
        }
    }
}
