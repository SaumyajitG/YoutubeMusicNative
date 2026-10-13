using System.Windows;
using System.Windows.Controls;
using YoutubeMusicNative.Models;

namespace YoutubeMusicNative.Views;

/// <summary>Mood/genre buttons render as coloured chips; everything else as an artwork card.</summary>
public sealed class CardTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Card { get; set; }
    public DataTemplate? Chip { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is MediaItem { Kind: ItemKind.Category } ? Chip : Card;
}
