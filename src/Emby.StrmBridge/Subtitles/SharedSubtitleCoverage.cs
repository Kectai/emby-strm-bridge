using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.StrmBridge.Subtitles;

internal readonly struct SharedSubtitleExtent
{
    internal readonly long LastStart;
    internal readonly long LastEnd;
    internal SharedSubtitleExtent(long lastStart, long lastEnd) { LastStart = lastStart; LastEnd = lastEnd; }
}

// Cue end is not demux progress: an early sign can remain visible for an hour.
// Index timestamps incrementally; keep only an incomplete UTF-8 line and two
// maxima. Neither maximum promises that an entire requested window is complete.
internal sealed class SharedSubtitleCoverage
{
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Decoder decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder partial = new();
    private long indexedLength;
    private long lastStart = -1;
    private long lastEnd = -1;
    internal SharedSubtitleCoverage(string path) { this.path = path; }
    internal async Task<long> ReadLastEndAsync(CancellationToken token) => (await ReadAsync(token).ConfigureAwait(false)).LastEnd;
    internal async Task<SharedSubtitleExtent> ReadAsync(CancellationToken token)
    {
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = file.Length;
            if (length > SharedSubtitleOutput.MaximumTrackBytes + 1024 * 1024) throw new SubtitleProblem("output-budget");
            if (length < indexedLength) Reset();
            if (length == indexedLength) return new(lastStart, lastEnd);
            file.Position = indexedLength;
            var bytes = new byte[64 * 1024]; var chars = new char[bytes.Length];
            while (indexedLength < length)
            {
                var read = await file.ReadAsync(bytes, 0, (int)Math.Min(bytes.Length, length - indexedLength), token).ConfigureAwait(false);
                if (read == 0) break;
                var count = decoder.GetChars(bytes, 0, read, chars, 0, false);
                var at = 0;
                for (var index = 0; index < count; index++)
                {
                    if (chars[index] != '\n') continue;
                    Append(chars, at, index - at);
                    IndexLine(partial.ToString()); partial.Clear(); at = index + 1;
                }
                Append(chars, at, count - at);
                indexedLength += read;
                token.ThrowIfCancellationRequested();
            }
            return new(lastStart, lastEnd);
        }
        catch { Reset(); throw; }
        finally { gate.Release(); }
    }
    private void Append(char[] chars, int start, int count)
    {
        if (partial.Length + count > 1024 * 1024) throw new SubtitleProblem("output-budget");
        partial.Append(chars, start, count);
    }
    private void IndexLine(string line)
    {
        if (!line.StartsWith("Dialogue:", StringComparison.Ordinal)) return;
        var fields = line.Split(new[] { ',' }, 4);
        if (fields.Length < 4 || !TimeSpan.TryParse(fields[1], CultureInfo.InvariantCulture, out var from) ||
            !TimeSpan.TryParse(fields[2], CultureInfo.InvariantCulture, out var to) || from.Ticks < 0 || to < from)
            throw new SubtitleProblem("parse-invalid");
        lastStart = Math.Max(lastStart, from.Ticks); lastEnd = Math.Max(lastEnd, to.Ticks);
    }
    private void Reset() { indexedLength = 0; lastStart = lastEnd = -1; partial.Clear(); decoder.Reset(); }
}
