using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace SyncMaster.App.Windows;

// The dashboard window. Frameless, rounded, transparent — it hosts the web UI in
// Task 2. Closing the window does NOT quit the app: it cancels the close and hides
// the window so the process stays resident in the tray. Quit goes through the tray
// menu (desktop.Shutdown()).
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing hides, never quits: keep the tray-resident process alive.
        e.Cancel = true;
        Hide();
        base.OnClosing(e);
    }
}
