using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace CoDDedupe.Core;

public sealed record FileEntry(string Path, long Size);

public sealed class ShareProgress
{
    public string Phase { get; init; } = "";
    public int Done { get; init; }
    public int Total { get; init; }
    public long LinkedBytes { get; init; }
}

public sealed class ShareResult
{
    public int AlreadyShared, Linked, Differ, Errors, Leftovers;
    public long AlreadySharedBytes, LinkedBytes, DifferBytes;
    public Dictionary<string, (long Before, long After)> FreeSpace { get; } = new();
    public TimeSpan Elapsed;
    public long Freed => FreeSpace.Values.Sum(v => v.After - v.Before);

    readonly object _lock = new();
    internal void AddAlreadyShared(long bytes) { lock (_lock) { AlreadyShared++; AlreadySharedBytes += bytes; } }
    internal void AddLinked(long bytes) { lock (_lock) { Linked++; LinkedBytes += bytes; } }
    internal void AddDiffer(long bytes) { lock (_lock) { Differ++; DifferBytes += bytes; } }
    internal void AddError() { lock (_lock) Errors++; }
    internal void AddLeftover() { lock (_lock) Leftovers++; }
    internal long LinkedBytesNow { get { lock (_lock) return LinkedBytes; } }
}

public sealed class UnshareResult
{
    public int Files, Errors;
    public long Bytes;
}

/// <summary>
/// Turns game data files that are byte-for-byte identical across installs into hard links.
/// The first install in the list is the main one: its files are never modified.
/// </summary>
public sealed class ShareEngine
{
    public static readonly string[] ShareExtensions = { ".xsub", ".xpak", ".ff", ".fp" };
    // Written by the game at run time, or store-specific: never shared.
    public static readonly string[] SkipFolders = { "main", "shadercache", "xpak_cache", "D3D12", "BlizzardBrowser" };
    public const string TmpSuffix = ".cod-shared.tmp";
    public const string CopySuffix = ".cod-shared.copy";

    readonly Action<string> _log;
    readonly object _logLock = new();

    /// <summary>Files compared at once. The compare is a sequential read of two files, so a few streams keep an SSD busy.</summary>
    public int Parallelism { get; set; } = 4;

    /// <summary>When set, a real run records every shared file here so later passes can spot in-place patches.</summary>
    public ShareManifest? Manifest { get; set; }

    public ShareEngine(Action<string> log) { _log = log; }

    void Log(string line) { lock (_logLock) _log(line); }

