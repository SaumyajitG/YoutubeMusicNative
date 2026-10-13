using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace YoutubeMusicNative.Infrastructure;

/// <summary>
/// Attached property that paints a remote image into a Border's background.
/// Images are fetched off the UI thread, decoded at display size (low memory),
/// frozen, and kept in a small LRU cache.
/// Usage: &lt;Border inf:Img.Size="200" inf:Img.Url="{Binding Thumbnail}" CornerRadius="4"/&gt;
/// </summary>
public static class Img
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Img), new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.RegisterAttached(
        "Size", typeof(int), typeof(Img), new PropertyMetadata(226));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);
    public static int GetSize(DependencyObject d) => (int)d.GetValue(SizeProperty);
    public static void SetSize(DependencyObject d, int value) => d.SetValue(SizeProperty, value);

    // ~250 KB per decoded cover; on-screen images stay alive through the UI regardless.
    private static readonly LruCache<string, ImageBrush> Cache = new(120);
    private static readonly ConcurrentDictionary<string, Task<ImageBrush?>> InFlight = new();
    private static readonly SemaphoreSlim Gate = new(8);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border border) return;
        var url = e.NewValue as string;
        if (string.IsNullOrEmpty(url))
        {
            border.SetResourceReference(Border.BackgroundProperty, "PlaceholderBrush");
            return;
        }

        var scale = VisualTreeHelper.GetDpi(border).DpiScaleX;
        var px = (int)Math.Ceiling(GetSize(border) * Math.Max(1, scale));
        var resolved = Thumbs.Resize(url, px);
        var key = resolved + "|" + px;

        if (Cache.TryGet(key, out var cached))
        {
            border.Background = cached;
            return;
        }

        border.SetResourceReference(Border.BackgroundProperty, "PlaceholderBrush");
        var brush = await InFlight.GetOrAdd(key, _ => LoadAsync(resolved, px, key));
        if (brush is not null && GetUrl(border) == url) border.Background = brush;
    }

    private static async Task<ImageBrush?> LoadAsync(string url, int px, string key)
    {
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            byte[] bytes;
            try { bytes = await App.Http.GetByteArrayAsync(url).ConfigureAwait(false); }
            finally { Gate.Release(); }

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.DecodePixelWidth = px;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();

            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            brush.Freeze();
            Cache.Add(key, brush);
            return brush;
        }
        catch
        {
            return null;
        }
        finally
        {
            InFlight.TryRemove(key, out _);
        }
    }
}

internal sealed class LruCache<TKey, TValue>(int capacity) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _list = new();
    private readonly Lock _lock = new();

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing)) _list.Remove(existing);
            var node = _list.AddFirst((key, value));
            _map[key] = node;
            while (_map.Count > capacity && _list.Last is { } last)
            {
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }
}
