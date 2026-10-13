using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using YoutubeMusicNative.Infrastructure;

namespace YoutubeMusicNative.Services;

public sealed record StreamInfo(string Url, string MimeType, long? ContentLength, string UserAgent, DateTime ExpiresAtUtc);

/// <summary>
/// Resolves a videoId to a direct audio URL.
/// 0. Signed in and "use my account for playback" on: yt-dlp with the user's session.
/// 1. InnerTube /player with native-app clients (direct URLs, no signature deciphering).
/// 2. Fallback: anonymous yt-dlp (downloaded to the data folder on first use).
/// Results are cached so the next track is resolved while the current one plays.
/// </summary>
public sealed class StreamResolver(HttpClient http, InnerTubeClient api, AuthService auth, Settings settings)
{
    private sealed record PlayerClient(string Name, string Version, int Id, string UserAgent, Action<JsonObject> Extra);

    private static readonly PlayerClient[] Clients =
    [
        new("ANDROID_VR", "1.62.27", 28,
            "com.google.android.apps.youtube.vr.oculus/1.62.27 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip",
            c =>
            {
                c["deviceMake"] = "Oculus";
                c["deviceModel"] = "Quest 3";
                c["androidSdkVersion"] = 32;
                c["osName"] = "Android";
                c["osVersion"] = "12L";
            }),
        new("IOS", "20.10.4", 5,
            "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)",
            c =>
            {
                c["deviceMake"] = "Apple";
                c["deviceModel"] = "iPhone16,2";
                c["osName"] = "iPhone";
                c["osVersion"] = "18.3.2.22D82";
            }),
    ];

    private readonly ConcurrentDictionary<string, Task<StreamInfo>> _cache = new();
    private Task<string>? _ytDlp;

    public Task<StreamInfo> ResolveAsync(string videoId)
    {
        if (_cache.TryGetValue(videoId, out var cached))
        {
            if (!cached.IsCompleted || (cached.IsCompletedSuccessfully && cached.Result.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(5)))
                return cached;
            _cache.TryRemove(videoId, out _);
        }

        var task = ResolveUncachedAsync(videoId);
        _cache[videoId] = task;
        task.ContinueWith(t => _cache.TryRemove(videoId, out _), TaskContinuationOptions.NotOnRanToCompletion);
        return task;
    }

    public void Invalidate(string videoId) => _cache.TryRemove(videoId, out _);

    /// <summary>Forget every resolved link (e.g. after signing in/out or changing account playback).</summary>
    public void Clear() => _cache.Clear();

    private async Task<StreamInfo> ResolveUncachedAsync(string videoId)
    {
        var errors = new List<string>();

        if (settings.UseAccountForPlayback && auth.SessionCookie is { } cookie)
        {
            try
            {
                if (await TryYtDlpAsync(videoId, cookie).ConfigureAwait(false) is { } viaAccount) return viaAccount;
                errors.Add("account: no playable audio");
            }
            catch (Exception ex)
            {
                errors.Add($"account: {ex.Message}");
            }
        }

        var visitor = await api.EnsureVisitorDataAsync().ConfigureAwait(false);

        foreach (var client in Clients)
        {
            try
            {
                if (await TryPlayerAsync(client, videoId, visitor).ConfigureAwait(false) is { } info)
                {
                    if (await IsServedAsync(info).ConfigureAwait(false)) return info;
                    errors.Add($"{client.Name}: stream blocked");
                    continue;
                }
                errors.Add($"{client.Name}: no playable audio");
            }
            catch (Exception ex)
            {
                errors.Add($"{client.Name}: {ex.Message}");
            }
        }

        if (await TryYtDlpAsync(videoId, cookie: null).ConfigureAwait(false) is { } fallback) return fallback;
        throw new InvalidOperationException("Couldn't get an audio stream for this track. " + string.Join("; ", errors));
    }

