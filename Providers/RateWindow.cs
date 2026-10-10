namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>Sliding window over cumulative byte counts, giving the live rate of the last second.</summary>
internal sealed class RateWindow
{
    private static readonly TimeSpan Span = TimeSpan.FromSeconds(1);

    private readonly Queue<(TimeSpan At, long Bytes)> _samples = new([(TimeSpan.Zero, 0L)]);

    /// <summary>Adds a sample (elapsed time, total bytes so far) and returns the live rate in Mbit/s.</summary>
    public double Add(TimeSpan at, long totalBytes)
    {
        _samples.Enqueue((at, totalBytes));
        while (_samples.Count > 2 && at - _samples.Peek().At > Span)
            _samples.Dequeue();

        (TimeSpan firstAt, long firstBytes) = _samples.Peek();
        return ToMbps(totalBytes - firstBytes, at - firstAt);
    }

    public static double ToMbps(long bytes, TimeSpan elapsed) =>
        elapsed <= TimeSpan.Zero ? 0 : bytes * 8 / elapsed.TotalSeconds / 1_000_000;
}
