using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CoDDedupe.ViewModels;
using Microsoft.Win32;
using Spock.AppKit;

namespace CoDDedupe;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm = new();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMinutes(10) };
    readonly TrayIcon _tray;
    bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        WindowPlacement.Restore(this, _vm.AppSettings.WindowWidth, _vm.AppSettings.WindowHeight, _vm.AppSettings.WindowMaximized);

        _vm.Confirm = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        _vm.Alert = (title, text) => MessageBox.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        _vm.PickFolder = () =>
        {
            var dlg = new OpenFolderDialog { Title = "Choose a Call of Duty install folder (the one with cod.exe, or the store's folder above it)" };
            return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
        };
        _vm.PickPreset = () =>
        {
            var dlg = new OpenFileDialog { Title = "Choose the CoD Tweaker preset to apply after sharing", Filter = "CoD Tweaker presets (*.json)|*.json|All files|*.*" };
            return dlg.ShowDialog(this) == true ? dlg.FileName : null;
        };

        _tray = new TrayIcon("CoD Dedupe") { ContextMenu = BuildTrayMenu(), Visible = _vm.WatchForUpdates };
        _tray.Activated += ShowFromTray;
        _vm.WatchNotice += message => _tray.Notify("CoD Dedupe", message);
        _vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.WatchForUpdates)) _tray.Visible = _vm.WatchForUpdates || !IsVisible; };

        DataContext = _vm;
        UpdateThemeButton();
        SourceInitialized += (_, _) => Theme.ApplyTitleBar(this);
        Loaded += async (_, _) => await _vm.Refresh();
        _timer.Tick += (_, _) => _vm.RefreshLaunchers();
        _timer.Start();
        _watch.Tick += async (_, _) => await _vm.WatchTick();
        _watch.Start();
        _vm.CheckForUpdates(manual: false);
        Closing += OnClosing;
    }

    // ----- tray -----

    ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu();
        var open = new MenuItem { Header = "Open CoD Dedupe" };
        open.Click += (_, _) => ShowFromTray();
        var watch = new MenuItem { Header = "Watch for updates", IsCheckable = true };
        watch.SetBinding(MenuItem.IsCheckedProperty, new Binding(nameof(MainViewModel.WatchForUpdates)) { Source = _vm, Mode = BindingMode.TwoWay });
        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(open);
        menu.Items.Add(watch);
        menu.Items.Add(new Separator());
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Closing the window keeps the app in the tray only while it has something to watch for.</summary>
    public bool CanHideToTray => _vm.WatchForUpdates;

    public void HideToTray(bool firstTime)
    {
        Hide();
        _tray.Visible = true;
        if (firstTime && !_vm.AppSettings.TrayHintShown)
        {
            _vm.AppSettings.TrayHintShown = true;
            _vm.AppSettings.Save();
            _tray.Notify("CoD Dedupe is still running", "It checks now and then whether an update left files that could be shared again, and tells you here. Right-click the icon to open it or to exit.");
        }
    }

    void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _tray.Visible = _vm.WatchForUpdates;
    }

    public void ExitApp()
    {
        _exiting = true;
        if (!IsVisible) Show();
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_exiting && CanHideToTray && !_vm.IsBusy)
        {
            e.Cancel = true;
            HideToTray(firstTime: true);
            return;
        }
        if (_vm.IsBusy)
        {
            e.Cancel = MessageBox.Show(this, "A run is still going. Close anyway? It stops cleanly; nothing is left half-done.", "Still working",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes;
            if (e.Cancel) { _exiting = false; return; }
            _vm.CancelCommand.Execute(null);
        }
        var (w, h, max) = WindowPlacement.Capture(this);
        _vm.AppSettings.WindowWidth = w; _vm.AppSettings.WindowHeight = h; _vm.AppSettings.WindowMaximized = max;
        _vm.AppSettings.Save();
        _timer.Stop();
        _watch.Stop();
        _tray.Dispose();
    }

    // ----- small handlers -----

    void LogBox_TextChanged(object sender, TextChangedEventArgs e) => LogBox.ScrollToEnd();

    void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        Theme.Apply(!Theme.IsDark);
        _vm.AppSettings.Theme = Theme.IsDark ? "dark" : "light";
        _vm.AppSettings.Save();
        UpdateThemeButton();
    }

    void UpdateThemeButton() => ThemeButton.Content = Theme.ToggleLabel;

    void Exit_Click(object sender, RoutedEventArgs e) => ExitApp();
}
