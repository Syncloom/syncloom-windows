using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Syncloom.Services;

namespace Syncloom.Pages;

public sealed partial class SettingsPage : Page
{
    private MainWindow _window = null!;

    public SettingsPage() => InitializeComponent();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _window = (MainWindow)e.Parameter;
        User.Text = AppState.Saved?.Username;
        Server.Text = AppState.Saved?.Server;
        Folder.Text = $"Sync folder: {AppState.Saved?.SyncFolder}";
        try
        {
            var me = (await AppState.CallAsync("me"))!;
            var used = me["used_bytes"]!.GetValue<long>() / 1024.0 / 1024.0;
            Storage.Text = me["quota_bytes"] is { } q ? $"Storage: {used:0.#} MB of {q.GetValue<long>() / 1024.0 / 1024.0:0.#} MB" : $"Storage: {used:0.#} MB used";
            Methods.ItemsSource = (await AppState.CallAsync("mfa_methods"))!.AsArray()
                .Select(m => $"{m!["name"]!.GetValue<string>()}   ·   {(m["kind"]!.GetValue<string>() == "webauthn" ? "Security key" : "Authenticator app")}").ToList();
        }
        catch (Exception ex) { Storage.Text = ex.Message; }
    }

    private void SignOut_Click(object sender, RoutedEventArgs e)
    {
        AppState.SignOut();
        _window.ShowSignIn();
    }
}
