using CoDDedupe.Core;

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

    public InstallVm(Install model) { Model = model; }

    public void Rename(string name) { Model = Model with { Name = name }; OnPropertyChanged(nameof(Name)); }
}
