using System.IO;

namespace CoDDedupe.Core;

/// <summary>
/// CoDDedupe.exe --preview | --share | --after-update | --unshare &lt;store&gt;
///               [--main &lt;Xbox|Steam|Battle.net&gt;] [--folder &lt;install&gt; ...] [--no-discover] [--preset file.json] [--tweaker CoDTweaker.exe]
/// Runs without a window, for a scheduled task after game updates. Exit codes: 0 done, 1 error,
/// 2 the game or a store app is open, 3 no installs (or no main store) to work with.
/// </summary>
public static class CliRun
{
    static readonly string[] Verbs = { "--preview", "--share", "--after-update", "--unshare" };

    public static bool Wanted(string[] args) => args.Any(a => Verbs.Contains(a, StringComparer.OrdinalIgnoreCase));

    static string? Arg(string[] args, string name)
    {
        int i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    static bool Has(string[] args, string name) => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args, TextWriter log)
    {
        var settings = Settings.Load();
        string verb = args.First(a => Verbs.Contains(a, StringComparer.OrdinalIgnoreCase)).ToLowerInvariant();
        bool dryRun = verb == "--preview";

        // Installs: found the usual way, plus any folders given on the command line or remembered in settings.
        List<Install> installs;
        try
        {
            installs = Has(args, "--no-discover") ? new List<Install>() : InstallFinder.FindAll();
            var extra = new List<(string Root, string? Store)>();
            if (settings.ExtraFolders != null) extra.AddRange(settings.ExtraFolders.Select(kv => (kv.Key, (string?)kv.Value)));
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("--folder", StringComparison.OrdinalIgnoreCase)) extra.Add((args[i + 1], null));
            foreach (var (root, storeName) in extra)
            {
                var dir = InstallFinder.ResolveGameFolder(root);
                if (dir == null) { log.WriteLine($"No cod.exe under {root}; skipped."); continue; }
                if (installs.Any(x => x.Root.Equals(dir, StringComparison.OrdinalIgnoreCase))) continue;
                var store = Enum.TryParse<Store>(storeName, out var s) ? s : InstallFinder.GuessStore(dir);
                installs.Add(InstallFinder.Make(store, dir));
            }
            InstallFinder.NameRepeats(installs);
        }
        catch (Exception ex) { log.WriteLine("Couldn't look for installs: " + ex.Message); return 1; }

        if (installs.Count == 0) { log.WriteLine("No Call of Duty install found."); return 3; }
        foreach (var i in installs) log.WriteLine($"  {i.Name,-12} {i.Root}");

        if (verb == "--unshare") return Unshare(args, installs, log);

        Install? main;
        if (Arg(args, "--main") is { } mainArg)
        {
            main = PickInstall(installs, mainArg);
            if (main == null) { log.WriteLine($"--main {mainArg} matches none of the installs above."); return 3; }
        }
        else
        {
            var mainStore = Settings.ParseStore(settings.Main);
            main = installs.FirstOrDefault(i => i.Root.Equals(settings.MainRoot, StringComparison.OrdinalIgnoreCase))
                ?? installs.FirstOrDefault(i => mainStore != null && i.Store == mainStore);
            if (main == null) { log.WriteLine("No main store chosen. Pick one in the app, or pass --main Xbox|Steam|Battle.net (or the install folder)."); return 3; }
        }
        log.WriteLine($"Main store: {main.Name} (never modified)");

        var running = Launchers.Running();
        if (!dryRun && running.Count > 0) { log.WriteLine("Close these first: " + string.Join(", ", running) + ". Nothing was changed."); return 2; }

        var ordered = new List<Install> { main };
        ordered.AddRange(installs.Where(i => i != main && i.VolumeKey == main.VolumeKey));
        var others = installs.Where(i => i != main && i.VolumeKey != main.VolumeKey).ToList();
        foreach (var o in others) log.WriteLine($"{o.Name} is on another drive than {main.Name}; hard links can't cross drives, so it is left alone.");
        if (ordered.Count < 2) { log.WriteLine("Nothing to share with."); return 3; }

