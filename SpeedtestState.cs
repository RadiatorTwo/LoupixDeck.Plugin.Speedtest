namespace LoupixDeck.Plugin.Speedtest;

internal enum SpeedtestPhase
{
    Idle,
    Connecting,
    Latency,
    Download,
    Upload,
    Done,
    Error
}

/// <summary>A live sample reported by a provider while a test runs.</summary>
/// <param name="Fraction">Progress of the current phase, 0..1.</param>
internal readonly record struct SpeedtestProgress(SpeedtestPhase Phase, double Mbps = 0, double Fraction = 0);

/// <summary>Outcome of a finished test. Values a provider cannot measure stay <c>null</c>.</summary>
internal sealed record SpeedtestResult(
    double? DownloadMbps,
    double? UploadMbps,
    double? PingMs,
    double? JitterMs,
    string? Server)
{
    public DateTimeOffset CompletedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>Immutable snapshot of one provider's test, read by the renderer.</summary>
internal sealed record SpeedtestState(
    SpeedtestPhase Phase,
    double Mbps = 0,
    double Fraction = 0,
    SpeedtestResult? LastResult = null,
    string? Error = null)
{
    public static SpeedtestState Initial { get; } = new(SpeedtestPhase.Idle);

    public bool IsRunning => Phase is SpeedtestPhase.Connecting or SpeedtestPhase.Latency
        or SpeedtestPhase.Download or SpeedtestPhase.Upload;
}
