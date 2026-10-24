using System.IO;
using CoDDedupe.Core;
using Xunit;

namespace CoDDedupe.Tests;

/// <summary>The share manifest, in-place change detection, the measuring pass and the command line, on a throwaway three-store tree.</summary>
public class ManifestAndCliTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CoDDedupeTests", Guid.NewGuid().ToString("N"));
    readonly string X, S, B;
    readonly byte[] _a = Bytes(5 << 20, 1), _b = Bytes(3 << 20, 2), _c1 = Bytes(2 << 20, 3), _c2 = Bytes(2 << 20, 4), _e = Bytes(1 << 20, 5);

    public ManifestAndCliTests()
    {
        // Laid out the way the real stores do it, so the command line can tell which store each folder is.
        X = Path.Combine(_root, "XboxGames", "Call of Duty", "Content");
        S = Path.Combine(_root, "Steam", "steamapps", "common", "Call of Duty HQ");
        B = Path.Combine(_root, "Call of Duty", "_retail_");
        foreach (var d in new[] { X, S, B })
        {
            Directory.CreateDirectory(Path.Combine(d, "cod26"));
            File.WriteAllText(Path.Combine(d, "cod.exe"), "x");
            File.WriteAllBytes(Path.Combine(d, "cod26", "a.xsub"), _a);     // identical everywhere
        }
        File.WriteAllText(Path.Combine(X, "MicrosoftGame.Config"), "<Game><Identity Name=\"38985CA0.COREBase\"/></Game>");
        foreach (var d in new[] { S, B })
        {
            File.WriteAllBytes(Path.Combine(d, "cod26", "b.xsub"), _b);     // only Steam and Battle.net
            File.WriteAllBytes(Path.Combine(d, "e.xpak"), _e);
            File.WriteAllBytes(Path.Combine(d, "c.ff"), _c2);
        }
        File.WriteAllBytes(Path.Combine(X, "c.ff"), _c1);                   // same name and size, different content on Xbox
        File.WriteAllBytes(Path.Combine(X, "d.ff"), _c1);                   // only on Xbox
        // The files were "installed" an hour ago, so a later in-place write stands out.
        foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddHours(-1));

        Settings.FolderOverride = Path.Combine(_root, "app");
        Launchers.Override = () => new List<string>();
        InstallFinder.DiscoveryOverride = () => new List<Install>();   // never the real installs on this PC
    }

    public void Dispose()
    {
        Settings.FolderOverride = null;
        Launchers.Override = null;
        InstallFinder.DiscoveryOverride = null;
        Directory.Delete(_root, recursive: true);
    }

    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    List<Install> Installs() => new()
    {
        InstallFinder.Make(Store.Xbox, X), InstallFinder.Make(Store.Steam, S), InstallFinder.Make(Store.BattleNet, B),
    };

    static uint Links(string dir, string rel) => Native.LinkCount(Path.Combine(dir, rel));

    [Fact]
    public void A_share_records_a_manifest_and_in_place_patches_are_noticed_later()
    {
        var installs = Installs();
        var manifest = new ShareManifest();
        var r = new ShareEngine(_ => { }) { Manifest = manifest }.Run(installs, dryRun: false, null, CancellationToken.None);

        Assert.Equal(5, r.Linked);
        Assert.Equal(1, r.Differ);
        Assert.Equal(9, manifest.Count);   // a: 3 names, b: 2, e: 2, c (Steam + Battle.net): 2

        string path = Path.Combine(_root, "m.json");
        manifest.Save(path);
        var back = ShareManifest.Load(path);
        Assert.Equal(9, back.Count);
        Assert.Empty(back.ChangedSince(installs[1]));

        // An update patches a shared file in place through Steam's name: every store now holds the patched bytes.
        File.WriteAllBytes(Path.Combine(S, "cod26", "a.xsub"), Bytes(5 << 20, 9));
        // Another update replaces a shared file outright: the link count drops, which is harmless.
        File.Delete(Path.Combine(S, "cod26", "b.xsub"));
        File.WriteAllBytes(Path.Combine(S, "cod26", "b.xsub"), Bytes(3 << 20, 10));

        var steam = back.ChangedSince(installs[1]);
        Assert.Single(steam);
        Assert.EndsWith("a.xsub", steam[0].Path);
        Assert.Single(back.ChangedSince(installs[0]));   // Xbox shares that data, so it sees the change too
        Assert.Single(back.ChangedSince(installs[2]));

        var stats = Analysis.Measure(installs, back, CancellationToken.None);
        Assert.Equal(1, stats[installs[0]].ChangedInPlace);
        Assert.Contains("rewritten in place", stats[installs[0]].Warning);
    }

    [Fact]
    public void Measuring_tells_shared_from_could_share_from_only_here()
    {
        var installs = Installs();
        var x = installs[0]; var s = installs[1];

        var before = Analysis.Measure(installs, null, CancellationToken.None);
        Assert.Equal(3, before[x].Files);                                   // a, c, d
        Assert.Equal(0, before[x].SharedBytes);
        Assert.Equal((5L << 20) + (2L << 20), before[x].CouldShareBytes);   // a and c have same-size twins elsewhere
        Assert.Equal(2L << 20, before[x].OnlyHereBytes);                    // d
        Assert.Equal(4, before[s].Files);
        Assert.Equal(0, before[s].OnlyHereBytes);
        Assert.Equal(2 * ((5L << 20) + (3L << 20) + (1L << 20) + (2L << 20)), Analysis.CouldFree(before, x));
        Assert.Contains("not shared yet", before[s].Detail);

        new ShareEngine(_ => { }).Run(installs, dryRun: false, null, CancellationToken.None);

        var after = Analysis.Measure(installs, null, CancellationToken.None);
        Assert.Equal(5L << 20, after[x].SharedBytes);                       // a
        Assert.Equal(2L << 20, after[x].CouldShareBytes);                   // c still looks shareable by size; a Preview says it isn't
        Assert.Equal((5L << 20) + (3L << 20) + (1L << 20) + (2L << 20), after[s].SharedBytes);
        Assert.Equal(0, after[s].CouldShareBytes);
        Assert.Contains("shared with another store", after[s].Detail);
    }

    [Fact]
    public void Command_line_runs_preview_share_and_after_update_with_the_given_folders()
    {
        var log = new StringWriter();
        string[] folders = { "--no-discover", "--folder", X, "--folder", S, "--folder", B, "--main", "Xbox" };

        int code = CliRun.Run(new[] { "--preview" }.Concat(folders).ToArray(), log);
        Assert.Equal(0, code);
        Assert.Contains("Would share now:", log.ToString());
        Assert.Equal(1u, Links(S, "cod26\\a.xsub"));
        Assert.False(File.Exists(ShareManifest.DefaultPath));

        Launchers.Override = () => new List<string> { "Steam" };
        Assert.Equal(2, CliRun.Run(new[] { "--share" }.Concat(folders).ToArray(), log));
        Assert.Equal(1u, Links(S, "cod26\\a.xsub"));
        Launchers.Override = () => new List<string>();

        code = CliRun.Run(new[] { "--share" }.Concat(folders).ToArray(), log);
        Assert.Equal(0, code);
        Assert.Equal(3u, Links(X, "cod26\\a.xsub"));
        Assert.Contains("Shared now:", log.ToString());
        Assert.True(File.Exists(ShareManifest.DefaultPath));
        Assert.Equal(9, ShareManifest.Load().Count);

        // No main store and no --main: nothing to do.
        Assert.Equal(3, CliRun.Run(new[] { "--share", "--no-discover", "--folder", X, "--folder", S }, log));

        // After an update with no preset chosen: shares, then says why nothing was applied.
        log = new StringWriter();
        code = CliRun.Run(new[] { "--after-update" }.Concat(folders).ToArray(), log);
        Assert.Equal(0, code);
        Assert.Contains("Already shared:", log.ToString());
        Assert.Contains("No CoD Tweaker preset", log.ToString());

        // Unshare through the command line gives Steam its own files back.
        code = CliRun.Run(new[] { "--unshare", "Steam", "--no-discover", "--folder", X, "--folder", S, "--folder", B }, log);
        Assert.Equal(0, code);
        Assert.Equal(1u, Links(S, "cod26\\a.xsub"));
        Assert.Equal(2u, Links(X, "cod26\\a.xsub"));
    }
}
