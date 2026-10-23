using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using CoDDedupe.Core;

namespace CoDDedupe.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    readonly Settings _settings;
    readonly SynchronizationContext _ui;
    CancellationTokenSource? _cts;
    StreamWriter? _logFile;

    public ObservableCollection<InstallVm> Installs { get; } = new();
    public ObservableCollection<string> RunningLaunchers { get; } = new();

    public RelayCommand RefreshCommand { get; }
    public RelayCommand PreviewCommand { get; }
    public RelayCommand ShareCommand { get; }
    public RelayCommand UnshareCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddFolderCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CheckLaunchersCommand { get; }

    public Func<string, string, bool>? Confirm { get; set; }
    public Action<string, string>? Alert { get; set; }
    public Func<string?>? PickFolder { get; set; }

    public MainViewModel()
    {
        _settings = Settings.Load();
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        RefreshCommand = new RelayCommand(() => Refresh(), () => !IsBusy);
        PreviewCommand = new RelayCommand(() => RunShare(dryRun: true), () => !IsBusy && CanRun);
        ShareCommand = new RelayCommand(() => RunShare(dryRun: false), () => !IsBusy && CanRun);
        UnshareCommand = new RelayCommand(RunUnshare, () => !IsBusy && UnshareTarget != null);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => CanCancel);
        AddFolderCommand = new RelayCommand(AddFolder, () => !IsBusy);
        OpenLogsCommand = new RelayCommand(() => { Directory.CreateDirectory(Settings.LogFolder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Settings.LogFolder}\"") { UseShellExecute = true }); });
        CheckLaunchersCommand = new RelayCommand(RefreshLaunchers);
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

    void RaiseAll()
    {
        foreach (var c in new[] { RefreshCommand, PreviewCommand, ShareCommand, UnshareCommand, CancelCommand, AddFolderCommand }) c.RaiseCanExecuteChanged();
    }

    // ----- discovery -----

    public async Task Refresh()
    {
        IsBusy = true; ProgressIndeterminate = true; Phase = "Looking for Call of Duty installs...";
        try
        {
            var overrides = new Dictionary<Store, string>();
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
            await Task.Run(() =>
            {
                var engine = new ShareEngine(_ => { });
                Parallel.ForEach(Installs.ToList(), vm =>
                {
                    try
                    {
                        var inv = engine.Inventory(vm.Model, dryRun: true, CancellationToken.None);
                        long shared = 0; int sharedFiles = 0;
                        foreach (var f in inv.Values) { if (Native.LinkCount(f.Path) > 1) { shared += f.Size; sharedFiles++; } }
                        string detail = $"{inv.Count:N0} game data files, {ShareEngine.Gb(inv.Values.Sum(f => f.Size))}" +
                                        (sharedFiles > 0 ? $"  ·  {ShareEngine.Gb(shared)} shared with another store" : "");
                        _ui.Post(_ => vm.Detail = detail, null);
                    }
                    catch (Exception ex) { _ui.Post(_ => vm.Detail = "Couldn't read: " + ex.Message, null); }
                });
            });
            Phase = "";
            Status = Installs.Count == 0 ? "No Call of Duty install found from the Xbox app, Steam or Battle.net. Use Add folder if yours is somewhere unusual."
                   : $"{Installs.Count} install{(Installs.Count == 1 ? "" : "s")} found.";
        }
        finally { ProgressIndeterminate = false; IsBusy = false; }
    }

    void OnMainChosen(InstallVm chosen)
    {
        foreach (var i in Installs) if (i != chosen && i.IsMain) i.IsMain = false;
        _settings.Main = Settings.StoreKey(chosen.Store);
        _settings.MainRoot = chosen.Root;
        _settings.Save();
        if (UnshareTarget == chosen) UnshareTarget = Installs.FirstOrDefault(i => i != chosen);
        OnPropertyChanged(nameof(Main));
        UpdateHint();
        RaiseAll();
    }

    void UpdateHint()
    {
        if (Installs.Count == 0) Hint = "";
        else if (Installs.Count < 2) Hint = "Only one install found: there is nothing to share it with.";
        else if (Main == null) Hint = "Pick your main store: the one you play from most. Its files are kept as they are; the other stores are pointed at them.";
        else if (!CanRun) Hint = "Installs must be on the same drive to share files. Move one with its store's move option, then refresh.";
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

    // ----- runs -----

    async void RunShare(bool dryRun)
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
            if (!(Confirm?.Invoke("Share game files?",
                    $"Identical game data files in {others} will be replaced by links to {main.Name}'s copies.\n\n" +
                    $"{main.Name} is never modified. Every file is compared byte by byte first. Continue?") ?? true)) return;
        }

        await Execute(dryRun ? "preview" : "share", (engine, progress, ct) =>
        {
            var r = engine.Run(ordered, dryRun, progress, ct);
            var sb = new StringBuilder();
            sb.AppendLine($"Already shared:          {r.AlreadyShared,8:N0} files  {ShareEngine.Gb(r.AlreadySharedBytes)}");
            sb.AppendLine($"{(dryRun ? "Would share now:" : "Shared now:"),-24} {r.Linked,8:N0} files  {ShareEngine.Gb(r.LinkedBytes)}");
            sb.AppendLine($"Different, kept apart:   {r.Differ,8:N0} files  {ShareEngine.Gb(r.DifferBytes)}" + (r.Differ > 0 ? "   (a store on another patch: update every store, then run again)" : ""));
            sb.AppendLine($"Errors:                  {r.Errors,8:N0}");
            if (r.Leftovers > 0) sb.AppendLine($"Temp files left behind:  {r.Leftovers,8:N0}   (cleaned up on the next run)");
            if (!dryRun)
                foreach (var (drive, (before, after)) in r.FreeSpace)
                    sb.AppendLine($"Free space on {drive}  {ShareEngine.Gb(before)} -> {ShareEngine.Gb(after)}  (freed {ShareEngine.Gb(after - before)})");
            sb.Append($"Took {r.Elapsed.TotalSeconds:N0} s.");
            return (sb.ToString(), dryRun ? $"Preview done: {r.Linked:N0} files ({ShareEngine.Gb(r.LinkedBytes)}) would be shared, {r.AlreadyShared:N0} already are."
                                          : $"Done: shared {r.Linked:N0} files, freed {ShareEngine.Gb(r.Freed)}.");
        });
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
        await Execute("unshare", (engine, progress, ct) =>
        {
            var r = engine.Unshare(model, progress, ct);
            return ($"{model.Name}: {r.Files:N0} files now separate ({ShareEngine.Gb(r.Bytes)}), {r.Errors} errors.",
                    $"Done: {model.Name} has its own copies again.");
        });
    }

    async Task Execute(string kind, Func<ShareEngine, IProgress<ShareProgress>, CancellationToken, (string Summary, string Status)> work)
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
        var engine = new ShareEngine(Log);
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
            _logFile.Dispose(); _logFile = null;
            Log($"Log saved to {logPath}");
            _cts.Dispose(); _cts = null;
            IsBusy = false;
            _ = Refresh();
        }
    }

    void Log(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        try { _logFile?.WriteLine(stamped); } catch { }
        _ui.Post(_ =>
        {
            // Keep the on-screen log readable: per-file lines go to the file, the window shows the rest.
            if (line.StartsWith("linked ") || line.StartsWith("would link ") || line.StartsWith("unshared ")) return;
            _log.AppendLine(stamped);
            OnPropertyChanged(nameof(LogText));
        }, null);
    }
}
