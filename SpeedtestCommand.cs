using LoupixDeck.Plugin.Speedtest.Providers;
using LoupixDeck.Plugin.Speedtest.Rendering;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest;

/// <summary>
/// Touch button that starts (or cancels) a speed test and renders its live progress and last result.
/// The provider is the command's only parameter, baked in through the plugin menu.
/// </summary>
internal sealed class SpeedtestCommand(SpeedtestRunner runner) : IDisplayImageCommand
{
    public const string Name = "Speedtest.Run";
    public const string ProviderParameter = "Provider";

    public CommandDescriptor Descriptor { get; } = new()
    {
        CommandName = Name,
        DisplayName = "Speedtest",
        Group = SpeedtestPlugin.Group,
        Icon = "\U000F04C5",
        Description = "Run a network speed test and show download, upload and ping on the button",
        ParameterTemplate = "({Provider})",
        Parameters = [new CommandParameter(ProviderParameter, typeof(string)) { DefaultValue = CloudflareProvider.ProviderId }],
        // Surfaced per provider through the plugin menu.
        HiddenFromMenu = true
    };

    public ButtonTargets SupportedTargets => ButtonTargets.TouchButton;

    public TimeSpan UpdateInterval => TimeSpan.FromSeconds(1);

    public bool RenderImage(CommandContext ctx, IRenderCanvas canvas)
    {
        ISpeedtestProvider? provider = Resolve(ctx);
        if (provider is null)
        {
            SpeedtestRenderer.Render(canvas, "Speedtest",
                new SpeedtestState(SpeedtestPhase.Error, Error: $"Unknown provider '{ProviderOf(ctx)}'"));
            return true;
        }

        SpeedtestRenderer.Render(canvas, provider.DisplayName, runner.GetState(provider.Id));
        return true;
    }

    public Task Execute(CommandContext ctx)
    {
        try
        {
            if (Resolve(ctx) is { } provider)
                runner.Toggle(provider);
            else
                ctx.Host.Logger.Warn($"Speedtest: unknown provider '{ProviderOf(ctx)}'.");
        }
        catch (Exception ex)
        {
            ctx.Host.Logger.Error("Speedtest: failed to start the test.", ex);
        }

        return Task.CompletedTask;
    }

    private ISpeedtestProvider? Resolve(CommandContext ctx) => runner.Find(ProviderOf(ctx));

    private static string ProviderOf(CommandContext ctx) =>
        ctx.Parameters is [{ Length: > 0 } provider, ..] ? provider.Trim() : CloudflareProvider.ProviderId;
}
