using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using QRCoder;
using Syncloom.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Syncloom.Pages;

public sealed partial class SignInPage : Page
{
    private MainWindow _window = null!;
    private bool _registering;
    private string _server = "";
    private string _token = "";
    private string _totpId = "";
    private string _pendingToken = "";
    private System.Text.Json.Nodes.JsonNode? _pendingLogin;
    private string _recoveryKey = "";

    public SignInPage()
    {
        InitializeComponent();
        Show("credentials");
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _window = (MainWindow)e.Parameter;
        base.OnNavigatedTo(e);
    }

    private void Show(string step)
    {
        CredentialsPanel.Visibility = step == "credentials" ? Visibility.Visible : Visibility.Collapsed;
        MfaPanel.Visibility = step == "mfa" ? Visibility.Visible : Visibility.Collapsed;
        KeyPanel.Visibility = step == "key" ? Visibility.Visible : Visibility.Collapsed;
        TotpPanel.Visibility = step == "totp" ? Visibility.Visible : Visibility.Collapsed;
        CodesPanel.Visibility = step == "codes" ? Visibility.Visible : Visibility.Collapsed;
        (Heading.Text, Sub.Text) = step switch
        {
            "mfa" => ("Verify it’s you", "Use any one of your second factors."),
            "key" => ("Save your recovery key", "If you forget your password, this key is the only way to get your files back."),
            "totp" => ("Set up your authenticator", "Scan the code, then enter the 6 digits."),
            "codes" => ("You are protected", "Keep these one-time recovery codes in case you lose your devices."),
            _ => _registering ? ("Create your account", "Your files are encrypted on this device before they leave it.") : ("Welcome back", "Sign in to your encrypted storage."),
        };
        Error.Text = "";
    }

    private async Task Run(Func<Task> work)
    {
        Busy.IsActive = true;
        Error.Text = "";
        try { await work(); }
        catch (Exception ex) { Error.Text = ex.Message; }
        finally { Busy.IsActive = false; }
    }

    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        _registering = !_registering;
        Invite.Visibility = _registering ? Visibility.Visible : Visibility.Collapsed;
        Go.Content = _registering ? "Create account" : "Sign in";
        Toggle.Content = _registering ? "I already have an account" : "Create an account with an invite";
        Show("credentials");
    }

    private void Password_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Go_Click(sender, e);
    }

    private async void Go_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var s = Server.Text.Trim().TrimEnd('/');
        _server = s.Contains("://") ? s : "https://" + s;
        var args = new { server = _server, username = Username.Text.Trim(), password = Password.Password, device = AppState.Device };
        if (_registering)
        {
            var r = await Native.InvokeAsync("register", new { args.server, args.username, args.password, args.device, invite = string.IsNullOrWhiteSpace(Invite.Text) ? null : Invite.Text.Trim() });
            _recoveryKey = r!["recovery_key"]!.GetValue<string>();
            _pendingLogin = r["login"];
            KeyText.Text = string.Join("\n", Chunk(_recoveryKey, 17));
            Show("key");
        }
        else await Handle((await Native.InvokeAsync("login", args))!);
    });

    private static IEnumerable<string> Chunk(string s, int n) { for (var i = 0; i < s.Length; i += n) yield return s.Substring(i, Math.Min(n, s.Length - i)); }

    private async Task Handle(System.Text.Json.Nodes.JsonNode login)
    {
        _token = login["token"]!.GetValue<string>();
        switch (login["status"]!.GetValue<string>())
        {
            case "ok": await Finish(_token); break;
            case "mfa_required": Code.Text = ""; Show("mfa"); break;
            default:
                var setup = (await Native.InvokeAsync("totp_setup", new { server = _server, token = _token, name = $"{AppState.Device} authenticator" }))!;
                _totpId = setup["id"]!.GetValue<string>();
                Secret.Text = string.Join(" ", Chunk(setup["secret"]!.GetValue<string>(), 4));
                using (var gen = new QRCodeGenerator())
                using (var data = gen.CreateQrCode(setup["otpauth_url"]!.GetValue<string>(), QRCodeGenerator.ECCLevel.M))
                {
                    var png = new PngByteQRCode(data).GetGraphic(8);
                    var img = new BitmapImage();
                    using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    await ms.WriteAsync(png.AsBuffer());
                    ms.Seek(0);
                    await img.SetSourceAsync(ms);
                    Qr.Source = img;
                }
                Show("totp");
                break;
        }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        var pkg = new DataPackage();
        pkg.SetText(_recoveryKey);
        Clipboard.SetContent(pkg);
    }

    private async void KeyContinue_Click(object sender, RoutedEventArgs e) => await Run(() => Handle(_pendingLogin!));

    private async void Code_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!UseRecovery.IsOn && Code.Text.Count(char.IsDigit) == 6 && Code.Text.Length == 6) await Verify();
    }

    private async void Verify_Click(object sender, RoutedEventArgs e) => await Verify();

    private Task Verify() => Run(async () =>
    {
        var r = await Native.InvokeAsync("mfa_verify", new { server = _server, token = _token, method = UseRecovery.IsOn ? "recovery" : "totp", code = Code.Text.Trim(), device = AppState.Device });
        await Finish(r!["token"]!.GetValue<string>());
    });

    private async void TotpConfirm_Click(object sender, RoutedEventArgs e) => await Run(async () =>
    {
        var r = (await Native.InvokeAsync("totp_confirm", new { server = _server, token = _token, id = _totpId, code = TotpCode.Text.Trim(), device = AppState.Device }))!;
        _pendingToken = r["token"]!.GetValue<string>();
        CodesText.Text = string.Join("\n", r["recovery_codes"]!.AsArray().Select(c => c!.GetValue<string>()));
        Show("codes");
    });

    private async void OpenFiles_Click(object sender, RoutedEventArgs e) => await Run(() => Finish(_pendingToken));

    private async Task Finish(string token)
    {
        await AppState.OpenAsync(_server, Username.Text.Trim(), token, Password.Password);
        Password.Password = "";
        _window.ShowMain();
    }
}
