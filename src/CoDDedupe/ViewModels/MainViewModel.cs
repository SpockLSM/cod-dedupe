using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CoDDedupe.Core;
using Spock.AppKit;
using Spock.AppKit.Mvvm;

namespace CoDDedupe.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    public const string GitHubRepo = "SpockLSM/cod-dedupe";
    public const string RunKeyName = "CoDDedupe";

    readonly Settings _settings;
    readonly SynchronizationContext _ui;
    CancellationTokenSource? _cts;
    StreamWriter? _logFile;
    readonly object _logLock = new();

    public ObservableCollection<InstallVm> Installs { get; } = new();
    public ObservableCollection<string> RunningLaunchers { get; } = new();
    public Settings AppSettings => _settings;

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand AfterUpdateCommand { get; }
    public RelayCommand UnshareCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CheckLaunchersCommand { get; }
    public RelayCommand BrowsePresetCommand { get; }
    public RelayCommand ClearPresetCommand { get; }
    public RelayCommand CheckUpdatesCommand { get; }
    public RelayCommand OpenUpdateCommand { get; }
    public RelayCommand DismissUpdateCommand { get; }

    public Func<string, string, bool>? Confirm { get; set; }
    public Action<string, string>? Alert { get; set; }
    public Func<string?>? PickFolder { get; set; }
    public Func<string?>? PickPreset { get; set; }

    /// <summary>Raised by the tray watcher when an update left files that could be shared again.</summary>
    public event Action<string>? WatchNotice;

    public MainViewModel()
    {
        _settings = Settings.Load();
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        RefreshCommand = new RelayCommand(() => _ = Refresh(), () => !IsBusy);
        PreviewCommand = new RelayCommand(() => RunShare(dryRun: true), () => !IsBusy && CanRun);
        ShareCommand = new RelayCommand(() => RunShare(dryRun: false), () => !IsBusy && CanRun);
        AfterUpdateCommand = new RelayCommand(() => RunShare(dryRun: false, thenTweaker: true), () => !IsBusy && CanRun);
        UnshareCommand = new RelayCommand(RunUnshare, () => !IsBusy && UnshareTarget != null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => CanCancel);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        OpenLogsCommand = new RelayCommand(() => { Directory.CreateDirectory(Settings.LogFolder); Shell.OpenFolder(Settings.LogFolder); });
        CheckLaunchersCommand = new RelayCommand(RefreshLaunchers);
        BrowsePresetCommand = new RelayCommand(() => { var p = PickPreset?.Invoke(); if (p != null) TweakerPreset = p; });
        ClearPresetCommand = new RelayCommand(() => TweakerPreset = null, () => HasTweakerPreset);
        CheckUpdatesCommand = new RelayCommand(() => CheckForUpdates(manual: true));
        OpenUpdateCommand = new RelayCommand(() => { if (UpdateAvailable != null) Shell.OpenUrl(UpdateAvailable.Url); });
        DismissUpdateCommand = new RelayCommand(() => { if (UpdateAvailable != null) { _settings.DismissedUpdate = UpdateAvailable.Tag; _settings.Save(); UpdateAvailable = null; } });
    }

    // ----- state -----

    bool _isBusy;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanCancel)); RaiseAll(); } } }

    /// <summary>True during a share, preview or unshare (not while just measuring).</summary>
    public bool CanCancel => IsBusy && _cts != null && !_cts.IsCancellationRequested;

    string _status = "";
    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    string _phase = "";
    public string Phase { get => _phase; private set => SetProperty(ref _phase, value); }

    double _progress;
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    bool _progressIndeterminate;
    public bool ProgressIndeterminate { get => _progressIndeterminate; private set => SetProperty(ref _progressIndeterminate, value); }

    string _summary = "";
    public string Summary { get => _summary; private set { if (SetProperty(ref _summary, value)) OnPropertyChanged(nameof(HasSummary)); } }
    public bool HasSummary => Summary.Length > 0;

    readonly StringBuilder _log = new();
    public string LogText => _log.ToString();

    InstallVm? _unshareTarget;
    public InstallVm? UnshareTarget { get => _unshareTarget; set { if (SetProperty(ref _unshareTarget, value)) UnshareCommand.RaiseCanExecuteChanged(); } }

    public InstallVm? Main => Installs.FirstOrDefault(i => i.IsMain);
    public bool HasInstalls => Installs.Count > 0;
    public bool LaunchersRunning => RunningLaunchers.Count > 0;
    public string LaunchersText => "Close these first: " + string.Join(", ", RunningLaunchers) + ". Make sure no store is downloading or updating Call of Duty.";
    public bool CanRun => Main != null && Installs.Count(i => i.CanShare) >= 2;

    string _hint = "";
    public string Hint { get => _hint; private set => SetProperty(ref _hint, value); }

    long _couldFree;
    /// <summary>Bytes a share would free right now, from the last measure.</summary>
    public long CouldFree { get => _couldFree; private set => SetProperty(ref _couldFree, value); }

    public bool WatchForUpdates
    {
        get => _settings.WatchForUpdates;
        set { if (_settings.WatchForUpdates != value) { _settings.WatchForUpdates = value; _settings.Save(); OnPropertyChanged(); } }
    }

    /// <summary>Registers the exe with --tray in the user's Startup entries.</summary>
    public bool StartWithWindows
    {
        get => StartupRun.IsSet(RunKeyName);
        set
        {
            if (value) StartupRun.Set(RunKeyName, Shell.ExePath, "--tray"); else StartupRun.Clear(RunKeyName);
            OnPropertyChanged();
        }
    }

    public string? TweakerPreset
    {
        get => _settings.TweakerPreset;
        set
        {
            if (_settings.TweakerPreset == value) return;
            _settings.TweakerPreset = value; _settings.Save();
            OnPropertyChanged(); OnPropertyChanged(nameof(HasTweakerPreset)); OnPropertyChanged(nameof(TweakerPresetDisplay));
            ClearPresetCommand.RaiseCanExecuteChanged();
        }
    }
    public bool HasTweakerPreset => !string.IsNullOrEmpty(_settings.TweakerPreset);
    public string TweakerPresetDisplay => HasTweakerPreset ? Path.GetFileName(_settings.TweakerPreset!) : "none chosen";

    ReleaseInfo? _update;
    public ReleaseInfo? UpdateAvailable { get => _update; private set { if (SetProperty(ref _update, value)) { OnPropertyChanged(nameof(HasUpdate)); OnPropertyChanged(nameof(UpdateText)); } } }
    public bool HasUpdate => _update != null;
    public string UpdateText => _update == null ? "" : $"CoD Dedupe {_update.Tag} is available (you have {AppVersion.Short}).";

    void RaiseAll()
    {
        foreach (var c in new[] { RefreshCommand, PreviewCommand, ShareCommand, AfterUpdateCommand, UnshareCommand, CancelCommand, AddFolderCommand }) c.RaiseCanExecuteChanged();
    }

    // ----- discovery and measuring -----

    public async Task Refresh()
    {
        IsBusy = true; ProgressIndeterminate = true; Phase = "Looking for Call of Duty installs...";
        try
        {
            var found = await Task.Run(() =>
            {
                var list = InstallFinder.FindAll();
                if (_settings.ExtraFolders != null)
                    foreach (var (root, storeName) in _settings.ExtraFolders)
                        if (InstallFinder.ResolveGameFolder(root) is { } dir && !list.Any(i => i.Root.Equals(dir, StringComparison.OrdinalIgnoreCase)))
                            list.Add(InstallFinder.Make(Enum.TryParse<Store>(storeName, out var s) ? s : InstallFinder.GuessStore(dir), dir));
                InstallFinder.NameRepeats(list);
                return list;
            });

            Installs.Clear();
            foreach (var i in found)
            {
                var vm = new InstallVm(i);
                vm.MainChosen += OnMainChosen;
                Installs.Add(vm);
            }
            // Installs can only share with others on the same drive.
            foreach (var g in Installs.GroupBy(i => i.Model.VolumeKey))
            {
                bool alone = g.Count() == 1 && Installs.Count > 1;
                foreach (var i in g) { i.CanShare = !alone; i.Note = alone ? $"No other install on {i.Drive}: hard links can't cross drives" : ""; }
            }
            var main = Installs.FirstOrDefault(i => i.Root.Equals(_settings.MainRoot, StringComparison.OrdinalIgnoreCase))
                    ?? Installs.FirstOrDefault(i => Settings.StoreKey(i.Store) == _settings.Main);
            if (main != null) main.IsMain = true;
            UnshareTarget = Installs.FirstOrDefault(i => !i.IsMain) ?? Installs.FirstOrDefault();
            OnPropertyChanged(nameof(HasInstalls));
            UpdateHint();
            RefreshLaunchers();

            Phase = "Measuring...";
            foreach (var vm in Installs) vm.Detail = "Measuring...";
            await MeasureAsync();
            Phase = "";
            Status = Installs.Count == 0 ? "No Call of Duty install found from the Xbox app, Steam or Battle.net. Use Add folder if yours is somewhere unusual."
                   : $"{Installs.Count} install{(Installs.Count == 1 ? "" : "s")} found.";
        }
        finally { ProgressIndeterminate = false; IsBusy = false; }
    }

    /// <summary>Reads every install and fills in what is shared, what could be, and what an update patched in place.</summary>
    public async Task<Dictionary<Install, InstallStats>> MeasureAsync()
    {
        var models = Installs.Select(i => i.Model).ToList();
        var stats = await Task.Run(() => Analysis.Measure(models, ShareManifest.Load(), CancellationToken.None));
        foreach (var vm in Installs) if (stats.TryGetValue(vm.Model, out var s)) vm.Stats = s;
        CouldFree = Analysis.CouldFree(stats, Main?.Model);
        UpdateHint();
        return stats;
    }

    void OnMainChosen(InstallVm chosen)
    {
        foreach (var i in Installs) if (i != chosen && i.IsMain) i.IsMain = false;
        _settings.Main = Settings.StoreKey(chosen.Store);
        _settings.MainRoot = chosen.Root;
        _settings.Save();
        if (UnshareTarget == chosen) UnshareTarget = Installs.FirstOrDefault(i => i != chosen);
        OnPropertyChanged(nameof(Main));
        if (Installs.All(i => i.Stats != null)) CouldFree = Analysis.CouldFree(Installs.ToDictionary(i => i.Model, i => i.Stats!), chosen.Model);
        UpdateHint();
        RaiseAll();
    }

    void UpdateHint()
    {
        if (Installs.Count == 0) Hint = "";
        else if (Installs.Count < 2) Hint = "Only one install found: there is nothing to share it with.";
        else if (Main == null) Hint = "Pick your main store: the one you play from most. Its files are kept as they are; the other stores are pointed at them.";
        else if (!CanRun) Hint = "Installs must be on the same drive to share files. Move one with its store's move option, then refresh.";
        else if (CouldFree >= Analysis.NoticeThreshold) Hint = $"{ShareEngine.Gb(CouldFree)} in the other stores is not shared yet (an update, most likely). Click Share files to merge it.";
        else Hint = $"Ready. {Main.Name} is the main store and is never modified.";
    }

    void AddFolder()
    {
        var dir = PickFolder?.Invoke();
        if (dir == null) return;
        var game = InstallFinder.ResolveGameFolder(dir);
        if (game == null) { Alert?.Invoke("Not a Call of Duty folder", $"No cod.exe found under:\n{dir}"); return; }
        _settings.ExtraFolders ??= new();
        _settings.ExtraFolders[game] = InstallFinder.GuessStore(game).ToString();
        _settings.Save();
        _ = Refresh();
    }

    public void RefreshLaunchers()
    {
        var now = Launchers.Running();
        if (now.SequenceEqual(RunningLaunchers)) return;
        RunningLaunchers.Clear();
        foreach (var n in now) RunningLaunchers.Add(n);
        OnPropertyChanged(nameof(LaunchersRunning));
        OnPropertyChanged(nameof(LaunchersText));
    }

    // ----- the tray watcher -----

    long _lastNoticed;

    /// <summary>Called every so often while the app waits in the tray: measures quietly and says when a share is worth it.</summary>
    public async Task WatchTick()
    {
        if (!WatchForUpdates || IsBusy || Installs.Count < 2 || Main == null) return;
        if (Launchers.Running().Count > 0) return;   // an update may still be in progress
        try
        {
            await MeasureAsync();
        }
        catch { return; }
        long could = CouldFree;
        if (could < Analysis.NoticeThreshold) { _lastNoticed = 0; return; }
        if (Math.Abs(could - _lastNoticed) < 256L * 1024 * 1024) return;   // already said so
        _lastNoticed = could;
        WatchNotice?.Invoke($"{ShareEngine.Gb(could)} could be shared again after the update. Open CoD Dedupe and click Share files.");
    }

    // ----- runs -----

    async void RunShare(bool dryRun, bool thenTweaker = false)
    {
        var main = Main;
        if (main == null) return;
        RefreshLaunchers();
        if (!dryRun && LaunchersRunning) { Alert?.Invoke("Close the game and the store apps first", LaunchersText); return; }

        var ordered = new List<Install> { main.Model };
        ordered.AddRange(Installs.Where(i => i != main && i.CanShare).Select(i => i.Model));
        if (!dryRun)
        {
            string others = string.Join(", ", ordered.Skip(1).Select(i => i.Name));
            string extra = thenTweaker && HasTweakerPreset ? $"\n\nThen CoD Tweaker applies {TweakerPresetDisplay}." : "";
            if (!(Confirm?.Invoke(thenTweaker ? "After an update: share, then apply the preset?" : "Share game files?",
                    $"Identical game data files in {others} will be replaced by links to {main.Name}'s copies.\n\n" +
                    $"{main.Name} is never modified. Every file is compared byte by byte first.{extra}\n\nContinue?") ?? true)) return;
        }

        var manifest = dryRun ? null : ShareManifest.Load();
        await Execute(dryRun ? "preview" : thenTweaker ? "after-update" : "share", manifest, (engine, progress, ct) =>
        {
            var r = engine.Run(ordered, dryRun, progress, ct);
            if (manifest != null) { try { manifest.Save(); } catch { } }
            var sb = new StringBuilder();
            sb.AppendLine($"Already shared:          {r.AlreadyShared,8:N0} files  {ShareEngine.Gb(r.AlreadySharedBytes)}");
            sb.AppendLine($"{(dryRun ? "Would share now:" : "Shared now:"),-24} {r.Linked,8:N0} files  {ShareEngine.Gb(r.LinkedBytes)}");
            sb.AppendLine($"Different, kept apart:   {r.Differ,8:N0} files  {ShareEngine.Gb(r.DifferBytes)}" + (r.Differ > 0 ? "   (a store on another patch: update every store, then run again)" : ""));
            sb.AppendLine($"Errors:                  {r.Errors,8:N0}");
            if (r.Leftovers > 0) sb.AppendLine($"Temp files left behind:  {r.Leftovers,8:N0}   (cleaned up on the next run)");
            if (!dryRun)
                foreach (var (drive, (before, after)) in r.FreeSpace)
                    sb.AppendLine($"Free space on {drive}  {ShareEngine.Gb(before)} -> {ShareEngine.Gb(after)}  (freed {ShareEngine.Gb(after - before)})");
            string status = dryRun ? $"Preview done: {r.Linked:N0} files ({ShareEngine.Gb(r.LinkedBytes)}) would be shared, {r.AlreadyShared:N0} already are."
                                   : $"Done: shared {r.Linked:N0} files, freed {ShareEngine.Gb(r.Freed)}.";
            if (thenTweaker && !dryRun)
            {
                string tweak = ApplyTweakerPreset(engine);
                sb.AppendLine(tweak);
                status += " " + tweak;
            }
            sb.Append($"Took {r.Elapsed.TotalSeconds:N0} s.");
            return (sb.ToString(), status);
        });
    }

    /// <summary>The second half of "after an update": hand the preset to CoD Tweaker. Returns a one-line account.</summary>
    string ApplyTweakerPreset(ShareEngine engine)
    {
        if (!HasTweakerPreset) return "No CoD Tweaker preset chosen, so nothing was applied.";
        string preset = _settings.TweakerPreset!;
        if (!File.Exists(preset)) return $"Preset not found: {preset}";
        string? exe = TweakerBridge.FindExe(_settings);
        if (exe == null) return "CoD Tweaker was not found (run it once so it records where it lives).";
        Log($"Applying {Path.GetFileName(preset)} with CoD Tweaker...");
        var a = TweakerBridge.Apply(exe, preset);
        foreach (var line in a.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)) Log("  " + line.TrimEnd());
        return a.ExitCode == 0 ? $"CoD Tweaker applied {Path.GetFileName(preset)}." : $"CoD Tweaker exited with code {a.ExitCode}; see its log.";
    }

    async void RunUnshare()
    {
        var target = UnshareTarget;
        if (target == null) return;
        RefreshLaunchers();
        if (LaunchersRunning) { Alert?.Invoke("Close the game and the store apps first", LaunchersText); return; }

        long need; int count;
        try
        {
            var files = new ShareEngine(_ => { }).SharedFiles(target.Model, CancellationToken.None);
            need = files.Sum(f => f.Size); count = files.Count;
        }
        catch (Exception ex) { Alert?.Invoke("Couldn't read the install", ex.Message); return; }
        if (count == 0) { Alert?.Invoke("Nothing to unshare", $"{target.Name} already has its own copies of everything."); return; }
        long free = new DriveInfo(target.Drive).AvailableFreeSpace;
        if (need >= free - 2L * 1024 * 1024 * 1024)
        {
            Alert?.Invoke("Not enough free space", $"Giving {target.Name} its own copies needs {ShareEngine.Gb(need)}, but {target.Drive} has {ShareEngine.Gb(free)} free.");
            return;
        }
        if (!(Confirm?.Invoke("Unshare?", $"Copy {count:N0} files ({ShareEngine.Gb(need)}) so {target.Name} has its own separate files again?") ?? true)) return;

        var model = target.Model;
        var manifest = ShareManifest.Load();
        await Execute("unshare", manifest, (engine, progress, ct) =>
        {
            var r = engine.Unshare(model, progress, ct);
            try { manifest.Save(); } catch { }
            return ($"{model.Name}: {r.Files:N0} files now separate ({ShareEngine.Gb(r.Bytes)}), {r.Errors} errors.",
                    $"Done: {model.Name} has its own copies again.");
        });
    }

    async Task Execute(string kind, ShareManifest? manifest, Func<ShareEngine, IProgress<ShareProgress>, CancellationToken, (string Summary, string Status)> work)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true; Summary = ""; Progress = 0; ProgressIndeterminate = true;
        _log.Clear(); OnPropertyChanged(nameof(LogText));
        Directory.CreateDirectory(Settings.LogFolder);
        string logPath = Path.Combine(Settings.LogFolder, $"cod-dedupe-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.log");
        _logFile = new StreamWriter(logPath, false, Encoding.UTF8);
        var progress = new Progress<ShareProgress>(p =>
        {
            Phase = p.Total > 0 ? $"{p.Phase}  {p.Done:N0} / {p.Total:N0}  ·  {ShareEngine.Gb(p.LinkedBytes)} so far" : p.Phase;
            ProgressIndeterminate = p.Total == 0;
            Progress = p.Total > 0 ? 100.0 * p.Done / p.Total : 0;
        });
        var engine = new ShareEngine(Log) { Manifest = manifest };
        Log($"CoD Dedupe {kind} started");
        try
        {
            var (summary, status) = await Task.Run(() => work(engine, progress, _cts.Token));
            Summary = summary; Status = status;
            Log(summary);
        }
        catch (OperationCanceledException) { Status = "Cancelled. Nothing is left half-done: every file is either linked or still a normal file."; Log(Status); }
        catch (Exception ex) { Status = "Stopped: " + ex.Message; Log(Status); Alert?.Invoke("Stopped", ex.Message); }
        finally
        {
            Phase = ""; Progress = 0; ProgressIndeterminate = false;
            lock (_logLock) { _logFile.Dispose(); _logFile = null; }
            Log($"Log saved to {logPath}");
            _cts.Dispose(); _cts = null;
            IsBusy = false;
            _ = Refresh();
        }
    }

    void Log(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_logLock) { try { _logFile?.WriteLine(stamped); } catch { } }
        _ui.Post(_ =>
        {
            // Keep the on-screen log readable: per-file lines go to the file, the window shows the rest.
            if (line.StartsWith("linked ") || line.StartsWith("would link ") || line.StartsWith("unshared ")) return;
            _log.AppendLine(stamped);
            OnPropertyChanged(nameof(LogText));
        }, null);
    }

    // ----- updates -----

    public void CheckForUpdates(bool manual)
    {
        if (!manual && !_settings.CheckForUpdates) return;
        _ = Task.Run(async () =>
        {
            var latest = await new UpdateCheck(GitHubRepo, "CoD Dedupe").LatestAsync();
            _ui.Post(_ =>
            {
                if (latest == null) { if (manual) Status = "Couldn't check for updates (offline, or GitHub didn't answer)."; return; }
                if (!UpdateCheck.IsNewer(latest)) { if (manual) Status = $"You have the latest version ({AppVersion.Short})."; return; }
                if (!manual && latest.Tag == _settings.DismissedUpdate) return;
                UpdateAvailable = latest;
                if (manual) Status = UpdateText;
            }, null);
        });
    }
}
