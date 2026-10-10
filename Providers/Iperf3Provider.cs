using System.Globalization;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>
/// LAN test against the user's own iperf3 server, using the installed <c>iperf3</c> client. Text output
/// (<c>-f m</c>) is parsed rather than JSON because only the text mode reports intervals while running,
/// and it works the same with the old iperf3 builds common on Windows. No latency is measured.
/// </summary>
internal sealed partial class Iperf3Provider(SpeedtestSettings settings) : ISpeedtestProvider
{
    public const string ProviderId = "iperf3";

    private static readonly TimeSpan ExtraTimeout = TimeSpan.FromSeconds(15);

    public string Id => ProviderId;

    public string DisplayName => "iperf3";

    public async Task<SpeedtestResult> RunAsync(IProgress<SpeedtestProgress> progress,
        CancellationToken cancellationToken)
    {
        progress.Report(new SpeedtestProgress(SpeedtestPhase.Connecting));

        string host = settings.Iperf3Host;
        if (host.Length == 0)
            throw new InvalidOperationException("iperf3 server not set");

        Version version = Iperf3Tool.GetVersion(settings.Iperf3Path)
                          ?? throw new InvalidOperationException("iperf3 not installed");

        double download = await RunClientAsync(host, version, reverse: true, progress, cancellationToken);
        double upload = await RunClientAsync(host, version, reverse: false, progress, cancellationToken);

        return new SpeedtestResult(download, upload, null, null, host);
    }

    private async Task<double> RunClientAsync(string host, Version version, bool reverse,
        IProgress<SpeedtestProgress> progress, CancellationToken cancellationToken)
    {
        SpeedtestPhase phase = reverse ? SpeedtestPhase.Download : SpeedtestPhase.Upload;
        progress.Report(new SpeedtestProgress(phase));

        int seconds = settings.Iperf3Seconds;
        int streams = settings.Iperf3Streams;
        List<string> args =
        [
            "-c", host, "-p", settings.Iperf3Port.ToString(CultureInfo.InvariantCulture),
            "-t", seconds.ToString(CultureInfo.InvariantCulture),
            "-P", streams.ToString(CultureInfo.InvariantCulture),
            "-f", "m"
        ];
        if (reverse)
            args.Add("-R");
        // Without it, output to a pipe is block-buffered and intervals only arrive at exit.
        if (version >= new Version(3, 2))
            args.Add("--forceflush");

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds) + ExtraTimeout);

        double? final = null;
        List<string> errors = [];
        // stdout and stderr callbacks arrive on different threads.
        void AddError(string line)
        {
            lock (errors)
                errors.Add(line);
        }

        int exitCode;
        try
        {
            exitCode = await Iperf3Tool.RunAsync(settings.Iperf3Path, args, line =>
            {
                if (ParseLine(line, streams) is not { } sample)
                {
                    if (line.Contains("error", StringComparison.OrdinalIgnoreCase))
                        AddError(line);
                    return;
                }

                if (sample.IsReceiverSummary)
                    final = sample.Mbps;
                else if (!sample.IsSummary)
                    progress.Report(new SpeedtestProgress(phase, sample.Mbps, sample.End / seconds));
            }, AddError, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("iperf3 did not finish in time");
        }

        if (exitCode != 0 || final is null)
            throw new InvalidOperationException(CleanError(errors.LastOrDefault()) ?? $"iperf3 exited with code {exitCode}");

        return final.Value;
    }

    /// <summary>
    /// Interval and summary lines look like <c>[  5]   0.00-1.00   sec   112 MBytes   941 Mbits/sec ...</c>.
    /// With several streams only the <c>[SUM]</c> lines count, with one stream only the per-stream lines.
    /// </summary>
    private static Sample? ParseLine(string line, int streams)
    {
        Match match = IntervalRegex().Match(line);
        if (!match.Success || (match.Groups["id"].Value == "SUM") != (streams > 1))
            return null;

        string rest = match.Groups["rest"].Value;
        return new Sample(
            double.Parse(match.Groups["end"].Value, CultureInfo.InvariantCulture),
            double.Parse(match.Groups["rate"].Value, CultureInfo.InvariantCulture),
            rest.Contains("sender", StringComparison.Ordinal) || rest.Contains("receiver", StringComparison.Ordinal),
            rest.Contains("receiver", StringComparison.Ordinal));
    }

    private static string? CleanError(string? line) =>
        line?.Replace("iperf3: error - ", string.Empty, StringComparison.Ordinal).Trim();

    private readonly record struct Sample(double End, double Mbps, bool IsSummary, bool IsReceiverSummary);

    [GeneratedRegex(@"^\[\s*(?<id>SUM|\d+)\]\s+[\d.]+-(?<end>[\d.]+)\s+sec\s+[\d.]+\s+\w*Bytes\s+(?<rate>[\d.]+)\s+Mbits/sec(?<rest>.*)$")]
    private static partial Regex IntervalRegex();
}
