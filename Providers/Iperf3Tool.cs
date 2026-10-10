using System.Diagnostics;
using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.Speedtest.Providers;

/// <summary>Locates and runs the iperf3 executable. Nothing is cached so an install is picked up at once.</summary>
internal static partial class Iperf3Tool
{
    /// <summary>The iperf3 version, or <c>null</c> when the executable cannot be run.</summary>
    public static Version? GetVersion(string path)
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(path, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return null;

            string output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(true); } catch { /* ignore */ }
                return null;
            }

            // e.g. "iperf 3.17.1 (cJSON 1.7.15)"
            Match match = VersionRegex().Match(output);
            return match.Success ? Version.Parse(match.Groups[1].Value) : new Version(3, 0);
        }
        catch
        {
            // Not on PATH, or not executable.
            return null;
        }
    }

    /// <summary>Runs iperf3, streaming stdout/stderr lines to the callbacks; kills it on cancellation.</summary>
    public static async Task<int> RunAsync(string path, IEnumerable<string> args, Action<string> onOutput,
        Action<string> onError, CancellationToken cancellationToken)
    {
        ProcessStartInfo info = new(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            info.ArgumentList.Add(arg);

        using Process process = new() { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onError(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(true); } catch { /* already gone */ }
            }
        }

        // Flush the async readers so the final summary line is not lost.
        process.WaitForExit();
        return process.ExitCode;
    }

    [GeneratedRegex(@"iperf\s+(\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();
}
