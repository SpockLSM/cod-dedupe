using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace CoDDedupe.Core;

public enum Store { Xbox, Steam, BattleNet, Other }

/// <summary>One Call of Duty install: the folder that holds cod.exe.</summary>
public sealed record Install(Store Store, string Root, string Drive, string VolumeKey)
{
    public string StoreName => Store switch { Store.Xbox => "Xbox app", Store.Steam => "Steam", Store.BattleNet => "Battle.net", _ => "Other" };
    public string Name { get; init; } = "";
}

/// <summary>Finds every Call of Duty install on the PC from the stores' own records.</summary>
public static class InstallFinder
{
    const string SteamAppId = "1938090";              // "Call of Duty" (the HQ) on Steam
    const string BattleNetProduct = "auks";           // "Call of Duty" (the HQ) on Battle.net
    const string XboxIdentity = "38985CA0.COREBase";  // "Call of Duty" (the HQ) on the Xbox app

    /// <summary>Tests plug in a fake answer here so they never see the real installs on the PC.</summary>
    public static Func<List<Install>>? DiscoveryOverride { get; set; }

    public static List<Install> FindAll(IReadOnlyDictionary<Store, string>? overrides = null)
    {
        if (DiscoveryOverride != null) return DiscoveryOverride();
        var list = new List<Install>();
        foreach (var store in new[] { Store.Xbox, Store.Steam, Store.BattleNet })
        {
            IEnumerable<string> roots;
            if (overrides != null && overrides.TryGetValue(store, out var given))
            {
                var dir = ResolveGameFolder(given) ?? throw new DirectoryNotFoundException($"No cod.exe found under the {store} folder you gave: {given}");
                roots = new[] { dir };
            }
            else
            {
                roots = store switch { Store.Xbox => FindXbox(), Store.Steam => FindSteam(), _ => FindBattleNet() };
            }
            foreach (var root in roots.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
                list.Add(Make(store, root));
        }
        NameRepeats(list);
        return list;
    }

    public static Install Make(Store store, string root)
    {
        root = Normalize(root);
        var i = new Install(store, root, Path.GetPathRoot(root)!.TrimEnd('\\'), SafeVolumeKey(root));
        return i with { Name = i.StoreName };
    }

    /// <summary>Guess the store from a folder the user picked by hand.</summary>
    public static Store GuessStore(string gameFolder)
    {
        if (File.Exists(Path.Combine(gameFolder, "MicrosoftGame.Config"))) return Store.Xbox;
        if (File.Exists(Path.Combine(gameFolder, "steam_api64.dll")) || gameFolder.Contains("steamapps", StringComparison.OrdinalIgnoreCase)) return Store.Steam;
        if (Path.GetFileName(gameFolder).StartsWith('_') || File.Exists(Path.Combine(gameFolder, ".flavor.info"))) return Store.BattleNet;
        return Store.Other;
    }

    public static void NameRepeats(List<Install> list)
    {
        foreach (var g in list.GroupBy(i => i.Store).Where(g => g.Count() > 1))
            foreach (var i in g.ToList())
                list[list.IndexOf(i)] = i with { Name = $"{i.StoreName} ({i.Drive})" };
    }

    static string Normalize(string p) => Path.GetFullPath(p).TrimEnd('\\');

    static string SafeVolumeKey(string root)
    {
        try { return Native.VolumeKey(root); }
        catch { return Path.GetPathRoot(root)!.ToUpperInvariant(); }
    }

    /// <summary>The folder with cod.exe, given that folder or a store's install folder above it.</summary>
    public static string? ResolveGameFolder(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        dir = dir.TrimEnd('\\');
        foreach (var c in new[] { dir, Path.Combine(dir, "Content"), Path.Combine(dir, "_retail_") })
            if (File.Exists(Path.Combine(c, "cod.exe"))) return Path.GetFullPath(c);
        if (Directory.Exists(dir))
            foreach (var sub in Directory.EnumerateDirectories(dir, "_*_"))   // other Battle.net flavours (_beta_, _ptr_)
                if (File.Exists(Path.Combine(sub, "cod.exe"))) return Path.GetFullPath(sub);
        return null;
    }

    public static IEnumerable<string> FindXbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            string driveRoot = drive.RootDirectory.FullName;
            var libraries = new List<string> { "XboxGames" };
            // The Xbox app marks each drive it installs to with .GamingRoot: "RGBX", a 32-bit count, then UTF-16 library paths.
            string marker = Path.Combine(driveRoot, ".GamingRoot");
            if (File.Exists(marker))
            {
                try
                {
                    var b = File.ReadAllBytes(marker);
                    if (b.Length > 8) libraries.AddRange(Encoding.Unicode.GetString(b, 8, b.Length - 8).Split('\0', StringSplitOptions.RemoveEmptyEntries));
                }
                catch { }
            }
            foreach (var lib in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string libDir = Path.IsPathRooted(lib) ? lib : Path.Combine(driveRoot, lib.TrimStart('\\'));
                if (!Directory.Exists(libDir)) continue;
                IEnumerable<string> games;
                try { games = Directory.EnumerateDirectories(libDir).ToList(); } catch { continue; }
                foreach (var game in games)
                {
                    string content = Path.Combine(game, "Content");
                    string config = Path.Combine(content, "MicrosoftGame.Config");
                    if (!File.Exists(Path.Combine(content, "cod.exe")) || !File.Exists(config)) continue;
                    bool match;
                    try { match = File.ReadAllText(config).Contains($"Name=\"{XboxIdentity}\"", StringComparison.OrdinalIgnoreCase); }
                    catch { continue; }
                    if (match) yield return content;
                }
            }
        }
    }

    public static IEnumerable<string> FindSteam()
    {
        var steamRoots = new List<string>();
        void Add(RegistryKey hive, string key, string value)
        {
            try { if (hive.OpenSubKey(key)?.GetValue(value) is string s && s.Length > 0) steamRoots.Add(s.Replace('/', '\\')); } catch { }
        }
        // Machine-wide InstallPath first: it keeps the folder's real capitalisation, the per-user SteamPath is lower-cased.
        Add(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        Add(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        Add(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");

        var libraries = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libraries.Add(root);
            foreach (var vdf in new[] { Path.Combine(root, "steamapps", "libraryfolders.vdf"), Path.Combine(root, "config", "libraryfolders.vdf") })
            {
                if (!File.Exists(vdf)) continue;
                string text;
                try { text = File.ReadAllText(vdf); } catch { continue; }
                foreach (Match m in Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\""))
                    libraries.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
            }
        }
        var found = new List<string>();
        foreach (var lib in libraries.Select(l => l.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string manifest = Path.Combine(lib, "steamapps", $"appmanifest_{SteamAppId}.acf");
            if (!File.Exists(manifest)) continue;
            string text;
            try { text = File.ReadAllText(manifest); } catch { continue; }
            var m = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
            if (m.Success && ResolveGameFolder(Path.Combine(lib, "steamapps", "common", m.Groups[1].Value)) is { } dir) found.Add(dir);
        }
        // Fallback: Windows' list of installed programs.
        foreach (var e in UninstallEntries())
            if (e.Key == $"Steam App {SteamAppId}" && ResolveGameFolder(e.InstallLocation) is { } dir) found.Add(dir);
        return found;
    }

    public static IEnumerable<string> FindBattleNet()
    {
        var dirs = new List<string>();
        foreach (var e in UninstallEntries())
            if (e.UninstallString.Contains($"--uid={BattleNetProduct}", StringComparison.OrdinalIgnoreCase) && e.InstallLocation.Length > 0)
                dirs.Add(e.InstallLocation);

        // Battle.net's own install list stores paths as plain text inside a binary file.
        string db = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Battle.net", "Agent", "product.db");
        if (File.Exists(db))
        {
            string text;
            try { text = Encoding.UTF8.GetString(File.ReadAllBytes(db)); } catch { text = ""; }
            foreach (Match m in Regex.Matches(text, @"[A-Za-z]:[/\\][^\x00-\x1f""*?<>|]{2,240}"))
            {
                string p = m.Value.Replace('/', '\\');
                // Binary data after the path can look like text; trim until a folder matches.
                for (int i = 0; i < 12 && p.Length > 3; i++, p = p[..^1])
                    if (File.Exists(Path.Combine(p, ".build.info"))) { dirs.Add(p); break; }
            }
        }
        var found = new List<string>();
        foreach (var d in dirs.Select(x => x.TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string info = Path.Combine(d, ".build.info");
            if (!File.Exists(info)) continue;
            bool ours;
            try { ours = File.ReadLines(info).Any(l => Regex.IsMatch(l, $@"\|{BattleNetProduct}\s*$")); } catch { continue; }
            if (ours && ResolveGameFolder(d) is { } dir) found.Add(dir);
        }
        return found;
    }

    sealed record UninstallEntry(string Key, string InstallLocation, string UninstallString);

    static IEnumerable<UninstallEntry> UninstallEntries()
    {
        var hives = new[]
        {
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (hive, path) in hives)
        {
            RegistryKey? key = null;
            try { key = hive.OpenSubKey(path); } catch { }
            if (key == null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                RegistryKey? sub = null;
                try { sub = key.OpenSubKey(name); } catch { }
                if (sub == null) continue;
                yield return new UninstallEntry(name, sub.GetValue("InstallLocation") as string ?? "", sub.GetValue("UninstallString") as string ?? "");
            }
        }
    }
}
