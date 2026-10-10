using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest;

/// <summary>
/// Typed view over the plugin's settings. Values are read on every access so a saved change
/// applies to the next test without a restart. Numbers arrive as <see cref="long"/>.
/// </summary>
internal sealed class SpeedtestSettings(IPluginSettings settings)
{
    public const string TestSecondsKey = "testSeconds";
    public const string StreamsKey = "streams";
    public const string LibreSpeedServerKey = "librespeedServer";
    public const string Iperf3HostKey = "iperf3Host";
    public const string Iperf3PortKey = "iperf3Port";
    public const string Iperf3SecondsKey = "iperf3Seconds";
    public const string Iperf3StreamsKey = "iperf3Streams";
    public const string Iperf3PathKey = "iperf3Path";

    public const long DefaultTestSeconds = 10;
    public const long DefaultStreams = 4;
    public const long DefaultIperf3Port = 5201;
    public const long DefaultIperf3Seconds = 10;
    public const long DefaultIperf3Streams = 1;
    public const string DefaultIperf3Path = "iperf3";

    /// <summary>Seconds per direction for the HTTP-based providers.</summary>
    public TimeSpan TestDuration => TimeSpan.FromSeconds(Clamp(TestSecondsKey, DefaultTestSeconds, 3, 60));

    public int Streams => (int)Clamp(StreamsKey, DefaultStreams, 1, 16);

    /// <summary>LibreSpeed backend URL; empty selects the nearest public server.</summary>
    public string LibreSpeedServer => Text(LibreSpeedServerKey, string.Empty);

    public string Iperf3Host => Text(Iperf3HostKey, string.Empty);

    public int Iperf3Port => (int)Clamp(Iperf3PortKey, DefaultIperf3Port, 1, 65535);

    public int Iperf3Seconds => (int)Clamp(Iperf3SecondsKey, DefaultIperf3Seconds, 1, 60);

    public int Iperf3Streams => (int)Clamp(Iperf3StreamsKey, DefaultIperf3Streams, 1, 32);

    public string Iperf3Path => Text(Iperf3PathKey, DefaultIperf3Path) is { Length: > 0 } path ? path : DefaultIperf3Path;

    private string Text(string key, string defaultValue) => (settings.Get(key, defaultValue) ?? defaultValue).Trim();

    private long Clamp(string key, long defaultValue, long min, long max) =>
        Math.Clamp(settings.Get(key, defaultValue), min, max);
}