    private async Task<StreamInfo?> TryPlayerAsync(PlayerClient client, string videoId, string? visitor)
    {
        var context = new JsonObject
        {
            ["clientName"] = client.Name,
            ["clientVersion"] = client.Version,
            ["hl"] = "en",
            ["gl"] = "US",
            ["userAgent"] = client.UserAgent,
        };
        client.Extra(context);
        if (visitor is not null) context["visitorData"] = visitor;

        var body = new JsonObject
        {
            ["context"] = new JsonObject { ["client"] = context },
            ["videoId"] = videoId,
            ["contentCheckOk"] = true,
            ["racyCheckOk"] = true,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/player?prettyPrint=false")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent);
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", client.Id.ToString());
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", client.Version);
        if (visitor is not null) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);

        using var response = await http.SendAsync(request).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var root = JsonNode.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        var status = root.Str("playabilityStatus.status");
        if (status != "OK") throw new InvalidOperationException(root.Str("playabilityStatus.reason") ?? status ?? "unplayable");

        var expiresIn = root.Path("streamingData.expiresInSeconds").Long() ?? 21540;
        var best = root.Path("streamingData.adaptiveFormats").Items()
            .Where(f => f.Get("url").Str() is not null && f.Get("mimeType").Str()?.StartsWith("audio/", StringComparison.Ordinal) == true)
            // Prefer AAC/MP4 (decoded natively by Media Foundation), then highest bitrate.
            .OrderByDescending(f => f.Get("mimeType").Str()!.StartsWith("audio/mp4", StringComparison.Ordinal))
            .ThenByDescending(f => f.Get("bitrate").Long() ?? 0)
            .FirstOrDefault();
        if (best is null) return null;

        var mime = best.Get("mimeType").Str()!;
        var semicolon = mime.IndexOf(';');
        return new StreamInfo(
            best.Get("url").Str()!,
            semicolon > 0 ? mime[..semicolon] : mime,
            best.Get("contentLength").Long(),
            client.UserAgent,
            DateTime.UtcNow.AddSeconds(expiresIn));
    }

    /// <summary>YouTube may hand out URLs that only serve the first ~1 MB; probe the middle of the file.</summary>
    private async Task<bool> IsServedAsync(StreamInfo info)
    {
        var mid = (info.ContentLength ?? 2_000_000) / 2;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{info.Url}&range={mid}-{mid + 1023}");
        request.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<StreamInfo?> TryYtDlpAsync(string videoId, string? cookie)
    {
        var exe = await EnsureYtDlpAsync().ConfigureAwait(false);
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-f", "bestaudio[ext=m4a]/bestaudio", "--no-playlist", "--no-warnings",
                     "--print", "%(url)s", "--print", "%(filesize)s", "--print", "%(http_headers.User-Agent)s", "--print", "%(ext)s" })
            psi.ArgumentList.Add(arg);

        // The session goes to yt-dlp as a Netscape cookie file that only lives while yt-dlp runs.
        string? cookieFile = null;
        if (cookie is not null)
        {
            cookieFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ytmn-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(cookieFile, ToNetscapeCookies(cookie)).ConfigureAwait(false);
            psi.ArgumentList.Add("--cookies");
            psi.ArgumentList.Add(cookieFile);
        }
        psi.ArgumentList.Add($"https://music.youtube.com/watch?v={videoId}");

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var stderr = proc.StandardError.ReadToEndAsync();
            var output = await proc.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await proc.WaitForExitAsync().ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            var lines = output.Split('\n', StringSplitOptions.TrimEntries);
            if (proc.ExitCode != 0 || lines.Length < 4 || !lines[0].StartsWith("http", StringComparison.Ordinal)) return null;

            return new StreamInfo(
                lines[0],
                lines[3] == "webm" ? "audio/webm" : "audio/mp4",
                long.TryParse(lines[1], out var size) ? size : null,
                lines[2],
                DateTime.UtcNow.AddHours(5));
        }
        finally
        {
            if (cookieFile is not null) File.Delete(cookieFile);
        }
    }

    private static string ToNetscapeCookies(string cookieHeader)
    {
        var sb = new StringBuilder("# Netscape HTTP Cookie File\n");
        foreach (var part in cookieHeader.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            sb.Append(".youtube.com\tTRUE\t/\tTRUE\t2147483647\t")
              .Append(part[..eq].Trim()).Append('\t').Append(part[(eq + 1)..].Trim()).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>yt-dlp lives in the app's data folder and is downloaded the first time it's needed.</summary>
    private Task<string> EnsureYtDlpAsync()
    {
        if (_ytDlp is null || _ytDlp.IsFaulted) _ytDlp = GetYtDlpAsync();
        return _ytDlp;
    }

    private async Task<string> GetYtDlpAsync()
    {
        var exe = System.IO.Path.Combine(Settings.Folder, "yt-dlp.exe");
        if (File.Exists(exe)) return exe;

        Directory.CreateDirectory(Settings.Folder);
        var bytes = await http.GetByteArrayAsync("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe").ConfigureAwait(false);
        await File.WriteAllBytesAsync(exe + ".tmp", bytes).ConfigureAwait(false);
        File.Move(exe + ".tmp", exe, overwrite: true);
        return exe;
    }
}