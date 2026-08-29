using System.Text.RegularExpressions;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Common;

/// <summary>
/// Recognises the URL shapes a saved post can arrive in — from an export, from a paste,
/// or from the iOS share sheet — and reduces them to a platform and an item id.
/// </summary>
/// <remarks>
/// One place for this on purpose. The same link appears in at least four forms across the
/// product (export share link, oEmbed's id-only form, yt-dlp's canonical creator form, and
/// the short link the share sheet produces), and the item id is the cross-user cache key
/// that ties them together, so parsing it inconsistently would quietly split the cache.
/// </remarks>
public static partial class PostUrl
{
    [GeneratedRegex(@"tiktok(?:v)?\.com/(?:@[^/]+/)?(?:share/)?video/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TikTokVideo { get; }

    [GeneratedRegex(@"instagram\.com/(p|reel|reels|tv)/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramPost { get; }

    /// <summary>
    /// Short links the share sheet hands out. These carry no item id at all — they have to
    /// be followed to the destination before anything can be read from them.
    /// </summary>
    [GeneratedRegex(@"^https?://(?:vm|vt)\.tiktok\.com/|^https?://(?:www\.)?tiktok\.com/t/",
        RegexOptions.IgnoreCase)]
    private static partial Regex TikTokShortLink { get; }

    public record Parsed(SourcePlatform Platform, string ItemId, SavedPostKind Kind, string CanonicalUrl);

    /// <summary>True when the URL must be followed before it can be parsed.</summary>
    public static bool IsShortLink(string url) => TikTokShortLink.IsMatch(url.Trim());

    public static bool TryParse(string? url, out Parsed parsed)
    {
        parsed = null!;

        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        url = url.Trim();

        var tiktok = TikTokVideo.Match(url);

        if (tiktok.Success)
        {
            var id = tiktok.Groups[1].Value;
            parsed = new Parsed(
                SourcePlatform.TikTok,
                id,
                SavedPostKind.Video,
                // Not yt-dlp's form: that one needs the creator handle, which is only known
                // after stage 1. This is the id-only form oEmbed accepts.
                $"https://www.tiktok.com/video/{id}");
            return true;
        }

        var instagram = InstagramPost.Match(url);

        if (instagram.Success)
        {
            var segment = instagram.Groups[1].Value.ToLowerInvariant();
            var shortcode = instagram.Groups[2].Value;
            parsed = new Parsed(
                SourcePlatform.Instagram,
                shortcode,
                // A ranking hint only. Carousels carry real recipes, so this must never
                // become a filter.
                segment is "reel" or "reels" ? SavedPostKind.Reel : SavedPostKind.Post,
                $"https://www.instagram.com/{(segment is "reel" or "reels" ? "reel" : "p")}/{shortcode}/");
            return true;
        }

        return false;
    }
}
