using System.Collections.Concurrent;

namespace CoDDedupe.Core;

/// <summary>What one install looks like right now, without changing anything.</summary>
public sealed class InstallStats
{
    public int Files, SharedFiles, CouldShareFiles, OnlyHereFiles, ChangedInPlace;
    public long Bytes, SharedBytes, CouldShareBytes, OnlyHereBytes;
    public string? Error;

    /// <summary>The one-line account shown under the install.</summary>
    public string Detail
    {
        get
        {
            if (Error != null) return "Couldn't read: " + Error;
            var parts = new List<string> { $"{Files:N0} game data files, {ShareEngine.Gb(Bytes)}" };
            if (SharedBytes > 0) parts.Add($"{ShareEngine.Gb(SharedBytes)} shared with another store");
            if (CouldShareBytes > 0) parts.Add($"{ShareEngine.Gb(CouldShareBytes)} not shared yet");
            if (OnlyHereBytes > 0) parts.Add($"{ShareEngine.Gb(OnlyHereBytes)} only in this store");
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>A warning worth showing, or empty.</summary>
    public string Warning => ChangedInPlace > 0
        ? $"{ChangedInPlace:N0} shared file{(ChangedInPlace == 1 ? " was" : "s were")} rewritten in place since the last share (an update patched them), so every store sharing them got the patched bytes. If another store reports damaged files, run its repair, then share again."
        : "";
}

/// <summary>Measures the installs: what is shared, what could be, what exists in one store only, and what an update patched in place.</summary>
public static class Analysis
{
    /// <summary>Bytes in "not shared yet" worth telling the user about.</summary>
    public const long NoticeThreshold = 512L * 1024 * 1024;

    public static Dictionary<Install, InstallStats> Measure(IReadOnlyList<Install> installs, ShareManifest? manifest, CancellationToken ct, Action<string>? log = null)
    {
        var engine = new ShareEngine(log ?? (_ => { }));
        var inv = new ConcurrentDictionary<Install, Dictionary<string, FileEntry>>();
        var ids = new ConcurrentDictionary<string, (string Key, uint Links)>(StringComparer.OrdinalIgnoreCase);
        var stats = installs.ToDictionary(i => i, _ => new InstallStats());

        Parallel.ForEach(installs, new ParallelOptions { CancellationToken = ct }, install =>
        {
            try
            {
                var map = engine.Inventory(install, dryRun: true, ct);
                inv[install] = map;
                foreach (var f in map.Values)
                {
                    ct.ThrowIfCancellationRequested();
                    try { ids[f.Path] = Native.Identity(f.Path); } catch { }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { stats[install].Error = ex.Message; inv[install] = new(StringComparer.OrdinalIgnoreCase); }
        });

        // Every file name, and which installs have it.
        var owners = new Dictionary<string, List<Install>>(StringComparer.OrdinalIgnoreCase);
        foreach (var install in installs)
            foreach (var rel in inv[install].Keys)
            {
                if (!owners.TryGetValue(rel, out var list)) owners[rel] = list = new();
                list.Add(install);
            }

        foreach (var (rel, who) in owners)
        {
            foreach (var install in who)
            {
                var f = inv[install][rel];
                var s = stats[install];
                s.Files++; s.Bytes += f.Size;
                if (who.Count == 1) { s.OnlyHereFiles++; s.OnlyHereBytes += f.Size; continue; }
                ids.TryGetValue(f.Path, out var me);
                bool shared = false, could = false;
                foreach (var other in who)
                {
                    if (other == install || other.VolumeKey != install.VolumeKey) continue;
                    var of = inv[other][rel];
                    if (ids.TryGetValue(of.Path, out var oid) && oid.Key == me.Key) { shared = true; break; }
                    if (of.Size == f.Size) could = true;
                }
                if (shared) { s.SharedFiles++; s.SharedBytes += f.Size; }
                else if (could) { s.CouldShareFiles++; s.CouldShareBytes += f.Size; }
            }
        }

        if (manifest != null)
            foreach (var install in installs)
                stats[install].ChangedInPlace = manifest.ChangedSince(install, p => ids.TryGetValue(p, out var id) ? id : null).Count;

        return stats;
    }

    /// <summary>"Not shared yet" bytes in every install except the main one: what a share could free.</summary>
    public static long CouldFree(Dictionary<Install, InstallStats> stats, Install? main) =>
        stats.Where(kv => kv.Key != main).Sum(kv => kv.Value.CouldShareBytes);
}
