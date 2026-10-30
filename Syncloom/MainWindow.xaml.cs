using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Syncloom.Pages;
using Syncloom.Services;

namespace Syncloom;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1120, 760));
        _ = StartAsync();
    }

    private async Task StartAsync()
    {
        if (await AppState.RestoreAsync()) ShowMain();
        else AuthFrame.Navigate(typeof(SignInPage), this);
    }

    /// <summary>Called by the sign-in page once the vault is unlocked.</summary>
    public void ShowMain()
    {
        AuthFrame.Visibility = Visibility.Collapsed;
        Nav.Visibility = Visibility.Visible;
        ContentFrame.Navigate(typeof(FilesPage), new FilesPage.Args(null, "Files", false));
        _ = CloudFilesRegistration.RegisterAsync(AppState.Saved!.SyncFolder, AppState.Saved.Username).ContinueWith(_ => { });
        AppState.StartFolderSync(_ => { });
    }

    public void ShowSignIn()
    {
        Nav.Visibility = Visibility.Collapsed;
        AuthFrame.Visibility = Visibility.Visible;
        AuthFrame.Navigate(typeof(SignInPage), this);
    }

    private void Nav_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        switch ((args.InvokedItemContainer as NavigationViewItem)?.Tag as string)
        {
            case "files": ContentFrame.Navigate(typeof(FilesPage), new FilesPage.Args(null, "Files", false)); break;
            case "shared": ContentFrame.Navigate(typeof(FilesPage), new FilesPage.Args(null, "Shared with me", true)); break;
            case "trash": ContentFrame.Navigate(typeof(FilesPage), new FilesPage.Args(null, "Trash", false, Trash: true)); break;
            case "settings": ContentFrame.Navigate(typeof(SettingsPage), this); break;
        }
    }
}
