using System.Text.Json.Nodes;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;

namespace YoutubeMusicNative.Services;

/// <summary>
/// Turns YouTube Music (WEB_REMIX) InnerTube responses into a small, uniform model.
/// The parser works structurally (renderer names), never on display strings,
/// so it is independent of the UI language.
/// </summary>
internal static class YtmParser
{
    // ---------- pages ----------

    public static BrowseResult ParseBrowse(JsonNode root)
    {
        var result = new BrowseResult { Header = ParseHeader(root) };
        Continuation? listCont = null, sectionCont = null;

        foreach (var sectionList in root.Get("contents").FindAll("sectionListRenderer"))
        {
            AddShelves(sectionList.Get("contents"), result.Shelves, ref listCont);
            sectionCont ??= QueryToken(sectionList);
        }

        // Album tracks come without artwork: number them and borrow the album cover.
        if (result.Header?.Thumbnail is { } cover)
        {
            foreach (var shelf in result.Shelves.Where(s => s.Style == ShelfStyle.List))
            {
                if (shelf.Items.Count == 0 || shelf.Items.Any(i => i.Thumbnail is not null)) continue;
                shelf.ShowIndex = true;
                foreach (var item in shelf.Items) item.Thumbnail = cover;
            }
        }

        result.Continuation = listCont ?? sectionCont;
        return result;
    }

    public static ContinuationResult ParseContinuation(JsonNode root)
    {
        var result = new ContinuationResult();
        var cc = root.Get("continuationContents");

        if (cc.Get("sectionListContinuation") is { } sectionList)
        {
            Continuation? ignored = null;
            AddShelves(sectionList.Get("contents"), result.Shelves, ref ignored);
            result.Next = QueryToken(sectionList);
        }
        else if ((cc.Get("musicPlaylistShelfContinuation") ?? cc.Get("musicShelfContinuation")) is { } shelf)
        {
            result.Items.AddRange(ParseItems(shelf.Get("contents")));
            result.Next = QueryToken(shelf) ?? TrailingToken(shelf.Get("contents"));
        }
        else
        {
            foreach (var action in root.Get("onResponseReceivedActions").Items())
            {
                var items = action.Path("appendContinuationItemsAction.continuationItems");
                result.Items.AddRange(ParseItems(items));
                result.Next ??= TrailingToken(items);
            }
        }
        return result;
    }

    public static QueueResult ParseQueue(JsonNode root)
    {
        var result = new QueueResult();
        var panel = root.Path("continuationContents.playlistPanelContinuation") ?? root.FindFirst("playlistPanelRenderer");
        if (panel is null) return result;

        result.PlaylistId = panel.Get("playlistId").Str();
        foreach (var c in panel.Get("contents").Items())
        {
            var v = c.Get("playlistPanelVideoRenderer")
                    ?? c.Path("playlistPanelVideoWrapperRenderer.primaryRenderer.playlistPanelVideoRenderer");
            var videoId = v.Get("videoId").Str();
            var title = v.Get("title").Text();
            if (videoId is null || string.IsNullOrEmpty(title)) continue;

            var item = new MediaItem
            {
                Title = title,
                VideoId = videoId,
                Kind = ItemKind.Song,
                Subtitle = v.Get("shortBylineText").Text(),
                Duration = v.Get("lengthText").Text(),
                Thumbnail = Thumbs.Best(v.Path("thumbnail.thumbnails")),
            };
            ApplyRunLinks(item, v.Get("longBylineText"));
            result.Items.Add(item);
        }

        var next = panel.Path("continuations.0.nextRadioContinuationData.continuation").Str()
                   ?? panel.Path("continuations.0.nextContinuationData.continuation").Str();
        if (next is not null) result.Next = new Continuation(next, true);
        return result;
    }

    public static List<string> ParseSuggestions(JsonNode root)
    {
        var list = new List<string>();
        foreach (var section in root.Get("contents").Items())
        foreach (var s in section.Path("searchSuggestionsSectionRenderer.contents").Items())
        {
            if (s.Path("searchSuggestionRenderer.suggestion").Text() is { Length: > 0 } text) list.Add(text);
        }
        return list;
    }

    public static AccountInfo? ParseAccount(JsonNode root)
    {
        var header = root.FindFirst("activeAccountHeaderRenderer");
        if (header is null) return null;
        return new AccountInfo(header.Get("accountName").Text(), Thumbs.Best(header.Path("accountPhoto.thumbnails")));
    }

    // ---------- shelves ----------

