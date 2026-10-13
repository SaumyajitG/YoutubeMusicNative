using System.Windows;
using Microsoft.Web.WebView2.Core;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Services;

namespace YoutubeMusicNative.Views;

/// <summary>
/// Embedded browser (WebView2) showing Google's sign-in page. When the browser lands on
/// music.youtube.com with a signed-in session, its cookies are handed to <see cref="AuthService"/>.
/// </summary>
public partial class LoginWindow : Window
{
    // Same entry point the YouTube Music website's "Sign in" button uses.
    private const string SignInUrl =
        "https://accounts.google.com/ServiceLogin?ltmpl=music&service=youtube&passive=true" +
        "&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fmusic.youtube.com%252F";

    private readonly AuthService _auth;

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        WindowTheming.Attach(this);
        _auth = auth;
        Loaded += async (_, _) => await StartAsync();
    }

    private async Task StartAsync()
    {
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, AuthService.BrowserDataFolder);
            await Browser.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowError("Microsoft Edge WebView2 Runtime is required for sign-in. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and try again.");
            return;
        }

        Browser.CoreWebView2.NavigationCompleted += async (_, _) => await TryCaptureAsync();
        Browser.CoreWebView2.Navigate(SignInUrl);
    }

    private async Task TryCaptureAsync()
    {
        var uri = Browser.Source;
        if (uri is null || uri.Host != "music.youtube.com") return;

        var cookies = await Browser.CoreWebView2.CookieManager.GetCookiesAsync(AuthService.Origin);
        var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
        if (_auth.TrySignIn(header)) DialogResult = true;
        else ShowError("Not signed in yet. Use the Sign in button on the page above.");
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
