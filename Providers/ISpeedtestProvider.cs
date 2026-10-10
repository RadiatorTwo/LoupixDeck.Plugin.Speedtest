namespace LoupixDeck.Plugin.Speedtest.Providers;

internal interface ISpeedtestProvider
{
    /// <summary>Stable id, baked into button parameters. Never rename after a release.</summary>
    string Id { get; }

    /// <summary>Short label shown on the button and in the menu.</summary>
    string DisplayName { get; }

    Task<SpeedtestResult> RunAsync(IProgress<SpeedtestProgress> progress, CancellationToken cancellationToken);
}
