using System.Text;

namespace OpenHandsNET.Infrastructure.Agent.Tools;

/// <summary>
/// Coalescing buffer for <c>command_output</c> events (SPEC-24 RF-003):
/// flushes at 250ms elapsed or 32KB buffered, whichever comes first.
/// Lossless — coalescing only concatenates, never drops content.
/// </summary>
public sealed class OutputDebouncer
{
    private static readonly TimeSpan FlushInterval =
        TimeSpan.FromMilliseconds(250);
    private const int FlushBytes = 32 * 1024;

    private readonly Func<string, string, Task> _flush;
    private readonly Dictionary<string, StringBuilder> _buffers = new();
    private DateTime _lastFlush = DateTime.UtcNow;

    /// <summary>Creates a debouncer; <paramref name="flush"/> receives (channel, text).</summary>
    public OutputDebouncer(Func<string, string, Task> flush) => _flush = flush;

    /// <summary>Buffers one chunk, flushing when a bound is hit.</summary>
    public async Task AddAsync(string channel, string text)
    {
        if (!_buffers.TryGetValue(channel, out var sb))
        {
            _buffers[channel] = sb = new StringBuilder();
        }

        sb.Append(text);
        if (sb.Length >= FlushBytes
            || DateTime.UtcNow - _lastFlush >= FlushInterval)
        {
            await FlushAsync();
        }
    }

    /// <summary>Flushes every pending channel (call at command end).</summary>
    public async Task FlushAsync()
    {
        _lastFlush = DateTime.UtcNow;
        foreach (var (channel, sb) in _buffers)
        {
            if (sb.Length > 0)
            {
                var text = sb.ToString();
                sb.Clear();
                await _flush(channel, text);
            }
        }
    }
}