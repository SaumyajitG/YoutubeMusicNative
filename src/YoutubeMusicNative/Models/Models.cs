using YoutubeMusicNative.Infrastructure;

namespace YoutubeMusicNative.Models;

public enum ItemKind { Unknown, Song, Video, Album, Playlist, Artist, Category, Episode }

/// <summary>Anything that can be shown as a card or a row: song, album, playlist, artist, mood chip...</summary>
public sealed class MediaItem : ObservableObject
{
    public required string Title { get; init; }
    public string? Subtitle { get; set; }
    public string? Thumbnail { get; set; }
    public ItemKind Kind { get; set; }

    public string? VideoId { get; set; }
    public string? PlaylistId { get; set; }
    public string? BrowseId { get; set; }
    public string? BrowseParams { get; set; }

    public string? Duration { get; set; }
    public string? Index { get; set; }
    public string? ArtistName { get; set; }
    public string? ArtistBrowseId { get; set; }
    public string? AlbumName { get; set; }
    public string? AlbumBrowseId { get; set; }

    public bool IsWide { get; set; }
    public string? AccentColor { get; set; }

    public bool IsRound => Kind == ItemKind.Artist;
    public bool IsNavigable => BrowseId is not null && VideoId is null;
    public bool CanPlay => VideoId is not null || PlaylistId is not null;

    private bool _isCurrent;
    public bool IsCurrent { get => _isCurrent; set => Set(ref _isCurrent, value); }

    public MediaItem Clone() => (MediaItem)MemberwiseClone();
}

public enum ShelfStyle { Carousel, QuickPicks, List, Grid }

public sealed class Shelf
{
    public string? Title { get; set; }
    public string? Strapline { get; set; }
    public ShelfStyle Style { get; set; }
    public List<MediaItem> Items { get; } = [];
    public bool ShowIndex { get; set; }
}

public sealed class PageHeader
{
    public string? Title { get; set; }
    public string? Subtitle { get; set; }
    public string? SecondSubtitle { get; set; }
    public string? Strapline { get; set; }
    public string? Description { get; set; }
    public string? Thumbnail { get; set; }
    public bool IsArtist { get; set; }
}

/// <summary>A paging token. BodyStyle = sent as {"continuation": token}; otherwise as ?ctoken= query.</summary>
public sealed record Continuation(string Token, bool BodyStyle);

public sealed class BrowseResult
{
    public PageHeader? Header { get; set; }
    public List<Shelf> Shelves { get; } = [];
    public Continuation? Continuation { get; set; }
}

public sealed class ContinuationResult
{
    public List<Shelf> Shelves { get; } = [];
    public List<MediaItem> Items { get; } = [];
    public Continuation? Next { get; set; }
}

public sealed class QueueResult
{
    public List<MediaItem> Items { get; } = [];
    public Continuation? Next { get; set; }
    public string? PlaylistId { get; set; }
}

public sealed record AccountInfo(string? Name, string? Photo);
