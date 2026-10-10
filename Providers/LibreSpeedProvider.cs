using System.Text.Json;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>
/// LibreSpeed (https://github.com/librespeed/speedtest): a configured backend (e.g. self-hosted) or the
/// public server with the lowest latency from the official list. Uses the backend's standard
/// <c>garbage.php</c> / <c>empty.php</c> endpoints, with the same chunk sizes as the official client.
/// </summary>
internal sealed class LibreSpeedProvider(SpeedtestSettings settings) : ISpeedtestProvider
{
    public const string ProviderId = "librespeed";

    private const string ServerListUrl = "https://librespeed.org/backend-servers/servers.php";
    private const int DownloadChunkMegabytes = 100;
    private const long UploadBytesPerRequest = 20L * 1024 * 1024;
    private const int LatencySamples = 10;
    private const int ProbeConcurrency = 16;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public string Id => ProviderId;

    public string DisplayName => "LibreSpeed";

    public async Task<SpeedtestResult> RunAsync(IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Connecting));
        Server server = await ResolveServerAsync(cancellationToken);

        progress.Report(new SpeedtestProgress(SpeedtestPhase.Latency));
        List<double> samples = [];
        for (int i = 0; i <= LatencySamples; i++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, server.Url(server.PingPath));
            (double ms, HttpResponseMessage response) = await HttpThroughput.TimeToHeadersAsync(request, cancellationToken);
            response.Dispose();
            if (i > 0)
                samples.Add(ms);
            progress.Report(new SpeedtestProgress(SpeedtestPhase.Latency, 0, (double)i / LatencySamples));
        }

        double download = await HttpThroughput.DownloadAsync(
            () => new HttpRequestMessage(HttpMethod.Get, server.Url(server.DownloadPath, $"ckSize={DownloadChunkMegabytes}")),
            settings.Streams, settings.TestDuration, progress, cancellationToken);

        double upload = await HttpThroughput.UploadAsync(
            () => new HttpRequestMessage(HttpMethod.Post, server.Url(server.UploadPath)),
            UploadBytesPerRequest, settings.Streams, settings.TestDuration, progress, cancellationToken);

        return new SpeedtestResult(download, upload, HttpThroughput.Median(samples), HttpThroughput.Jitter(samples),
            server.Name);
    }

    private async Task<Server> ResolveServerAsync(CancellationToken cancellationToken)
    {
        string configured = settings.LibreSpeedServer;
        if (configured.Length > 0)
        {
            Uri uri = new(configured.EndsWith('/') ? configured : configured + "/");
            return new Server(uri.Host, uri, "garbage.php", "empty.php", "empty.php");
        }

        List<Server> servers = await LoadPublicServersAsync(cancellationToken);
        using SemaphoreSlim slots = new(ProbeConcurrency);
        (Server Server, double Ms)[] probes = await Task.WhenAll(servers.Select(async server =>
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                return (server, await ProbeAsync(server, cancellationToken));
            }
            finally
            {
                slots.Release();
            }
        }));

        return probes.Where(p => !double.IsInfinity(p.Ms)).OrderBy(p => p.Ms).Select(p => p.Server).FirstOrDefault()
               ?? throw new InvalidOperationException("No LibreSpeed server reachable");
    }

    /// <summary>
    /// Round trip to the ping endpoint; infinity when unreachable or too slow. The first request pays for
    /// DNS, TCP and TLS, so the second one on the warm connection is the one compared.
    /// </summary>
    private static async Task<double> ProbeAsync(Server server, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            double ms = 0;
            for (int i = 0; i < 2; i++)
            {
                using HttpRequestMessage request = new(HttpMethod.Get, server.Url(server.PingPath));
                (ms, HttpResponseMessage response) = await HttpThroughput.TimeToHeadersAsync(request, timeout.Token);
                response.Dispose();
            }

            return ms;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return double.PositiveInfinity;
        }
    }

    private static async Task<List<Server>> LoadPublicServersAsync(CancellationToken cancellationToken)
    {
        await using Stream stream = await HttpThroughput.Client.GetStreamAsync(ServerListUrl, cancellationToken);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        List<Server> servers = [];
        foreach (JsonElement entry in doc.RootElement.EnumerateArray())
        {
            string? baseUrl = entry.TryGetProperty("server", out JsonElement s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(baseUrl))
                continue;

            // The list may use protocol-relative URLs.
            if (baseUrl.StartsWith("//", StringComparison.Ordinal))
                baseUrl = "https:" + baseUrl;
            if (!Uri.TryCreate(baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/", UriKind.Absolute, out Uri? uri))
                continue;

            servers.Add(new Server(
                entry.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? uri.Host : uri.Host,
                uri,
                PathOf(entry, "dlURL", "garbage.php"),
                PathOf(entry, "ulURL", "empty.php"),
                PathOf(entry, "pingURL", "empty.php")));
        }

        return servers;
    }

    private static string PathOf(JsonElement entry, string property, string fallback) =>
        entry.TryGetProperty(property, out JsonElement value) && value.GetString() is { Length: > 0 } path
            ? path
            : fallback;

    private sealed record Server(string Name, Uri BaseUri, string DownloadPath, string UploadPath, string PingPath)
    {
        /// <summary>Endpoint URL with a cache buster, as the official client does.</summary>
        public Uri Url(string path, string? query = null)
        {
            Uri uri = new(BaseUri, path);
            string separator = uri.Query.Length > 0 ? "&" : "?";
            string extra = query is null ? string.Empty : query + "&";
            return new Uri($"{uri}{separator}{extra}r={Random.Shared.NextInt64()}");
        }
    }
}
