using Microsoft.UI.Xaml;

namespace Syncloom;

public partial class App : Application
{
    /// The main window; pickers need its handle (WinUI 3 has no Window.Current).
    public static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
