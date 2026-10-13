using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using YoutubeMusicNative.Infrastructure;

namespace YoutubeMusicNative.Services;

/// <summary>
/// Sign-in happens in an embedded browser window (Views/LoginWindow). Once the user is signed in
/// there, the music.youtube.com cookies are captured and every API request is authenticated the
/// same way the YouTube Music website does it: Cookie header + SAPISIDHASH.
/// </summary>
public sealed class AuthService(Settings settings) : ObservableObject
{
    public const string Origin = "https://music.youtube.com";

    /// <summary>Profile folder of the sign-in browser window.</summary>
    public static string BrowserDataFolder { get; } = System.IO.Path.Combine(Settings.Folder, "WebView2");

    public bool IsSignedIn => settings.Secret.Cookie is not null;

    /// <summary>The signed-in session's cookies (Cookie header form), or null when signed out.</summary>
    public string? SessionCookie => settings.Secret.Cookie;

    public Task ApplyAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (settings.Secret.Cookie is { } cookie)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if ((ReadCookie(cookie, "__Secure-3PAPISID") ?? ReadCookie(cookie, "SAPISID")) is { } sapisid)
                request.Headers.TryAddWithoutValidation("Authorization", SapisidHash(sapisid));
        }
        return Task.CompletedTask;
    }

    /// <summary>Returns false if the cookies don't belong to a signed-in session yet.</summary>
    public bool TrySignIn(string cookieHeader)
    {
        if (ReadCookie(cookieHeader, "SAPISID") is null && ReadCookie(cookieHeader, "__Secure-3PAPISID") is null) return false;
        settings.Secret.Cookie = cookieHeader;
        settings.Save();
        OnPropertyChanged(nameof(IsSignedIn));
        return true;
    }

    public void SignOut()
    {
        settings.Secret.Cookie = null;
        settings.Save();
        // Forget the browser session too, so the next sign-in starts fresh.
        try { Directory.Delete(BrowserDataFolder, recursive: true); }
        catch { /* browser process may still hold files; harmless */ }
        OnPropertyChanged(nameof(IsSignedIn));
    }

    private static string? ReadCookie(string cookie, string name)
    {
        foreach (var part in cookie.Split(';'))
        {
            var kv = part.Trim();
            if (kv.StartsWith(name + "=", StringComparison.Ordinal)) return kv[(name.Length + 1)..];
        }
        return null;
    }

    private static string SapisidHash(string sapisid)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes($"{ts} {sapisid} {Origin}")));
        return $"SAPISIDHASH {ts}_{hash}";
    }
}
