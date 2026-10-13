using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;

namespace YoutubeMusicNative.Services;

public sealed class InnerTubeException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>Talks to music.youtube.com/youtubei/v1 as the YouTube Music web client (WEB_REMIX).</summary>
public sealed class InnerTubeClient(HttpClient http, AuthService auth)
{
    public const string WebUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36";

    private const string ApiBase = "https://music.youtube.com/youtubei/v1/";
    private const string ClientName = "WEB_REMIX";
    private const string ClientVersion = "1.20251006.01.00";

    private readonly string _hl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is { Length: 2 } l ? l : "en";
    private readonly string _gl = SafeRegion();

    public string? VisitorData { get; private set; }

    // ---------- public API ----------

    public async Task<BrowseResult> GetHomeAsync(CancellationToken ct = default) =>
        YtmParser.ParseBrowse(await PostAsync("browse", new JsonObject { ["browseId"] = "FEmusic_home" }, ct: ct));

    public async Task<BrowseResult> BrowseAsync(string browseId, string? parameters = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["browseId"] = browseId };
        if (parameters is not null) body["params"] = parameters;
        return YtmParser.ParseBrowse(await PostAsync("browse", body, ct: ct));
    }

    public async Task<BrowseResult> SearchAsync(string query, CancellationToken ct = default) =>
        YtmParser.ParseBrowse(await PostAsync("search", new JsonObject { ["query"] = query }, ct: ct));

    public async Task<ContinuationResult> ContinueAsync(Continuation c, CancellationToken ct = default)
    {
        var root = c.BodyStyle
            ? await PostAsync("browse", new JsonObject { ["continuation"] = c.Token }, ct: ct)
            : await PostAsync("browse", new JsonObject(), $"&ctoken={Uri.EscapeDataString(c.Token)}&continuation={Uri.EscapeDataString(c.Token)}&type=next", ct);
        return YtmParser.ParseContinuation(root);
    }

    public async Task<QueueResult> GetQueueAsync(string? videoId, string? playlistId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
            ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
        };
        if (videoId is not null)
        {
            body["videoId"] = videoId;
            body["watchEndpointMusicSupportedConfigs"] = new JsonObject
            {
                ["watchEndpointMusicConfig"] = new JsonObject
                {
                    ["hasPersistentPlaylistPanel"] = true,
                    ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV",
                },
            };
        }
        if (playlistId is not null) body["playlistId"] = playlistId;

        var result = YtmParser.ParseQueue(await PostAsync("next", body, ct: ct));
        result.PlaylistId ??= playlistId;
        return result;
    }

    public async Task<QueueResult> ContinueQueueAsync(Continuation c, string? playlistId, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["continuation"] = c.Token,
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
        };
        if (playlistId is not null) body["playlistId"] = playlistId;
        var result = YtmParser.ParseQueue(await PostAsync("next", body, ct: ct));
        result.PlaylistId ??= playlistId;
        return result;
    }

    public async Task<List<string>> GetSuggestionsAsync(string input, CancellationToken ct = default) =>
        YtmParser.ParseSuggestions(await PostAsync("music/get_search_suggestions", new JsonObject { ["input"] = input }, ct: ct));

    public async Task<AccountInfo?> GetAccountAsync(CancellationToken ct = default) =>
        YtmParser.ParseAccount(await PostAsync("account/account_menu", new JsonObject(), ct: ct));

    /// <summary>Fetches a visitor id for anonymous sessions (needed by the player endpoint).</summary>
    public async Task<string?> EnsureVisitorDataAsync(CancellationToken ct = default)
    {
        if (VisitorData is null)
        {
            try { await PostAsync("music/get_search_suggestions", new JsonObject { ["input"] = "" }, ct: ct); }
            catch { /* best effort */ }
        }
        return VisitorData;
    }

    // ---------- transport ----------

    private async Task<JsonNode> PostAsync(string endpoint, JsonObject body, string? query = null, CancellationToken ct = default)
    {
        var client = new JsonObject
        {
            ["clientName"] = ClientName,
            ["clientVersion"] = ClientVersion,
            ["hl"] = _hl,
            ["gl"] = _gl,
        };
        if (VisitorData is not null) client["visitorData"] = VisitorData;
        body["context"] = new JsonObject { ["client"] = client, ["user"] = new JsonObject() };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}{endpoint}?prettyPrint=false{query}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var h = request.Headers;
        h.TryAddWithoutValidation("User-Agent", WebUserAgent);
        h.TryAddWithoutValidation("Origin", AuthService.Origin);
        h.TryAddWithoutValidation("X-Origin", AuthService.Origin);
        h.TryAddWithoutValidation("Referer", AuthService.Origin + "/");
        h.TryAddWithoutValidation("X-Goog-AuthUser", "0");
        h.TryAddWithoutValidation("X-YouTube-Client-Name", "67");
        h.TryAddWithoutValidation("X-YouTube-Client-Version", ClientVersion);
        if (VisitorData is not null) h.TryAddWithoutValidation("X-Goog-Visitor-Id", VisitorData);
        await auth.ApplyAsync(request, ct).ConfigureAwait(false);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? message = null;
            try { message = JsonNode.Parse(text).Path("error.message").Str(); } catch { /* not JSON */ }
            throw new InnerTubeException((int)response.StatusCode,
                message ?? $"YouTube Music returned {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false)
                   ?? throw new InnerTubeException(0, "Empty response from YouTube Music");
        VisitorData ??= root.Path("responseContext.visitorData").Str();
        return root;
    }

    private static string SafeRegion()
    {
        try { return RegionInfo.CurrentRegion.TwoLetterISORegionName is { Length: 2 } r ? r : "US"; }
        catch { return "US"; }
    }
}
