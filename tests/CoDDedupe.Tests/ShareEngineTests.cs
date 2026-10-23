using System.IO;
using CoDDedupe.Core;
using Xunit;

namespace CoDDedupe.Tests;

/// <summary>Runs the engine against a throwaway three-store tree with the quirks real installs have.</summary>
public class ShareEngineTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "CoDDedupeTests", Guid.NewGuid().ToString("N"));
    readonly string X, S, B;
    readonly byte[] _a = Bytes(5 << 20, 1), _b = Bytes(3 << 20, 2), _c1 = Bytes(2 << 20, 3), _c2 = Bytes(2 << 20, 4), _e = Bytes(1 << 20, 5);
    readonly List<string> _log = new();

    public ShareEngineTests()
    {
        X = Path.Combine(_root, "Xbox", "Call of Duty", "Content");
        S = Path.Combine(_root, "Steam", "Call of Duty HQ");
        B = Path.Combine(_root, "Bnet", "Call of Duty", "_retail_");
        foreach (var d in new[] { X, S, B })
        {
            Directory.CreateDirectory(Path.Combine(d, "main"));
            Directory.CreateDirectory(Path.Combine(d, "cod26"));
            File.WriteAllText(Path.Combine(d, "cod.exe"), "x");
            File.WriteAllBytes(Path.Combine(d, "cod26", "a.xsub"), _a);     // identical everywhere
            File.WriteAllBytes(Path.Combine(d, "main", "skip.xsub"), _a);   // in a skipped folder
            File.WriteAllBytes(Path.Combine(d, "f.dll"), _a);               // not a shareable type
        }
        foreach (var d in new[] { S, B })
        {
            File.WriteAllBytes(Path.Combine(d, "cod26", "b.xsub"), _b);     // only Steam and Battle.net
            File.WriteAllBytes(Path.Combine(d, "e.xpak"), _e);
            File.WriteAllBytes(Path.Combine(d, "c.ff"), _c2);
        }
        File.WriteAllBytes(Path.Combine(X, "c.ff"), _c1);                   // same name, different content on Xbox
        File.WriteAllBytes(Path.Combine(X, "d.ff"), _c1);
        File.WriteAllBytes(Path.Combine(X, "e.xpak"), Array.Empty<byte>()); // placeholder: not installed on Xbox
        File.WriteAllBytes(Path.Combine(S, "z.xsub" + ShareEngine.TmpSuffix), _b); // left by an interrupted run
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static byte[] Bytes(int n, int seed) { var b = new byte[n]; new Random(seed).NextBytes(b); return b; }

    List<Install> Installs() => new()
    {
        InstallFinder.Make(Store.Xbox, X), InstallFinder.Make(Store.Steam, S), InstallFinder.Make(Store.BattleNet, B),
    };

    ShareEngine Engine() => new(_log.Add);

    static uint Links(string dir, string rel) => Native.LinkCount(Path.Combine(dir, rel));

    [Fact]
    public void Links_identical_files_keeps_differences_and_never_touches_main()
    {
        var xboxKey = Native.FileKey(Path.Combine(X, "cod26", "a.xsub"));
        var xboxWritten = File.GetLastWriteTimeUtc(Path.Combine(X, "c.ff"));

        var r = Engine().Run(Installs(), dryRun: false, null, CancellationToken.None);

        Assert.Equal(0, r.Errors);
        Assert.Equal(5, r.Linked);          // a.xsub x2, b.xsub, e.xpak, c.ff (Steam<->Battle.net)
        Assert.Equal(1, r.Differ);          // c.ff on Xbox differs
        Assert.Equal(3u, Links(X, @"cod26\a.xsub"));
        Assert.Equal(2u, Links(S, @"cod26\b.xsub"));
        Assert.Equal(1u, Links(X, "c.ff"));
        Assert.Equal(2u, Links(S, "c.ff"));
        Assert.Equal(1u, Links(X, @"main\skip.xsub"));
        Assert.Equal(1u, Links(S, "f.dll"));
        Assert.Equal(xboxKey, Native.FileKey(Path.Combine(S, "cod26", "a.xsub")));      // Steam points at Xbox's copy
        Assert.Equal(xboxKey, Native.FileKey(Path.Combine(X, "cod26", "a.xsub")));      // and Xbox's copy is the same file as before
        Assert.Equal(xboxWritten, File.GetLastWriteTimeUtc(Path.Combine(X, "c.ff")));
        Assert.Equal(_c1, File.ReadAllBytes(Path.Combine(X, "c.ff")));
        Assert.Equal(_c2, File.ReadAllBytes(Path.Combine(B, "c.ff")));
        Assert.True(File.Exists(Path.Combine(S, "z.xsub")), "the interrupted-run leftover was put back");
        Assert.False(File.Exists(Path.Combine(S, "z.xsub" + ShareEngine.TmpSuffix)));
    }

    [Fact]
    public void Second_run_finds_everything_already_shared()
    {
        Engine().Run(Installs(), false, null, CancellationToken.None);
        var r = Engine().Run(Installs(), false, null, CancellationToken.None);
        Assert.Equal(0, r.Linked);
        Assert.Equal(5, r.AlreadyShared);
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void Dry_run_changes_nothing()
    {
        var r = Engine().Run(Installs(), dryRun: true, null, CancellationToken.None);
        Assert.Equal(5, r.Linked);
        Assert.Equal(1u, Links(S, @"cod26\a.xsub"));
        Assert.True(File.Exists(Path.Combine(S, "z.xsub" + ShareEngine.TmpSuffix)), "a dry run reports leftovers but doesn't touch them");
        Assert.Contains(_log, l => l.StartsWith("would link"));
    }

    [Fact]
    public void Main_store_order_decides_whose_copy_is_kept()
    {
        var installs = Installs();
        var steamFirst = new List<Install> { installs[1], installs[0], installs[2] };
        var steamKey = Native.FileKey(Path.Combine(S, "cod26", "a.xsub"));
        Engine().Run(steamFirst, false, null, CancellationToken.None);
        Assert.Equal(steamKey, Native.FileKey(Path.Combine(X, "cod26", "a.xsub")));
    }

    [Fact]
    public void Unshare_gives_a_store_its_own_copies_back()
    {
        Engine().Run(Installs(), false, null, CancellationToken.None);
        var steam = Installs()[1];
        Assert.Equal(4, Engine().SharedFiles(steam, CancellationToken.None).Count);

        var r = Engine().Unshare(steam, null, CancellationToken.None);

        Assert.Equal(4, r.Files);
        Assert.Equal(0, r.Errors);
        Assert.Equal(1u, Links(S, @"cod26\a.xsub"));
        Assert.Equal(2u, Links(X, @"cod26\a.xsub"));   // Xbox and Battle.net still share
        Assert.Equal(_a, File.ReadAllBytes(Path.Combine(S, "cod26", "a.xsub")));
        Assert.Empty(Engine().SharedFiles(steam, CancellationToken.None));
    }

    [Fact]
    public void Installs_on_different_volumes_are_left_alone()
    {
        var installs = Installs();
        var other = installs[2] with { VolumeKey = "FAKE-OTHER-VOLUME" };
        var r = Engine().Run(new[] { installs[0], installs[1], other }, false, null, CancellationToken.None);
        Assert.Equal(1u, Links(B, @"cod26\a.xsub"));
        Assert.Equal(2u, Links(X, @"cod26\a.xsub"));
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void Resolves_game_folders_and_guesses_stores()
    {
        Assert.Equal(X, InstallFinder.ResolveGameFolder(Path.Combine(_root, "Xbox", "Call of Duty")));
        Assert.Equal(B, InstallFinder.ResolveGameFolder(Path.Combine(_root, "Bnet", "Call of Duty")));
        Assert.Equal(S, InstallFinder.ResolveGameFolder(S));
        Assert.Null(InstallFinder.ResolveGameFolder(Path.Combine(_root, "nope")));
        Assert.Equal(Store.BattleNet, InstallFinder.GuessStore(B));
    }
}
