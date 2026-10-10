using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest;

public sealed class SpeedtestPlugin : LoupixPlugin
{
    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "speedtest",
        Name = "Speedtest",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "",
        Description = ""
    };

    public override void Initialize(IPluginHost host)
    {
    }

    public override IEnumerable<IPluginCommand> GetCommands() => [];
}
