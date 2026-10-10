using System.Globalization;
using LoupixDeck.PluginSdk;

namespace LoupixDeck.Plugin.Speedtest.Rendering;

/// <summary>Draws a provider's <see cref="SpeedtestState"/> onto a touch button, scaled to the canvas.</summary>
internal static class SpeedtestRenderer
{
    private static readonly PluginColor Background = new(18, 20, 24);
    private static readonly PluginColor Track = new(48, 52, 60);
    private static readonly PluginColor Muted = new(150, 156, 168);
    private static readonly PluginColor DownloadColor = new(56, 189, 248);
    private static readonly PluginColor UploadColor = new(251, 146, 60);
    private static readonly PluginColor PingColor = new(163, 230, 53);
    private static readonly PluginColor ErrorColor = new(248, 113, 113);

    public static void Render(IRenderCanvas canvas, string title, SpeedtestState state)
    {
        canvas.Clear(Background);
        float s = Math.Min(canvas.Width, canvas.Height) / 90f;

        switch (state.Phase)
        {
            case SpeedtestPhase.Connecting or SpeedtestPhase.Latency or SpeedtestPhase.Download or SpeedtestPhase.Upload:
                RenderRunning(canvas, s, state);
                break;
            case SpeedtestPhase.Error:
                RenderError(canvas, s, title, state.Error);
                break;
            default:
                if (state.LastResult is { } result)
                    RenderResult(canvas, s, result);
                else
                    RenderIdle(canvas, s, title);
                break;
        }
    }

    private static void RenderIdle(IRenderCanvas canvas, float s, string title)
    {
        int w = canvas.Width;
        Symbol(canvas, "speedometer", w / 2, Px(30, s), Px(36, s), PluginColor.White);
        Text(canvas, title, 0, Px(54, s), w, Px(16, s), PluginColor.White, 13 * s, bold: true);
        Text(canvas, "Press to start", 0, Px(70, s), w, Px(14, s), Muted, 10 * s);
    }

    private static void RenderRunning(IRenderCanvas canvas, float s, SpeedtestState state)
    {
        (string label, PluginColor color) = state.Phase switch
        {
            SpeedtestPhase.Download => ("Download", DownloadColor),
            SpeedtestPhase.Upload => ("Upload", UploadColor),
            SpeedtestPhase.Latency => ("Ping", PingColor),
            _ => ("Connecting", Muted)
        };

        int w = canvas.Width;
        int h = canvas.Height;
        int size = Math.Min(w, h) - Px(24, s);
        int x = (w - size) / 2;
        int y = (h - size) / 2;
        int stroke = Math.Max(2, Px(4, s));
        canvas.DrawArc(x, y, size, size, 135, 270, stroke, Track);
        if (state.Fraction > 0)
            canvas.DrawArc(x, y, size, size, 135, (float)(270 * state.Fraction), stroke, color);

        bool showsRate = state.Phase is SpeedtestPhase.Download or SpeedtestPhase.Upload;
        string value = showsRate ? FormatMbps(state.Mbps) : "…";
        // Value and unit sit inside the ring, the phase label in the gap at the bottom.
        int inner = size - (2 * stroke) - Px(6, s);
        int innerX = (w - inner) / 2;
        Text(canvas, value, innerX, Px(28, s), inner, Px(24, s), PluginColor.White, FitFont(canvas, value, inner, 20 * s), bold: true);
        Text(canvas, showsRate ? "Mbit/s" : string.Empty, 0, Px(50, s), w, Px(14, s), Muted, 11 * s);
        Text(canvas, label, 0, Px(72, s), w, Px(14, s), color, 10 * s, bold: true);
    }

