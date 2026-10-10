using System.Collections.Concurrent;
using System.Diagnostics;
using LoupixDeck.Plugin.Speedtest.Providers;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest;

/// <summary>
/// Runs at most one test at a time (parallel tests would distort each other) and keeps the latest
/// state per provider for the renderer. Progress is pushed to the buttons via
/// <see cref="IPluginHost.RequestButtonRefresh"/>, throttled to a few repaints per second.
/// </summary>
internal sealed class SpeedtestRunner(IPluginHost host, IReadOnlyList<ISpeedtestProvider> providers)
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(200);

    private readonly ConcurrentDictionary<string, SpeedtestState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _running;
    private string? _runningProviderId;
    private long _lastRefreshTicks;

    public IReadOnlyList<ISpeedtestProvider> Providers => providers;

    public ISpeedtestProvider? Find(string? providerId) =>
        providers.FirstOrDefault(p => string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase));

    public SpeedtestState GetState(string providerId) =>
        _states.TryGetValue(providerId, out SpeedtestState? state) ? state : SpeedtestState.Initial;

    /// <summary>Starts a test, or cancels it when this provider's test is already running.</summary>
    public void Toggle(ISpeedtestProvider provider)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_running is not null)
            {
                if (string.Equals(_runningProviderId, provider.Id, StringComparison.OrdinalIgnoreCase))
                    _running.Cancel();
                else
                    host.Logger.Info($"Speedtest: {provider.DisplayName} ignored, {_runningProviderId} is still running.");
                return;
            }

            cts = new CancellationTokenSource();
            _running = cts;
            _runningProviderId = provider.Id;
        }

        _ = Task.Run(() => RunAsync(provider, cts));
    }

    public void CancelAll()
    {
        lock (_gate)
            _running?.Cancel();
    }

    private async Task RunAsync(ISpeedtestProvider provider, CancellationTokenSource cts)
    {
        SpeedtestResult? previous = GetState(provider.Id).LastResult;
        ProgressSink sink = new(this, provider.Id, previous);
        sink.Report(new SpeedtestProgress(SpeedtestPhase.Connecting));

        try
        {
            SpeedtestResult result = await provider.RunAsync(sink, cts.Token);
            Publish(provider.Id, new SpeedtestState(SpeedtestPhase.Done, LastResult: result), force: true);
            host.Logger.Info($"Speedtest {provider.DisplayName}: down {result.DownloadMbps:F1} Mbit/s, " +
                             $"up {result.UploadMbps:F1} Mbit/s, ping {result.PingMs:F1} ms ({result.Server})");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            Publish(provider.Id, new SpeedtestState(previous is null ? SpeedtestPhase.Idle : SpeedtestPhase.Done,
                LastResult: previous), force: true);
        }
        catch (Exception ex)
        {
            host.Logger.Error($"Speedtest {provider.DisplayName} failed.", ex);
            Publish(provider.Id, new SpeedtestState(SpeedtestPhase.Error, LastResult: previous, Error: ShortMessage(ex)),
                force: true);
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                _runningProviderId = null;
            }

            cts.Dispose();
        }
    }

    private void Publish(string providerId, SpeedtestState state, bool force)
    {
        SpeedtestState? old = _states.TryGetValue(providerId, out SpeedtestState? existing) ? existing : null;
        _states[providerId] = state;

        long now = Stopwatch.GetTimestamp();
        bool due = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastRefreshTicks), now) >= RefreshInterval;
        if (!force && !due && old?.Phase == state.Phase)
            return;

        Interlocked.Exchange(ref _lastRefreshTicks, now);
        try
        {
            host.RequestButtonRefresh(SpeedtestCommand.Name);
        }
        catch (Exception ex)
        {
            host.Logger.Warn($"Speedtest: button refresh failed: {ex.Message}");
        }
    }

    private static string ShortMessage(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } code } => $"HTTP {(int)code}",
        HttpRequestException => "No connection",
        TimeoutException => "Timeout",
        _ => ex.Message
    };

    private sealed class ProgressSink(SpeedtestRunner runner, string providerId, SpeedtestResult? previous)
        : IProgress<SpeedtestProgress>
    {
        public void Report(SpeedtestProgress value) =>
            runner.Publish(providerId,
                new SpeedtestState(value.Phase, value.Mbps, Math.Clamp(value.Fraction, 0, 1), previous),
                force: false);
    }
}
