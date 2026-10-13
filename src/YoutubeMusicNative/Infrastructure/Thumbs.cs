using System.Text.Json.Nodes;

namespace YoutubeMusicNative.Infrastructure;

internal static class Thumbs
{
    /// <summary>Picks the largest thumbnail from a "thumbnails" array.</summary>
    public static string? Best(JsonNode? thumbnails)
    {
        var url = thumbnails.Items().LastOrDefault().Get("url").Str();
        if (url is null) return null;
        return url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
    }

    /// <summary>Google image CDN URLs encode the size in the suffix; request exactly what we render.</summary>
    public static string Resize(string url, int size)
    {
        if (url.Contains("googleusercontent.com", StringComparison.Ordinal) || url.Contains("ggpht.com", StringComparison.Ordinal))
        {
            var eq = url.LastIndexOf('=');
            if (eq > url.LastIndexOf('/')) return $"{url[..eq]}=w{size}-h{size}-l90-rj";
        }
        return url;
    }
}
