using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CoDDedupe.ViewModels;
using Microsoft.Win32;

namespace CoDDedupe;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };

    public MainWindow()
    {
        InitializeComponent();
        _vm.Confirm = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _vm.Alert = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        _vm.PickFolder = () =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a Call of Duty install folder (the one with cod.exe, or the store's folder above it)" };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };
        DataContext = _vm;
        Loaded += async (_, _) => await _vm.Refresh();
        _timer.Tick += (_, _) => _vm.RefreshLaunchers();
        _timer.Start();
        Closing += (_, e) =>
        {
            if (!_vm.IsBusy) return;
            e.Cancel = MessageBox.Show(this, "A run is still going. Close anyway? It stops cleanly; nothing is left half-done.", "Still working",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes;
            if (!e.Cancel) _vm.CancelCommand.Execute(null);
        };
    }

    void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();
}
