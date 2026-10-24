using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CoDDedupe.Core;

/// <summary>Runs CoD Tweaker's command-line apply after a share, so one click covers the whole "after an update" routine.</summary>
public static class TweakerBridge
{
    /// <summary>CoD Tweaker records where it last ran from; that is the exe to call.</summary>
    public static string? FindExe(Settings settings)
    {
        if (settings.TweakerExe is { Length: > 0 } given && File.Exists(given)) return given;
        try
        {
            string json = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoDTweaker", "settings.json");
            if (File.Exists(json))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(json));
                if (doc.RootElement.TryGetProperty("LastExePath", out var p) && p.GetString() is { Length: > 0 } path && File.Exists(path)) return path;
            }
        }
        catch { }
        return null;
    }

    public sealed record ApplyOutcome(int ExitCode, string Output);

    /// <summary>Applies the preset and returns the Tweaker's own report of what it changed.</summary>
    public static ApplyOutcome Apply(string exe, string preset, TimeSpan? timeout = null)
    {
        string logs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoDTweaker", "logs");
        var before = new DirectoryInfo(logs).Exists ? new DirectoryInfo(logs).GetFiles("apply-*.txt").Select(f => f.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
        using var p = Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--apply", preset }, UseShellExecute = false, CreateNoWindow = true });
        if (p == null) return new ApplyOutcome(1, "CoD Tweaker did not start.");
        if (!p.WaitForExit((int)(timeout ?? TimeSpan.FromMinutes(2)).TotalMilliseconds)) { try { p.Kill(); } catch { } return new ApplyOutcome(1, "CoD Tweaker did not finish in time."); }
        string output = "";
        try
        {
            var log = new DirectoryInfo(logs).GetFiles("apply-*.txt").Where(f => !before.Contains(f.FullName)).OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
            if (log != null) output = File.ReadAllText(log.FullName).Trim();
        }
        catch { }
        return new ApplyOutcome(p.ExitCode, output);
    }
}