    private static void AddShelves(JsonNode? contents, List<Shelf> into, ref Continuation? listCont)
    {
        foreach (var c in contents.Items())
        {
            if ((c.Get("musicCarouselShelfRenderer") ?? c.Get("musicImmersiveCarouselShelfRenderer")) is { } carousel)
            {
                var header = carousel.Path("header.musicCarouselShelfBasicHeaderRenderer");
                var shelf = new Shelf
                {
                    Title = header.Get("title").Text(),
                    Strapline = header.Get("strapline").Text(),
                    Style = carousel.Path("contents.0.musicResponsiveListItemRenderer") is not null
                        ? ShelfStyle.QuickPicks
                        : ShelfStyle.Carousel,
                };
                shelf.Items.AddRange(ParseItems(carousel.Get("contents")));
                if (shelf.Items.Count > 0) into.Add(shelf);
            }
            else if ((c.Get("musicShelfRenderer") ?? c.Get("musicPlaylistShelfRenderer")) is { } list)
            {
                var shelf = new Shelf { Title = list.Get("title").Text(), Style = ShelfStyle.List };
                shelf.Items.AddRange(ParseItems(list.Get("contents")));
                listCont ??= QueryToken(list) ?? TrailingToken(list.Get("contents"));
                if (shelf.Items.Count > 0) into.Add(shelf);
            }
            else if (c.Get("gridRenderer") is { } grid)
            {
                var shelf = new Shelf { Title = grid.Path("header.gridHeaderRenderer.title").Text(), Style = ShelfStyle.Grid };
                shelf.Items.AddRange(ParseItems(grid.Get("items")));
                if (shelf.Items.Count > 0) into.Add(shelf);
            }
            else if (c.Get("musicCardShelfRenderer") is { } card)
            {
                var shelf = new Shelf
                {
                    Title = card.Path("header.musicCardShelfHeaderBasicRenderer.title").Text() ?? "Top result",
                    Style = ShelfStyle.List,
                };
                if (ParseCard(card) is { } top) shelf.Items.Add(top);
                shelf.Items.AddRange(ParseItems(card.Get("contents")));
                if (shelf.Items.Count > 0) into.Add(shelf);
            }
            else if (c.Get("itemSectionRenderer") is { } section)
            {
                // Search results arrive as one bare row per section: gather them into a single list.
                var rows = ParseItems(section.Get("contents")).ToList();
                if (rows.Count > 0)
                {
                    if (into.LastOrDefault() is not { Style: ShelfStyle.List, Title: null } results)
                        into.Add(results = new Shelf { Style = ShelfStyle.List });
                    results.Items.AddRange(rows);
                }
                AddShelves(section.Get("contents"), into, ref listCont);
            }
        }
    }

    private static IEnumerable<MediaItem> ParseItems(JsonNode? items)
    {
        foreach (var n in items.Items())
            if (ParseAny(n) is { } item) yield return item;
    }

    private static MediaItem? ParseAny(JsonNode n)
    {
        if (n.Get("musicTwoRowItemRenderer") is { } twoRow) return ParseTwoRow(twoRow);
        if (n.Get("musicResponsiveListItemRenderer") is { } row) return ParseResponsive(row);
        if (n.Get("musicNavigationButtonRenderer") is { } nav) return ParseNavigationButton(nav);
        if (n.Get("musicMultiRowListItemRenderer") is { } multi) return ParseMultiRow(multi);
        return null;
    }

    // ---------- items ----------

    private static MediaItem? ParseTwoRow(JsonNode r)
    {
        var title = r.Get("title").Text();
        if (string.IsNullOrEmpty(title)) return null;

        var item = new MediaItem
        {
            Title = title,
            Subtitle = r.Get("subtitle").Text(),
            Thumbnail = Thumbs.Best(r.Path("thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails")),
            IsWide = r.Get("aspectRatio").Str()?.Contains("16_9", StringComparison.Ordinal) == true,
        };

        var nav = r.Get("navigationEndpoint");
        if (nav.Get("browseEndpoint") is { } browse)
        {
            item.BrowseId = browse.Get("browseId").Str();
            item.BrowseParams = browse.Get("params").Str();
            item.Kind = KindFromPageType(PageType(browse));
        }
        else if (nav.Get("watchEndpoint") is { } watch)
        {
            item.VideoId = watch.Get("videoId").Str();
            item.PlaylistId = watch.Get("playlistId").Str();
            item.Kind = KindFromVideoType(VideoType(watch));
        }
        else if (nav.Get("watchPlaylistEndpoint") is { } wp)
        {
            item.PlaylistId = wp.Get("playlistId").Str();
            item.Kind = ItemKind.Playlist;
        }

        var play = r.Path("thumbnailOverlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint");
        item.PlaylistId ??= play.Path("watchPlaylistEndpoint.playlistId").Str() ?? play.Path("watchEndpoint.playlistId").Str();
        item.VideoId ??= item.Kind is ItemKind.Song or ItemKind.Video ? play.Path("watchEndpoint.videoId").Str() : null;

        ApplyRunLinks(item, r.Get("subtitle"));
        return item.BrowseId is null && item.VideoId is null && item.PlaylistId is null ? null : item;
    }

