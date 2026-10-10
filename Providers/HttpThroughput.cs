using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>
/// Shared HTTP measurement for the plain-HTTP providers: parallel download/upload streams for a fixed
/// duration. The first part of the run is treated as warm-up (TCP slow start) and excluded from the
/// final figure; the live figure is a sliding one-second window.
/// </summary>
internal static class HttpThroughput
{
    private const double WarmupShare = 0.2;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);

    // Upload payload: random so compressing proxies cannot cheat; shared, read-only.
    private static readonly byte[] UploadChunk = CreateUploadChunk();

    public static HttpClient Client { get; } = CreateClient();

    public static Task<double> DownloadAsync(
        Func<HttpRequestMessage> createRequest, int streams, TimeSpan duration,
        IProgress<SpeedtestProgress> progress, CancellationToken cancellationToken) =>
        MeasureAsync(SpeedtestPhase.Download, streams, duration, progress, cancellationToken,
            (counter, token) => DownloadStreamAsync(createRequest, counter, token));

    /// <param name="createRequest">Builds a POST request; its content is set here.</param>
    public static Task<double> UploadAsync(
        Func<HttpRequestMessage> createRequest, long bytesPerRequest, int streams, TimeSpan duration,
        IProgress<SpeedtestProgress> progress, CancellationToken cancellationToken) =>
        MeasureAsync(SpeedtestPhase.Upload, streams, duration, progress, cancellationToken,
            (counter, token) => UploadStreamAsync(createRequest, bytesPerRequest, counter, token));

    /// <summary>Time to response headers of a request on a warm connection, in milliseconds.</summary>
    public static async Task<(double Milliseconds, HttpResponseMessage Response)> TimeToHeadersAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        long start = Stopwatch.GetTimestamp();
        HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
        }

        return (ms, response);
    }

    public static double Median(IReadOnlyList<double> values)
    {
        double[] sorted = [.. values.Order()];
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Mean absolute difference between consecutive samples.</summary>
    public static double Jitter(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
            return 0;

        double sum = 0;
        for (int i = 1; i < values.Count; i++)
            sum += Math.Abs(values[i] - values[i - 1]);
        return sum / (values.Count - 1);
    }

    private static async Task<double> MeasureAsync(
        SpeedtestPhase phase, int streams, TimeSpan duration, IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken, Func<ByteCounter, CancellationToken, Task> runStream)
    {
        progress.Report(new SpeedtestProgress(phase));

        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ByteCounter counter = new();
        Task[] workers = [.. Enumerable.Range(0, streams).Select(_ => Task.Run(() => runStream(counter, stop.Token)))];

        Stopwatch clock = Stopwatch.StartNew();
        TimeSpan warmup = duration * WarmupShare;
        long warmupBytes = -1;
        TimeSpan warmupAt = TimeSpan.Zero;
        RateWindow window = new();

        try
        {
            while (clock.Elapsed < duration)
            {
                await Task.Delay(SampleInterval, cancellationToken);
                ThrowIfAllFailed(workers);

                TimeSpan now = clock.Elapsed;
                long bytes = counter.Value;
                if (warmupBytes < 0 && now >= warmup)
                {
                    warmupBytes = bytes;
                    warmupAt = now;
                }

                progress.Report(new SpeedtestProgress(phase, window.Add(now, bytes), now / duration));
            }
        }
        finally
        {
            await stop.CancelAsync();
            await Task.WhenAll(workers.Select(w => w.ContinueWith(_ => { }, TaskScheduler.Default)));
        }

        TimeSpan total = clock.Elapsed;
        long totalBytes = counter.Value;
        if (warmupBytes < 0)
            return RateWindow.ToMbps(totalBytes, total);

        return RateWindow.ToMbps(totalBytes - warmupBytes, total - warmupAt);
    }

    private static void ThrowIfAllFailed(Task[] workers)
    {
        if (workers.All(w => w.IsFaulted))
            throw workers[0].Exception!.InnerException ?? workers[0].Exception!;
    }

    private static async Task DownloadStreamAsync(
        Func<HttpRequestMessage> createRequest, ByteCounter counter, CancellationToken token)
    {
        byte[] buffer = new byte[81920];
        while (!token.IsCancellationRequested)
        {
            using HttpRequestMessage request = createRequest();
            using HttpResponseMessage response = await Client.SendAsync(request,
                HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
                counter.Add(read);
        }
    }

    private static async Task UploadStreamAsync(
        Func<HttpRequestMessage> createRequest, long bytesPerRequest, ByteCounter counter, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using HttpRequestMessage request = createRequest();
            request.Content = new CountingContent(bytesPerRequest, counter);
            using HttpResponseMessage response = await Client.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
        }
    }

    private static HttpClient CreateClient()
    {
        SocketsHttpHandler handler = new()
        {
            AutomaticDecompression = DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(10)
        };

        HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("LoupixDeck-Speedtest", "1.0"));
        return client;
    }

    private static byte[] CreateUploadChunk()
    {
        byte[] chunk = new byte[64 * 1024];
        Random.Shared.NextBytes(chunk);
        return chunk;
    }

    private sealed class ByteCounter
    {
        private long _value;

        public long Value => Interlocked.Read(ref _value);

        public void Add(long bytes) => Interlocked.Add(ref _value, bytes);
    }

    /// <summary>Request body that counts bytes as they are handed to the socket, for a live upload figure.</summary>
    private sealed class CountingContent : HttpContent
    {
        private readonly long _length;
        private readonly ByteCounter _counter;

        public CountingContent(long length, ByteCounter counter)
        {
            _length = length;
            _counter = counter;
            Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context,
            CancellationToken cancellationToken)
        {
            long remaining = _length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(remaining, UploadChunk.Length);
                await stream.WriteAsync(UploadChunk.AsMemory(0, count), cancellationToken);
                _counter.Add(count);
                remaining -= count;
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }
    }
}
