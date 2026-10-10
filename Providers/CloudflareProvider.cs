using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>
/// speed.cloudflare.com. Only the web UI sits behind a bot check; the raw <c>__down</c>/<c>__up</c>
/// endpoints used by Cloudflare's own speedtest library answer plain HTTP clients.
/// </summary>
internal sealed partial class CloudflareProvider(SpeedtestSettings settings) : ISpeedtestProvider
{
    public const string ProviderId = "cloudflare";

    private const string BaseUrl = "https://speed.cloudflare.com";
    private const int LatencySamples = 10;
    private const long DownloadBytesPerRequest = 25_000_000;
    private const long UploadBytesPerRequest = 10_000_000;

    public string Id => ProviderId;

    public string DisplayName => "Cloudflare";

    public async Task<SpeedtestResult> RunAsync(IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        (double ping, double jitter, string? colo) = await MeasureLatencyAsync(progress, cancellationToken);

        double download = await HttpThroughput.DownloadAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/__down?bytes={DownloadBytesPerRequest}"),
            settings.Streams, settings.TestDuration, progress, cancellationToken);

        double upload = await HttpThroughput.UploadAsync(
            () => new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/__up"),
            UploadBytesPerRequest, settings.Streams, settings.TestDuration, progress, cancellationToken);

        return new SpeedtestResult(download, upload, ping, jitter, colo is null ? DisplayName : $"Cloudflare {colo}");
    }

    private static async Task<(double Ping, double Jitter, string? Colo)> MeasureLatencyAsync(
        IProgress<SpeedtestProgress> progress, CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Latency));

        List<double> samples = [];
        string? colo = null;

        // One extra request first: it pays for DNS, TCP and TLS and is not counted.
        for (int i = 0; i <= LatencySamples; i++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, $"{BaseUrl}/__down?bytes=0");
            (double ms, HttpResponseMessage response) = await HttpThroughput.TimeToHeadersAsync(request, cancellationToken);
            using (response)
            {
                colo ??= ColoFromRay(response);
                if (i > 0)
                    samples.Add(Math.Max(0, ms - ServerTime(response)));
            }

            progress.Report(new SpeedtestProgress(SpeedtestPhase.Latency, 0, (double)i / LatencySamples));
        }

        return (HttpThroughput.Median(samples), HttpThroughput.Jitter(samples), colo);
    }

    /// <summary>
    /// Server-side processing time from the <c>Server-Timing</c> header, subtracted from the round trip
    /// so the figure approximates network latency as Cloudflare's own page does.
    /// </summary>
    private static double ServerTime(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Server-Timing", out IEnumerable<string>? values))
            return 0;

        double max = 0;
        foreach (string value in values)
        {
            foreach (Match match in DurationRegex().Matches(value))
            {
                if (double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out double dur))
                    max = Math.Max(max, dur);
            }
        }

        return max;
    }

    /// <summary>The data center code is the suffix of the <c>CF-RAY</c> header, e.g. <c>…-FRA</c>.</summary>
    private static string? ColoFromRay(HttpResponseMessage response) =>
        response.Headers.TryGetValues("CF-RAY", out IEnumerable<string>? values)
        && values.FirstOrDefault() is { } ray && ray.LastIndexOf('-') is var dash and > 0
            ? ray[(dash + 1)..]
            : null;

    [GeneratedRegex(@"\bdur=([0-9.]+)")]
    private static partial Regex DurationRegex();
}