    private static MediaItem? ParseResponsive(JsonNode r)
    {
        var cols = r.Get("flexColumns").Items()
            .Select(c => c.Path("musicResponsiveListItemFlexColumnRenderer.text"))
            .ToList();
        var title = cols.FirstOrDefault().Text();
        if (string.IsNullOrEmpty(title)) return null;

        var parts = cols.Skip(1).Select(c => c.Text()).Where(t => !string.IsNullOrWhiteSpace(t));
        var item = new MediaItem
        {
            Title = title,
            Subtitle = string.Join(" • ", parts),
            Thumbnail = Thumbs.Best(r.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails")),
            Duration = r.Path("fixedColumns.0.musicResponsiveListItemFixedColumnRenderer.text").Text(),
            Index = r.Get("index").Text(),
        };
        foreach (var col in cols.Skip(1)) ApplyRunLinks(item, col);

        var play = r.Path("overlay.musicItemThumbnailOverlayRenderer.content.musicPlayButtonRenderer.playNavigationEndpoint");
        var watch = play.Get("watchEndpoint") ?? cols[0].Path("runs.0.navigationEndpoint.watchEndpoint");
        item.VideoId = r.Path("playlistItemData.videoId").Str() ?? watch.Get("videoId").Str();
        item.PlaylistId = play.Path("watchPlaylistEndpoint.playlistId").Str();

        if (r.Path("navigationEndpoint.browseEndpoint") is { } browse)
        {
            item.BrowseId = browse.Get("browseId").Str();
            item.BrowseParams = browse.Get("params").Str();
            item.Kind = KindFromPageType(PageType(browse));
            if (item.Kind is ItemKind.Album or ItemKind.Playlist or ItemKind.Artist) item.VideoId = null;
        }
        else if (item.VideoId is not null)
        {
            item.Kind = KindFromVideoType(VideoType(watch));
        }

        return item.BrowseId is null && item.VideoId is null && item.PlaylistId is null ? null : item;
    }

    private static MediaItem? ParseNavigationButton(JsonNode b)
    {
        var title = b.Get("buttonText").Text();
        var browse = b.Path("clickCommand.browseEndpoint");
        if (string.IsNullOrEmpty(title) || browse is null) return null;

        var color = b.Path("solid.leftStripeColor").Long() is { } argb ? $"#{(uint)argb:X8}" : "#FFFF0033";
        return new MediaItem
        {
            Title = title,
            Kind = ItemKind.Category,
            BrowseId = browse.Get("browseId").Str(),
            BrowseParams = browse.Get("params").Str(),
            AccentColor = color,
        };
    }

    private static MediaItem? ParseMultiRow(JsonNode r)
    {
        var title = r.Get("title").Text();
        var videoId = r.Path("onTap.watchEndpoint.videoId").Str();
        if (string.IsNullOrEmpty(title) || videoId is null) return null;
        return new MediaItem
        {
            Title = title,
            Subtitle = r.Get("subtitle").Text(),
            Thumbnail = Thumbs.Best(r.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails")),
            VideoId = videoId,
            Kind = ItemKind.Episode,
        };
    }

    private static MediaItem? ParseCard(JsonNode card)
    {
        var title = card.Get("title").Text();
        if (string.IsNullOrEmpty(title)) return null;
        var item = new MediaItem
        {
            Title = title,
            Subtitle = card.Get("subtitle").Text(),
            Thumbnail = Thumbs.Best(card.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails")),
        };
        var nav = card.Path("title.runs.0.navigationEndpoint");
        if (nav.Get("browseEndpoint") is { } browse)
        {
            item.BrowseId = browse.Get("browseId").Str();
            item.Kind = KindFromPageType(PageType(browse));
        }
        else if (nav.Get("watchEndpoint") is { } watch)
        {
            item.VideoId = watch.Get("videoId").Str();
            item.Kind = KindFromVideoType(VideoType(watch));
        }
        ApplyRunLinks(item, card.Get("subtitle"));
        return item.BrowseId is null && item.VideoId is null ? null : item;
    }

    // ---------- header ----------

    private static PageHeader? ParseHeader(JsonNode root)
    {
        var h = root.Get("header");
        var isArtist = h.Get("musicImmersiveHeaderRenderer") is not null || h.Get("musicVisualHeaderRenderer") is not null;
        var r = h.Get("musicImmersiveHeaderRenderer")
                ?? h.Get("musicVisualHeaderRenderer")
                ?? h.Get("musicDetailHeaderRenderer")
                ?? h.Path("musicEditablePlaylistDetailHeaderRenderer.header.musicDetailHeaderRenderer")
                ?? h.Path("musicEditablePlaylistDetailHeaderRenderer.header.musicResponsiveHeaderRenderer")
                ?? root.Get("contents").FindFirst("musicResponsiveHeaderRenderer")
                ?? h.Get("musicHeaderRenderer");
        if (r is null) return null;

        var thumbs = r.Path("thumbnail.musicThumbnailRenderer.thumbnail.thumbnails")
                     ?? r.Path("thumbnail.croppedSquareThumbnailRenderer.thumbnail.thumbnails")
                     ?? r.Path("foregroundThumbnail.musicThumbnailRenderer.thumbnail.thumbnails");

        return new PageHeader
        {
            Title = r.Get("title").Text(),
            Subtitle = r.Get("subtitle").Text() ?? r.Path("subscriptionButton.subscribeButtonRenderer.subscriberCountText").Text(),
            SecondSubtitle = r.Get("secondSubtitle").Text(),
            Strapline = r.Get("straplineTextOne").Text(),
            Description = r.Path("description.musicDescriptionShelfRenderer.description").Text() ?? r.Get("description").Text(),
            Thumbnail = Thumbs.Best(thumbs),
            IsArtist = isArtist,
        };
    }

    // ---------- helpers ----------

    private static void ApplyRunLinks(MediaItem item, JsonNode? text)
    {
        foreach (var run in text.Get("runs").Items())
        {
            var browse = run.Path("navigationEndpoint.browseEndpoint");
            if (browse is null) continue;
            var id = browse.Get("browseId").Str();
            switch (KindFromPageType(PageType(browse)))
            {
                case ItemKind.Artist:
                    item.ArtistName ??= run.Get("text").Str();
                    item.ArtistBrowseId ??= id;
                    break;
                case ItemKind.Album:
                    item.AlbumName ??= run.Get("text").Str();
                    item.AlbumBrowseId ??= id;
                    break;
            }
        }
    }

    private static string? PageType(JsonNode browse) =>
        browse.Str("browseEndpointContextSupportedConfigs.browseEndpointContextMusicConfig.pageType");

    private static string? VideoType(JsonNode? watch) =>
        watch.Str("watchEndpointMusicSupportedConfigs.watchEndpointMusicConfig.musicVideoType");

    private static ItemKind KindFromPageType(string? pageType) => pageType switch
    {
        "MUSIC_PAGE_TYPE_ALBUM" or "MUSIC_PAGE_TYPE_AUDIOBOOK" => ItemKind.Album,
        "MUSIC_PAGE_TYPE_PLAYLIST" or "MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE" => ItemKind.Playlist,
        "MUSIC_PAGE_TYPE_ARTIST" or "MUSIC_PAGE_TYPE_USER_CHANNEL" or "MUSIC_PAGE_TYPE_LIBRARY_ARTIST" => ItemKind.Artist,
        _ => ItemKind.Unknown,
    };

    private static ItemKind KindFromVideoType(string? videoType) => videoType switch
    {
        "MUSIC_VIDEO_TYPE_OMV" or "MUSIC_VIDEO_TYPE_UGC" or "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC" => ItemKind.Video,
        "MUSIC_VIDEO_TYPE_PODCAST_EPISODE" => ItemKind.Episode,
        _ => ItemKind.Song,
    };

    private static Continuation? QueryToken(JsonNode? renderer) =>
        renderer.Path("continuations.0.nextContinuationData.continuation").Str() is { } t ? new Continuation(t, false) : null;

    private static Continuation? TrailingToken(JsonNode? contents)
    {
        var token = contents.Items()
            .Select(c => c.Path("continuationItemRenderer.continuationEndpoint.continuationCommand.token").Str())
            .LastOrDefault(t => t is not null);
        return token is null ? null : new Continuation(token, true);
    }
}