        var manifest = dryRun ? null : ShareManifest.Load();
        var engine = new ShareEngine(line => { if (!line.StartsWith("linked ") && !line.StartsWith("would link ")) log.WriteLine(line); }) { Manifest = manifest };
        int lastReport = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.Total > 0 && (p.Done - lastReport >= 2000 || p.Done == p.Total)) { lastReport = p.Done; log.WriteLine($"  {p.Done:N0} / {p.Total:N0} compared, {ShareEngine.Gb(p.LinkedBytes)} {(dryRun ? "would be " : "")}shared so far"); }
        });
        ShareResult r;
        try { r = engine.Run(ordered, dryRun, progress, CancellationToken.None); }
        catch (Exception ex) { log.WriteLine("Stopped: " + ex.Message); return 1; }
        if (manifest != null) { try { manifest.Save(); } catch { } }

        log.WriteLine($"Already shared:          {r.AlreadyShared,8:N0} files  {ShareEngine.Gb(r.AlreadySharedBytes)}");
        log.WriteLine($"{(dryRun ? "Would share now:" : "Shared now:"),-24} {r.Linked,8:N0} files  {ShareEngine.Gb(r.LinkedBytes)}");
        log.WriteLine($"Different, kept apart:   {r.Differ,8:N0} files  {ShareEngine.Gb(r.DifferBytes)}");
        log.WriteLine($"Errors:                  {r.Errors,8:N0}");
        if (!dryRun) foreach (var (drive, (before, after)) in r.FreeSpace) log.WriteLine($"Free space on {drive}  {ShareEngine.Gb(before)} -> {ShareEngine.Gb(after)}  (freed {ShareEngine.Gb(after - before)})");
        log.WriteLine($"Took {r.Elapsed.TotalSeconds:N0} s.");

        if (verb == "--after-update")
        {
            string? preset = Arg(args, "--preset") ?? settings.TweakerPreset;
            string? exe = Arg(args, "--tweaker") ?? TweakerBridge.FindExe(settings);
            if (preset == null) log.WriteLine("No CoD Tweaker preset set (--preset, or pick one in the app); nothing applied.");
            else if (!File.Exists(preset)) { log.WriteLine($"Preset not found: {preset}"); return 1; }
            else if (exe == null) { log.WriteLine("CoD Tweaker not found: run it once, or pass --tweaker <path to CoDTweaker.exe>."); return 1; }
            else
            {
                log.WriteLine($"Applying {Path.GetFileName(preset)} with CoD Tweaker...");
                var a = TweakerBridge.Apply(exe, preset);
                if (a.Output.Length > 0) log.WriteLine(a.Output);
                log.WriteLine(a.ExitCode == 0 ? "CoD Tweaker preset applied." : $"CoD Tweaker exited with code {a.ExitCode}.");
                if (a.ExitCode != 0) return 1;
            }
        }
        return r.Errors > 0 ? 1 : 0;
    }

    /// <summary>An install named by store ("Steam") or by its folder.</summary>
    static Install? PickInstall(List<Install> installs, string text)
    {
        var store = Settings.ParseStore(text);
        if (store != null) return installs.FirstOrDefault(i => i.Store == store);
        var dir = InstallFinder.ResolveGameFolder(text);
        return dir == null ? null : installs.FirstOrDefault(i => i.Root.Equals(dir, StringComparison.OrdinalIgnoreCase));
    }

    static int Unshare(string[] args, List<Install> installs, TextWriter log)
    {
        var target = Arg(args, "--unshare") is { } which ? PickInstall(installs, which) : null;
        if (target == null) { log.WriteLine("Which store? --unshare Xbox|Steam|Battle.net (or the install folder)"); return 3; }
        var running = Launchers.Running();
        if (running.Count > 0) { log.WriteLine("Close these first: " + string.Join(", ", running) + ". Nothing was changed."); return 2; }
        var engine = new ShareEngine(line => { if (!line.StartsWith("unshared ")) log.WriteLine(line); }) { Manifest = ShareManifest.Load() };
        var files = engine.SharedFiles(target, CancellationToken.None);
        long need = files.Sum(f => f.Size);
        if (files.Count == 0) { log.WriteLine($"{target.Name} already has its own copies of everything."); return 0; }
        long free = new DriveInfo(target.Drive).AvailableFreeSpace;
        if (need >= free - 2L * 1024 * 1024 * 1024) { log.WriteLine($"Needs {ShareEngine.Gb(need)} free on {target.Drive}, only {ShareEngine.Gb(free)} is."); return 1; }
        var r = engine.Unshare(target, null, CancellationToken.None);
        try { engine.Manifest!.Save(); } catch { }
        log.WriteLine($"{target.Name}: {r.Files:N0} files now separate ({ShareEngine.Gb(r.Bytes)}), {r.Errors} errors.");
        return r.Errors > 0 ? 1 : 0;
    }

    /// <summary>Progress that reports on the calling thread, since there is no UI thread to post to.</summary>
    sealed class SyncProgress : IProgress<ShareProgress>
    {
        readonly Action<ShareProgress> _h;
        readonly object _lock = new();
        public SyncProgress(Action<ShareProgress> h) => _h = h;
        public void Report(ShareProgress value) { lock (_lock) _h(value); }
    }
}
