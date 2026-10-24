using CoDDedupe.Core;
using Spock.AppKit.Mvvm;

namespace CoDDedupe.ViewModels;

public sealed class InstallVm : ObservableObject
{
    public Install Model { get; private set; }
    public string Name => Model.Name;
    public string Root => Model.Root;
    public string Drive => Model.Drive;
    public Store Store => Model.Store;

    bool _isMain;
    public bool IsMain { get => _isMain; set { if (SetProperty(ref _isMain, value) && value) MainChosen?.Invoke(this); } }
    public event Action<InstallVm>? MainChosen;

    bool _canShare = true;
    public bool CanShare { get => _canShare; set => SetProperty(ref _canShare, value); }

    string _detail = "";
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }

    string _note = "";
    public string Note { get => _note; set => SetProperty(ref _note, value); }

    string _warning = "";
    /// <summary>Shared files an update patched in place; see <see cref="InstallStats.Warning"/>.</summary>
    public string Warning { get => _warning; set => SetProperty(ref _warning, value); }

    InstallStats? _stats;
    public InstallStats? Stats { get => _stats; set { if (SetProperty(ref _stats, value)) { Detail = value?.Detail ?? ""; Warning = value?.Warning ?? ""; } } }

    public InstallVm(Install model) { Model = model; }

    public void Rename(string name) { Model = Model with { Name = name }; OnPropertyChanged(nameof(Name)); }
}
