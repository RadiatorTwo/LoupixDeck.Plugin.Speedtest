using LoupixDeck.Plugin.Speedtest.Providers;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest;

public sealed class SpeedtestPlugin : LoupixPlugin, IMenuContributor, IPluginSettingsPage, IPluginRequirements
{
    public const string Group = "Speedtest";

    private IPluginHost? _host;
    private SpeedtestSettings? _settings;
    private SpeedtestRunner? _runner;
    private SpeedtestCommand? _command;

    public override PluginMetadata Metadata { get; } = new()
    {
        Id = "speedtest",
        Name = "Speedtest",
        Version = new Version(1, 0, 0),
        SdkVersion = SdkInfo.Version,
        Author = "RadiatorTwo",
        Description = "Internet speed test (Cloudflare, M-Lab, LibreSpeed) and LAN test (iperf3) with live results on a touch button.",
        Icon = LoadIcon()
    };

    /// <summary>The plugin icon (icon.png, embedded). Missing data only costs the icon.</summary>
    private static byte[]? LoadIcon()
    {
        using Stream? stream = typeof(SpeedtestPlugin).Assembly.GetManifestResourceStream("LoupixDeck.Plugin.Speedtest.icon.png");
        if (stream == null) return null;

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public override void Initialize(IPluginHost host)
    {
        _host = host;
        _settings = new SpeedtestSettings(host.Settings);
        _runner = new SpeedtestRunner(host,
        [
            new CloudflareProvider(_settings),
            new Ndt7Provider(),
            new LibreSpeedProvider(_settings),
            new Iperf3Provider(_settings)
        ]);
        _command = new SpeedtestCommand(_runner);
    }

    public override void Shutdown() => _runner?.CancelAll();

    public override IEnumerable<IPluginCommand> GetCommands() => _command is null ? [] : [_command];

    public Task<IReadOnlyList<MenuNode>> GetMenuNodes(ButtonTargets target)
    {
        if (_runner is null || !target.HasFlag(ButtonTargets.TouchButton))
            return Task.FromResult<IReadOnlyList<MenuNode>>([]);

        IReadOnlyList<MenuNode> nodes =
        [
            new MenuNode
            {
                Name = Group,
                CommandName = string.Empty,
                Children = [.. _runner.Providers.Select(provider => new MenuNode
                {
                    Name = $"Speedtest ({provider.DisplayName})",
                    CommandName = SpeedtestCommand.Name,
                    Parameters = new Dictionary<string, string> { [SpeedtestCommand.ProviderParameter] = provider.Id }
                })]
            }
        ];
        return Task.FromResult(nodes);
    }

    public IReadOnlyList<PluginSettingDescriptor> SettingsSchema { get; } =
    [
        new() { Key = "internetHeading", Label = "Internet test (Cloudflare, LibreSpeed)", Kind = PluginSettingKind.Heading },
        new()
        {
            Key = SpeedtestSettings.TestSecondsKey, Label = "Duration per direction (s)", Kind = PluginSettingKind.Number,
            DefaultValue = SpeedtestSettings.DefaultTestSeconds,
            Description = "How long download and upload are each measured (3–60). M-Lab always uses 10 s."
        },
        new()
        {
            Key = SpeedtestSettings.StreamsKey, Label = "Parallel connections", Kind = PluginSettingKind.Number,
            DefaultValue = SpeedtestSettings.DefaultStreams,
            Description = "More connections saturate fast lines better (1–16)."
        },
        new()
        {
            Key = SpeedtestSettings.LibreSpeedServerKey, Label = "LibreSpeed server", Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty,
            Description = "Backend URL of a LibreSpeed server, e.g. https://speed.example.com/backend. Empty picks the nearest public server."
        },
        new() { Key = "iperf3Heading", Label = "LAN test (iperf3)", Kind = PluginSettingKind.Heading },
        new()
        {
            Key = SpeedtestSettings.Iperf3HostKey, Label = "iperf3 server", Kind = PluginSettingKind.Text,
            DefaultValue = string.Empty,
            Description = "Host name or IP of a machine running 'iperf3 -s'."
        },
        new()
        {
            Key = SpeedtestSettings.Iperf3PortKey, Label = "iperf3 port", Kind = PluginSettingKind.Number,
            DefaultValue = SpeedtestSettings.DefaultIperf3Port
        },
        new()
        {
            Key = SpeedtestSettings.Iperf3SecondsKey, Label = "iperf3 duration per direction (s)", Kind = PluginSettingKind.Number,
            DefaultValue = SpeedtestSettings.DefaultIperf3Seconds
        },
        new()
        {
            Key = SpeedtestSettings.Iperf3StreamsKey, Label = "iperf3 parallel streams", Kind = PluginSettingKind.Number,
            DefaultValue = SpeedtestSettings.DefaultIperf3Streams
        },
        new()
        {
            Key = SpeedtestSettings.Iperf3PathKey, Label = "iperf3 executable", Kind = PluginSettingKind.Text,
            DefaultValue = SpeedtestSettings.DefaultIperf3Path,
            Description = "Name on PATH or full path to the iperf3 executable."
        }
    ];

    public IReadOnlyList<PluginSettingAction> SettingsActions =>
    [
        new() { Label = "Check iperf3", Invoke = CheckIperf3Async }
    ];

    public void OnSettingsSaved()
    {
        try
        {
            _host?.RequestButtonRefresh(SpeedtestCommand.Name);
        }
        catch (Exception ex)
        {
            _host?.Logger.Warn($"Speedtest: button refresh failed: {ex.Message}");
        }
    }

    /// <summary>
    /// iperf3 is only needed for the LAN test, so it is reported only once a server is configured —
    /// users who only test their internet connection are not asked to install it.
    /// </summary>
    public IReadOnlyList<PluginRequirement> GetRequirements()
    {
        if (_settings is null || _settings.Iperf3Host.Length == 0)
            return [];

        return
        [
            new PluginRequirement
            {
                Id = "iperf3",
                Name = "iperf3",
                IsMet = Iperf3Tool.GetVersion(_settings.Iperf3Path) is not null,
                Message = "iperf3 is missing, so the LAN speed test is unavailable.",
                InstallHint = OperatingSystem.IsWindows()
                    ? "Download iperf3 from https://iperf.fr and set its path in the plugin settings."
                    : "Install the package iperf3."
            }
        ];
    }

    private async Task<string> CheckIperf3Async()
    {
        if (_settings is null)
            return "Plugin not initialized.";

        Version? version = Iperf3Tool.GetVersion(_settings.Iperf3Path);
        if (version is null)
            return $"iperf3 not found ('{_settings.Iperf3Path}').";

        if (_settings.Iperf3Host.Length == 0)
            return $"iperf3 {version} found. Set a server to test the connection.";

        string[] args = ["-c", _settings.Iperf3Host, "-p", _settings.Iperf3Port.ToString(), "-t", "1", "-f", "m"];
        List<string> output = [];
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        try
        {
            int exitCode = await Iperf3Tool.RunAsync(_settings.Iperf3Path, args,
                line => { lock (output) output.Add(line); },
                line => { lock (output) output.Add(line); },
                timeout.Token);

            return exitCode == 0
                ? $"iperf3 {version}: server {_settings.Iperf3Host}:{_settings.Iperf3Port} reachable."
                : output.LastOrDefault(l => l.Length > 0) ?? $"iperf3 exited with code {exitCode}.";
        }
        catch (OperationCanceledException)
        {
            return $"No answer from {_settings.Iperf3Host}:{_settings.Iperf3Port}.";
        }
        catch (Exception ex)
        {
            return $"Check failed: {ex.Message}";
        }
    }
}
