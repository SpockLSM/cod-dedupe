using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoDDedupe.Core;

/// <summary>Remembered choices. Same file the original script used, so an existing choice carries over.</summary>
public sealed class Settings
{
    /// <summary>"Xbox", "Steam" or "Battle.net".</summary>
    public string? Main { get; set; }
    public string? MainRoot { get; set; }
    public Dictionary<string, string>? ExtraFolders { get; set; }
    /// <summary>"light", "dark", or null to follow Windows.</summary>
    public string? Theme { get; set; }
    /// <summary>Keep running in the tray and say when an update left files that could be shared again.</summary>
    public bool WatchForUpdates { get; set; }
    /// <summary>A CoD Tweaker preset to apply after sharing ("After an update").</summary>
    public string? TweakerPreset { get; set; }
    /// <summary>Where CoD Tweaker is, when it can't be found through its own settings.</summary>
    public string? TweakerExe { get; set; }
    public bool CheckForUpdates { get; set; } = true;
    public string? DismissedUpdate { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public bool TrayHintShown { get; set; }

    /// <summary>Tests point this somewhere disposable.</summary>
    public static string? FolderOverride { get; set; }

    public static string Folder => FolderOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodSharedInstall");
    public static string LogFolder => Path.Combine(Folder, "logs");
    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public static Settings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public static string StoreKey(Store s) => s switch { Store.Xbox => "Xbox", Store.Steam => "Steam", Store.BattleNet => "Battle.net", _ => "Other" };

    public static Store? ParseStore(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "xbox" or "xbox app" => Store.Xbox,
        "steam" => Store.Steam,
        "battle.net" or "battlenet" or "bnet" or "blizzard" => Store.BattleNet,
        _ => null,
    };
}

public static class Launchers
{
    /// <summary>Tests plug in a fake answer here.</summary>
    public static Func<List<string>>? Override { get; set; }

    /// <summary>Names of the game or store apps that are open right now.</summary>
    public static List<string> Running()
    {
        if (Override != null) return Override();
        var names = new List<string>();
        void Check(string process, string label, string? pathContains = null)
        {
            try
            {
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(process))
                {
                    bool match = pathContains == null;
                    if (!match) { try { match = p.MainModule?.FileName?.Contains(pathContains!, StringComparison.OrdinalIgnoreCase) == true; } catch { match = true; } }
                    p.Dispose();
                    if (match && !names.Contains(label)) names.Add(label);
                }
            }
            catch { }
        }
        Check("cod", "Call of Duty");
        Check("steam", "Steam");
        Check("Battle.net", "Battle.net");
        Check("Agent", "Battle.net", "Battle.net");
        Check("XboxPcApp", "Xbox app");
        return names;
    }
}