    public Dictionary<string, FileEntry> Inventory(Install install, bool dryRun, CancellationToken ct)
    {
        var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(install.Root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (path.EndsWith(TmpSuffix, StringComparison.OrdinalIgnoreCase) || path.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase))
            {
                RepairLeftover(path, dryRun);
                continue;
            }
            string rel = path[(install.Root.Length + 1)..];
            if (!ShareExtensions.Contains(Path.GetExtension(rel), StringComparer.OrdinalIgnoreCase)) continue;
            var parts = rel.Split('\\');
            bool skip = false;
            for (int i = 0; i < parts.Length - 1 && !skip; i++) skip = SkipFolders.Contains(parts[i], StringComparer.OrdinalIgnoreCase);
            if (skip) continue;
            long len = new FileInfo(path).Length;
            if (len == 0) continue;   // placeholders for content a store has not installed
            map[rel] = new FileEntry(path, len);
        }
        return map;
    }

    // Put back anything an interrupted run left half-done.
    void RepairLeftover(string path, bool dryRun)
    {
        if (dryRun) { Log($"leftover from an interrupted run: {path}"); return; }
        if (path.EndsWith(CopySuffix, StringComparison.OrdinalIgnoreCase)) { Native.Delete(path); Log($"removed partial copy {path}"); return; }
        string orig = path[..^TmpSuffix.Length];
        if (File.Exists(orig)) { Native.Delete(path); Log($"removed leftover {path}"); }
        else { File.Move(path, orig); Log($"restored leftover {path}"); }
    }

    /// <summary>Can we create hard links inside this install's folder?</summary>
    public bool CanLinkInto(Install target, string samplePath)
    {
        string test = Path.Combine(target.Root, ".cod-dedupe-test.tmp");
        try
        {
            if (File.Exists(test)) Native.Delete(test);
            Native.CreateHardLink(test, samplePath);
            Native.Delete(test);
            return true;
        }
        catch (Exception ex)
        {
            Log($"Can't create links in {target.Name}'s folder, so it is left out: {ex.Message}");
            return false;
        }
    }

    /// <param name="installs">Main install first. Only installs on the same drive as another can share.</param>
    public ShareResult Run(IReadOnlyList<Install> installs, bool dryRun, IProgress<ShareProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var result = new ShareResult();
        var groups = installs.GroupBy(i => i.VolumeKey).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        foreach (var g in groups)
            if (!result.FreeSpace.ContainsKey(g[0].Drive))
                result.FreeSpace[g[0].Drive] = (new DriveInfo(g[0].Drive).AvailableFreeSpace, 0);

        var equal = new ConcurrentDictionary<string, bool>();   // "keptKey|otherKey" -> same bytes, so a file is read once per pair
        foreach (var members in groups)
        {
            ct.ThrowIfCancellationRequested();
            var inv = new Dictionary<Install, Dictionary<string, FileEntry>>();
            foreach (var m in members)
            {
                progress?.Report(new ShareProgress { Phase = $"Reading {m.Name}..." });
                inv[m] = Inventory(m, dryRun, ct);
                Log($"{m.Name}: {inv[m].Count:N0} game data files, {Gb(inv[m].Values.Sum(f => f.Size))}");
            }

            // Each store's folder must accept links before anything is relinked there.
            var first = members.FirstOrDefault(m => inv[m].Count > 0);
            if (first != null)
            {
                var sample = inv[first].Values.First().Path;
                foreach (var other in members.Where(m => m != first && inv[m].Count > 0).ToList())
                    if (!dryRun && !CanLinkInto(other, sample)) inv[other].Clear();
            }

            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in members) keys.UnionWith(inv[m].Keys);
            var candidates = keys.Where(k => members.Count(m => inv[m].ContainsKey(k)) >= 2).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();
            Log($"Comparing {candidates.Count:N0} files found in more than one store on {members[0].Drive}, {Math.Max(1, Parallelism)} at a time...");

            int done = 0, total = candidates.Count;
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Parallelism), CancellationToken = ct };
            Parallel.ForEach(candidates, options, rel =>
            {
                int n = Interlocked.Increment(ref done);
                if (n % 100 == 0 || n == total)
                    progress?.Report(new ShareProgress { Phase = "Comparing and linking", Done = n, Total = total, LinkedBytes = result.LinkedBytesNow });
                CompareOne(members, inv, rel, dryRun, equal, result);
            });
        }

        foreach (var d in result.FreeSpace.Keys.ToList())
            result.FreeSpace[d] = (result.FreeSpace[d].Before, new DriveInfo(d).AvailableFreeSpace);
        result.Elapsed = sw.Elapsed;
        return result;
    }

    /// <summary>One file name across the stores: the main store's copy is kept, identical copies elsewhere become links to it.</summary>
    void CompareOne(List<Install> members, Dictionary<Install, Dictionary<string, FileEntry>> inv, string rel, bool dryRun,
        ConcurrentDictionary<string, bool> equal, ShareResult result)
    {
        var kept = new List<(Install Owner, FileEntry File, string Key)>();   // distinct copies so far, main store first
        foreach (var m in members)
        {
            if (!inv[m].TryGetValue(rel, out var f)) continue;
            try
            {
                string key = Native.FileKey(f.Path);
                bool matched = false;
                foreach (var k in kept)
                {
                    if (k.File.Size != f.Size) continue;
                    if (k.Key == key)
                    {
                        matched = true;
                        result.AddAlreadyShared(f.Size);
                        RecordShared(k.File.Path, f.Path, key, f.Size, dryRun);
                        break;
                    }
                    string ck = k.Key + "|" + key;
                    bool same = equal.GetOrAdd(ck, _ => Native.ContentEqual(k.File.Path, f.Path));
                    if (!same) continue;
                    matched = true;
                    if (dryRun) Log($"would link [{m.Name}] {rel} -> [{k.Owner.Name}]");
                    else
                    {
                        Relink(f.Path, k.File.Path, m.Name, k.Owner.Name, rel, result);
                        RecordShared(k.File.Path, f.Path, k.Key, f.Size, dryRun);
                    }
                    result.AddLinked(f.Size);
                    break;
                }
                if (!matched)
                {
                    if (kept.Count > 0) { result.AddDiffer(f.Size); Log($"differs, kept separate: [{m.Name}] {rel}"); }
                    kept.Add((m, f, key));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.AddError(); Log($"ERROR [{m.Name}] {rel}: {ex.Message}"); }
        }
    }

    void RecordShared(string keptPath, string otherPath, string key, long size, bool dryRun)
    {
        if (dryRun || Manifest == null) return;
        try
        {
            var write = File.GetLastWriteTimeUtc(keptPath);
            Manifest.Record(keptPath, key, size, write);
            Manifest.Record(otherPath, key, size, write);
        }
        catch { }
    }

    void Relink(string path, string keepPath, string name, string keptName, string rel, ShareResult result)
    {
        string tmp = path + TmpSuffix;
        File.Move(path, tmp);
        try { Native.CreateHardLink(path, keepPath); }
        catch { File.Move(tmp, path); throw; }
        try { Native.Delete(tmp); }
        catch (Exception ex) { result.AddLeftover(); Log($"could not delete {tmp} (removed on the next run): {ex.Message}"); }
        Log($"linked [{name}] {rel} -> [{keptName}]");
    }

    /// <summary>Files in this install that share their data with another install.</summary>
    public List<FileEntry> SharedFiles(Install install, CancellationToken ct) =>
        Inventory(install, dryRun: true, ct).Values.Where(f => Native.LinkCount(f.Path) > 1).ToList();

    /// <summary>Give an install its own separate copies again.</summary>
    public UnshareResult Unshare(Install install, IProgress<ShareProgress>? progress, CancellationToken ct)
    {
        var files = SharedFiles(install, ct);
        var r = new UnshareResult();
        int done = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++done % 20 == 0 || done == files.Count)
                progress?.Report(new ShareProgress { Phase = $"Giving {install.Name} its own copies", Done = done, Total = files.Count, LinkedBytes = r.Bytes });
            try
            {
                string copy = f.Path + CopySuffix, tmp = f.Path + TmpSuffix;
                File.Copy(f.Path, copy, overwrite: true);
                File.Move(f.Path, tmp);
                File.Move(copy, f.Path);
                Native.Delete(tmp);
                r.Files++; r.Bytes += f.Size;
                Manifest?.Forget(f.Path);
                Log($"unshared {f.Path}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { r.Errors++; Log($"ERROR {f.Path}: {ex.Message}"); }
        }
        return r;
    }

    public static string Gb(long bytes) => $"{bytes / 1073741824.0:N1} GB";
}
