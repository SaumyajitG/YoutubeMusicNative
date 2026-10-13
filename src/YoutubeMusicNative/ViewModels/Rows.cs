using System.Windows.Input;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;

namespace YoutubeMusicNative.ViewModels;

// A page is rendered as one flat, virtualized list of rows. Each row type has an implicit
// DataTemplate (Themes/Templates.xaml). Flattening keeps long playlists cheap: only visible
// rows are realized.

public sealed class ShelfViewModel
{
    public ShelfViewModel(Shelf shelf, bool playAsQueue)
    {
        Model = shelf;
        PlayAsQueue = playAsQueue;
        Items = shelf.Items;
        TrackRows = shelf.Items.Select(i => new TrackRow(i, this)).ToList();
        PlayItemCommand = new RelayCommand(p => { if (p is MediaItem item) PlayItem(item); });
    }

    public Shelf Model { get; }
    public string? Title => Model.Title;
    public string? Strapline => Model.Strapline;
    public ShelfStyle Style => Model.Style;
    public bool ShowIndex => Model.ShowIndex;
    public List<MediaItem> Items { get; }
    public List<TrackRow> TrackRows { get; }

    /// <summary>On album/playlist pages clicking a song queues the whole list; elsewhere it starts a radio.</summary>
    public bool PlayAsQueue { get; }

    public ICommand PlayItemCommand { get; }

    private void PlayItem(MediaItem item)
    {
        var main = App.Vm;
        if (item.VideoId is null) { main.Open(item); return; }
        if (PlayAsQueue) main.Player.PlayList(Items, Items.Where(i => i.VideoId is not null).ToList().IndexOf(item));
        else _ = main.Player.PlayRadioAsync(item);
    }
}

public sealed record TrackRow(MediaItem Item, ShelfViewModel Shelf);
public sealed record CarouselRow(ShelfViewModel Shelf);
public sealed record GridRow(ShelfViewModel Shelf);
public sealed record ShelfHeaderRow(ShelfViewModel Shelf);
public sealed record TitleRow(string Title, string? Subtitle);
public sealed record DetailHeaderRow(PageHeader Header, PageViewModel Page);
public sealed record ArtistHeaderRow(PageHeader Header, PageViewModel Page);
public sealed record MessageRow(string Text, string? ActionText = null, ICommand? Action = null);
public sealed class LoadingRow { public static readonly LoadingRow Instance = new(); }