    private static void RenderResult(IRenderCanvas canvas, float s, SpeedtestResult result)
    {
        int row = Px(28, s);
        int y = (canvas.Height - (3 * row)) / 2;

        Row(canvas, s, y, row, "arrow-down", DownloadColor, FormatMbps(result.DownloadMbps), "Mbps");
        Row(canvas, s, y + row, row, "arrow-up", UploadColor, FormatMbps(result.UploadMbps), "Mbps");
        Row(canvas, s, y + (2 * row), row, "timer-outline", PingColor,
            result.PingMs is { } ping ? ping.ToString("F0", CultureInfo.InvariantCulture) : "–",
            result.PingMs is null ? string.Empty : "ms");
    }

    private static void RenderError(IRenderCanvas canvas, float s, string title, string? error)
    {
        int w = canvas.Width;
        Symbol(canvas, "alert-circle", w / 2, Px(23, s), Px(30, s), ErrorColor);
        Text(canvas, title, 0, Px(40, s), w, Px(16, s), PluginColor.White, 12 * s, bold: true);
        string message = error ?? "Failed";
        Text(canvas, message, Px(2, s), Px(58, s), w - Px(4, s), Px(28, s), ErrorColor, FitFont(canvas, message, (w - Px(4, s)) * 2, 10 * s));
    }

    /// <summary>Icon, value in large type, unit in small type right after it.</summary>
    private static void Row(IRenderCanvas canvas, float s, int y, int height, string symbol, PluginColor color,
        string value, string unit)
    {
        int icon = Px(20, s);
        int x = Px(4, s);
        Symbol(canvas, symbol, x + (icon / 2), y + (height / 2), icon, color);

        int textX = x + icon + Px(4, s);
        int textW = canvas.Width - textX - Px(3, s);
        float unitSize = 10 * s;
        float unitW = unit.Length == 0 ? 0 : canvas.MeasureText(unit, unitSize) + Px(3, s);
        float valueSize = FitFont(canvas, value, (int)(textW - unitW), 19 * s);
        int valueW = (int)MathF.Ceiling(canvas.MeasureText(value, valueSize, bold: true));

        canvas.DrawText(value, textX, y, valueW + 1, height, PluginColor.White, valueSize,
            TextHAlign.Left, TextVAlign.Middle, bold: true);
        if (unit.Length > 0)
            canvas.DrawText(unit, textX + valueW + Px(3, s), y + Px(2, s), (int)unitW, height, Muted, unitSize,
                TextHAlign.Left, TextVAlign.Middle);
    }

    /// <summary>
    /// Draws a symbol centered on a point. The host stretches a glyph to fill its box, so the box must
    /// have the glyph's own aspect ratio (width / height, measured from the MDI font).
    /// </summary>
    private static void Symbol(IRenderCanvas canvas, string id, int centerX, int centerY, int height, PluginColor color)
    {
        double aspect = id switch
        {
            "speedometer" => 1.151,
            "timer-outline" => 0.857,
            _ => 1.0
        };
        int width = (int)Math.Round(height * aspect);
        canvas.DrawSymbol(id, centerX - (width / 2), centerY - (height / 2), width, height, color);
    }

    private static void Text(IRenderCanvas canvas, string text, int x, int y, int w, int h, PluginColor color,
        float size, bool bold = false) =>
        canvas.DrawText(text, x, y, w, h, color, size, TextHAlign.Center, TextVAlign.Middle, bold);

    /// <summary>Shrinks the font until the text fits the given width.</summary>
    private static float FitFont(IRenderCanvas canvas, string text, int width, float size)
    {
        while (size > 6 && canvas.MeasureText(text, size, bold: true) > width)
            size -= 0.5f;
        return size;
    }

    private static int Px(float value, float scale) => (int)MathF.Round(value * scale);

    internal static string FormatMbps(double? mbps) => mbps switch
    {
        null => "–",
        >= 100 => mbps.Value.ToString("F0", CultureInfo.InvariantCulture),
        >= 10 => mbps.Value.ToString("F1", CultureInfo.InvariantCulture),
        _ => mbps.Value.ToString("F2", CultureInfo.InvariantCulture)
    };
}
