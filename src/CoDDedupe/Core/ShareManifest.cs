using System.IO;
using System.Text.Json;

namespace CoDDedupe.Core;

public sealed class ManifestEntry
{
    public string Key { get; set; } = "";
    public long Size { get; set; }
    public DateTime WriteUtc { get; set; }
}

/// <summary>
/// What the last share left linked: for every shared file, the data it pointed at and when that data was
/// last written. A later pass can then tell a file a store replaced (unlinked, harmless) from one a store
/// patched in place (still linked, so every store now has the patched bytes).
/// </summary>
public sealed class ShareManifest
{
    public DateTime Saved { get; set; }
    /// <summary>Lower-cased full path -> what was shared there.</summary>
    public Dictionary<string, ManifestEntry> Files { get; set; } = new();

    readonly object _lock = new();

    public static string DefaultPath => Path.Combine(Settings.Folder, "shared-manifest.json");

    public static ShareManifest Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<ShareManifest>(File.ReadAllText(path)) ?? new ShareManifest();
        }
        catch { }
        return new ShareManifest();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        lock (_lock)
        {
            Saved = DateTime.UtcNow;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = false }));
        }
    }

    public int Count { get { lock (_lock) return Files.Count; } }

    public void Record(string path, string key, long size, DateTime writeUtc)
    {
        lock (_lock) Files[path.ToLowerInvariant()] = new ManifestEntry { Key = key, Size = size, WriteUtc = writeUtc };
    }

    public void Forget(string path)
    {
        lock (_lock) Files.Remove(path.ToLowerInvariant());
    }

    public sealed record Change(string Path, DateTime Then, DateTime Now, long SizeThen, long SizeNow);

    /// <summary>
    /// Shared files under this install whose data changed since the share while still being linked:
    /// an update patched them in place, so the other stores got the patched bytes too.
    /// </summary>
    public List<Change> ChangedSince(Install install, Func<string, (string Key, uint Links)?>? identity = null)
    {
        var changes = new List<Change>();
        string prefix = install.Root.ToLowerInvariant() + "\\";
        KeyValuePair<string, ManifestEntry>[] entries;
        lock (_lock) entries = Files.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        foreach (var (path, entry) in entries)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var id = identity?.Invoke(path) ?? Native.Identity(path);
                if (id.Links <= 1) continue;          // the store replaced the file with its own copy: fine
                var info = new FileInfo(path);
                var write = info.LastWriteTimeUtc;
                if (info.Length != entry.Size || Math.Abs((write - entry.WriteUtc).TotalSeconds) > 2)
                    changes.Add(new Change(path, entry.WriteUtc, write, entry.Size, info.Length));
            }
            catch { }
        }
        return changes;
    }
}
