using System.Buffers;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>
/// M-Lab NDT7 (https://github.com/m-lab/ndt-server/blob/main/spec/ndt7-protocol.md): the nearest server
/// comes from the Locate API, download and upload each run over a WebSocket for about ten seconds.
/// Latency is the server-side TCP MinRTT, so it reflects the network path without HTTP overhead.
/// </summary>
internal sealed class Ndt7Provider : ISpeedtestProvider
{
    public const string ProviderId = "ndt7";

    private const string SubProtocol = "net.measurementlab.ndt.v7";
    private const string ClientQuery = "client_name=loupixdeck-speedtest&client_version=1.0.0";
    private const string LocateUrl = "https://locate.measurementlab.net/v2/nearest/ndt/ndt7?" + ClientQuery;
    private const string DownloadKey = "wss:///ndt/v7/download";
    private const string UploadKey = "wss:///ndt/v7/upload";

    private static readonly TimeSpan PhaseDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PhaseTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(200);

    private const int InitialMessageSize = 1 << 13;
    private const int MaxMessageSize = 1 << 20;

    public string Id => ProviderId;

    public string DisplayName => "M-Lab";

    public async Task<SpeedtestResult> RunAsync(IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Connecting));
        (string downloadUrl, string uploadUrl, string server) = await LocateAsync(cancellationToken);

        Measurement download = await DownloadAsync(downloadUrl, progress, cancellationToken);
        Measurement upload = await UploadAsync(uploadUrl, progress, cancellationToken);

        return new SpeedtestResult(download.Mbps, upload.Mbps, download.MinRttMs ?? upload.MinRttMs,
            download.RttVarMs, server);
    }

    private static async Task<(string Download, string Upload, string Server)> LocateAsync(
        CancellationToken cancellationToken)
    {
        await using Stream stream = await HttpThroughput.Client.GetStreamAsync(LocateUrl, cancellationToken);
        using JsonDocument doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        foreach (JsonElement result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            if (!result.TryGetProperty("urls", out JsonElement urls)
                || !urls.TryGetProperty(DownloadKey, out JsonElement down)
                || !urls.TryGetProperty(UploadKey, out JsonElement up))
                continue;

            string server = result.TryGetProperty("location", out JsonElement location)
                            && location.TryGetProperty("city", out JsonElement city)
                ? $"M-Lab {city.GetString()}"
                : "M-Lab";
            return (down.GetString()!, up.GetString()!, server);
        }

        throw new InvalidOperationException("No M-Lab server available");
    }

    private static async Task<Measurement> DownloadAsync(string url, IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Download));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PhaseTimeout);
        using ClientWebSocket socket = await ConnectAsync(url, timeout.Token);

        Measurement measurement = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxMessageSize);
        MemoryStream text = new();
        Stopwatch clock = Stopwatch.StartNew();
        RateWindow window = new();
        TimeSpan lastReport = TimeSpan.Zero;
        long bytes = 0;

        try
        {
            while (true)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, timeout.Token);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;

                bytes += received.Count;
                if (received.MessageType == WebSocketMessageType.Text)
                {
                    text.Write(buffer, 0, received.Count);
                    if (received.EndOfMessage)
                    {
                        measurement.ApplyServerMessage(text.ToArray(), upload: false);
                        text.SetLength(0);
                    }
                }

                TimeSpan now = clock.Elapsed;
                if (now - lastReport >= ReportInterval)
                {
                    lastReport = now;
                    progress.Report(new SpeedtestProgress(SpeedtestPhase.Download, window.Add(now, bytes),
                        now / PhaseDuration));
                }
            }

            await CloseQuietlyAsync(socket);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        measurement.ClientMbps = RateWindow.ToMbps(bytes, clock.Elapsed);
        return measurement;
    }

    private static async Task<Measurement> UploadAsync(string url, IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Upload));

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PhaseTimeout);
        using ClientWebSocket socket = await ConnectAsync(url, timeout.Token);

        Measurement measurement = new();
        Task reader = ReadServerMessagesAsync(socket, measurement, timeout.Token);

        byte[] payload = new byte[MaxMessageSize];
        Random.Shared.NextBytes(payload);
        int messageSize = InitialMessageSize;
        Stopwatch clock = Stopwatch.StartNew();
        RateWindow window = new();
        TimeSpan lastReport = TimeSpan.Zero;
        long sent = 0;

        while (clock.Elapsed < PhaseDuration)
        {
            await socket.SendAsync(payload.AsMemory(0, messageSize), WebSocketMessageType.Binary, true, timeout.Token);
            sent += messageSize;

            // Spec: grow the message while it is small relative to what has been sent.
            if (messageSize < MaxMessageSize && messageSize <= sent / 16)
                messageSize *= 2;

            TimeSpan now = clock.Elapsed;
            if (now - lastReport >= ReportInterval)
            {
                lastReport = now;
                progress.Report(new SpeedtestProgress(SpeedtestPhase.Upload, window.Add(now, sent),
                    now / PhaseDuration));
            }
        }

        measurement.ClientMbps = RateWindow.ToMbps(sent, clock.Elapsed);
        await CloseQuietlyAsync(socket);
        try
        {
            await reader;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The server measurements are a refinement; the client-side figure stands without them.
        }

        return measurement;
    }

    private static async Task ReadServerMessagesAsync(ClientWebSocket socket, Measurement measurement,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        MemoryStream text = new();
        while (socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
                return;
            if (received.MessageType != WebSocketMessageType.Text)
                continue;

            text.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                measurement.ApplyServerMessage(text.ToArray(), upload: true);
                text.SetLength(0);
            }
        }
    }

    private static async Task<ClientWebSocket> ConnectAsync(string url, CancellationToken cancellationToken)
    {
        string separator = url.Contains('?') ? "&" : "?";
        ClientWebSocket socket = new();
        socket.Options.AddSubProtocol(SubProtocol);
        socket.Options.SetRequestHeader("User-Agent", "LoupixDeck-Speedtest/1.0");
        try
        {
            await socket.ConnectAsync(new Uri(url + separator + ClientQuery), cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        catch (Exception)
        {
            // Closing is best effort; the measurement is already complete.
        }
    }

    /// <summary>Accumulates the server's JSON measurement messages (TCPInfo values are microseconds).</summary>
    private sealed class Measurement
    {
        private double? _serverUploadMbps;

        public double ClientMbps { get; set; }

        public double? MinRttMs { get; private set; }

        public double? RttVarMs { get; private set; }

        /// <summary>For upload the server's count of received bytes is authoritative; the client only knows what it queued.</summary>
        public double Mbps => _serverUploadMbps ?? ClientMbps;

        public void ApplyServerMessage(byte[] json, bool upload)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("TCPInfo", out JsonElement tcp))
                    return;

                if (tcp.TryGetProperty("MinRTT", out JsonElement minRtt) && minRtt.GetDouble() > 0)
                    MinRttMs = minRtt.GetDouble() / 1000;
                if (tcp.TryGetProperty("RTTVar", out JsonElement rttVar))
                    RttVarMs = rttVar.GetDouble() / 1000;

                if (upload
                    && tcp.TryGetProperty("BytesReceived", out JsonElement received)
                    && tcp.TryGetProperty("ElapsedTime", out JsonElement elapsed)
                    && elapsed.GetDouble() > 0)
                {
                    // Bits per microsecond equals Mbit/s.
                    _serverUploadMbps = received.GetDouble() * 8 / elapsed.GetDouble();
                }
            }
            catch (JsonException)
            {
                // A malformed measurement is skipped; the next one replaces it.
            }
        }
    }
}
