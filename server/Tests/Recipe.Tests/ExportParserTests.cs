using System.Text;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Models.Import;
using Recipe.Api.Services.Import;

namespace Recipe.Tests;

/// <summary>
/// Fixtures are trimmed, anonymised versions of real export records. Every case here is a
/// quirk confirmed against actual files — see the root README.
/// </summary>
public class ExportParserTests
{
    private static Recipe.Api.Dtos.Import.ExportParseResult Parse(string json, bool includeLikes = false)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return ExportParser.Parse(stream, includeLikes);
    }

    // ------------------------------------------------------------- Instagram

    private const string InstagramExport = """
    [
      {
        "timestamp": 1785369434,
        "media": [],
        "fbid": "1",
        "label_values": [
          { "label": "URL", "value": "https://www.instagram.com/p/AAAA1111/", "href": "https://www.instagram.com/p/AAAA1111/" },
          { "label": "Caption", "value": "Garlic pasta" },
          { "label": "Title", "value": "" },
          { "label": "Caption", "value": "Garlic pasta" },
          { "title": "Hashtags", "dict": [
              { "title": "", "dict": [ { "label": "Name", "value": "recipe" } ] },
              { "title": "", "dict": [ { "label": "Name", "value": "pasta" } ] }
          ]},
          { "title": "Owner", "dict": [
              { "title": "", "dict": [
                  { "label": "URL", "value": "https://somechef.example/links" },
                  { "label": "Name", "value": "Some Chef" },
                  { "label": "Username", "value": "somechef" }
              ]}
          ]},
          { "title": "Brand partner", "dict": [] }
        ]
      },
      {
        "timestamp": 1785369000,
        "media": [],
        "fbid": "2",
        "label_values": [
          { "label": "URL", "value": "https://www.instagram.com/reel/BBBB2222/" }
        ]
      }
    ]
    """;

    [Fact]
    public void Instagram_reads_a_bare_array_of_records()
    {
        var result = Parse(InstagramExport);

        Assert.Equal(SourcePlatform.Instagram, result.Platform);
        Assert.Equal(2, result.Posts.Count);
        Assert.Equal(0, result.SkippedCount);
    }

    [Fact]
    public void Instagram_takes_the_post_url_not_the_owners_link_in_bio()
    {
        // "URL" exists at depth 0 as the permalink and under Owner as a personal site.
        // Keying leaves by bare label overwrites one with the other.
        var post = Parse(InstagramExport).Posts[0];

        Assert.Equal("https://www.instagram.com/p/AAAA1111/", post.Url);
        Assert.Equal("AAAA1111", post.PlatformItemId);
    }

    [Fact]
    public void Instagram_reads_the_creator_out_of_the_nested_owner_group()
    {
        var post = Parse(InstagramExport).Posts[0];

        Assert.Equal("somechef", post.CreatorHandle);
        Assert.Equal("Some Chef", post.CreatorName);
    }

    [Fact]
    public void Instagram_keeps_hashtag_names_separate_from_the_owner_name()
    {
        // "Name" appears under both Hashtags and Owner.
        var post = Parse(InstagramExport).Posts[0];

        Assert.Equal(["recipe", "pasta"], post.Hashtags);
    }

    [Fact]
    public void Instagram_collects_every_caption_for_the_service_to_deduplicate()
    {
        var post = Parse(InstagramExport).Posts[0];

        Assert.Equal(2, post.Captions!.Count);
    }

    [Fact]
    public void Instagram_distinguishes_reels_from_posts()
    {
        var posts = Parse(InstagramExport).Posts;

        Assert.Equal(SavedPostKind.Post, posts[0].Kind);
        Assert.Equal(SavedPostKind.Reel, posts[1].Kind);
    }

    [Fact]
    public void Instagram_reads_timestamps_as_unix_seconds()
    {
        var post = Parse(InstagramExport).Posts[0];

        // 1785369434 seconds since the epoch. Read as milliseconds it would land in 1970.
        Assert.Equal(new DateTime(2026, 7, 29, 23, 57, 14, DateTimeKind.Utc), post.SavedAt);
    }

    [Fact]
    public void Instagram_handles_a_group_entry_with_no_label_key()
    {
        // A flat loop over label_values crashes or drops data here.
        var result = Parse("""
        [ { "label_values": [
            { "title": "Owner", "dict": [ { "title": "", "dict": [ { "label": "Username", "value": "x" } ] } ] },
            { "label": "URL", "value": "https://www.instagram.com/p/CCCC3333/" }
        ] } ]
        """);

        Assert.Equal("CCCC3333", Assert.Single(result.Posts).PlatformItemId);
    }

    [Fact]
    public void Instagram_detects_a_wrapped_array_rather_than_assuming_a_bare_one()
    {
        // Some accounts export a saved_saved_posts wrapper instead.
        var result = Parse("""
        { "saved_saved_posts": [
            { "label_values": [ { "label": "URL", "value": "https://www.instagram.com/p/DDDD4444/" } ] }
        ] }
        """);

        Assert.Equal("DDDD4444", Assert.Single(result.Posts).PlatformItemId);
    }

    [Fact]
    public void Instagram_skips_records_with_no_usable_permalink()
    {
        var result = Parse("""
        [ { "label_values": [ { "label": "Caption", "value": "orphan" } ] },
          { "label_values": [ { "label": "URL", "value": "https://example.com/nope" } ] } ]
        """);

        Assert.Empty(result.Posts);
        Assert.Equal(2, result.SkippedCount);
    }

    // ---------------------------------------------------------------- TikTok

    private const string TikTokExport = """
    { "Likes and Favorites": {
        "Favorite Videos": {
          "App": 1,
          "FavoriteVideoList": [
            { "Date": "2026-08-28 22:22:56", "Link": "https://www.tiktokv.com/share/video/7679084446330473759/" },
            { "Date": "2025-01-02 10:00:00", "Link": "https://www.tiktokv.com/share/video/7000000000000000001/" }
          ]
        },
        "Like List": {
          "App": 1,
          "ItemFavoriteList": [
            { "date": "2026-08-29 04:00:41", "link": "https://www.tiktokv.com/share/video/7679207389060533535/" },
            { "date": "2026-08-27 04:00:41", "link": "https://www.tiktokv.com/share/video/7679084446330473759/" }
          ]
        },
        "Collection": {},
        "Favorite Collection": { "FavoriteCollectionList": [ { "Date": "2025-12-16 06:21:45", "FavoriteCollection": "Huh" } ] }
    } }
    """;

    [Fact]
    public void TikTok_imports_favourites_only_by_default()
    {
        var result = Parse(TikTokExport);

        Assert.Equal(SourcePlatform.TikTok, result.Platform);
        Assert.Equal(2, result.Posts.Count);
    }

    [Fact]
    public void TikTok_includes_likes_only_when_explicitly_asked()
    {
        var result = Parse(TikTokExport, includeLikes: true);

        // Three distinct ids across both lists; one video appears in each.
        Assert.Equal(3, result.Posts.Count);
    }

    [Fact]
    public void TikTok_reads_the_lowercase_field_names_the_like_list_uses()
    {
        // Favourites use Date/Link, likes use date/link, in the same file.
        var result = Parse(TikTokExport, includeLikes: true);
        var liked = result.Posts.Single(p => p.PlatformItemId == "7679207389060533535");

        Assert.NotNull(liked.SavedAt);
        Assert.Equal(new DateTime(2026, 8, 29), liked.SavedAt!.Value.Date);
    }

    [Fact]
    public void TikTok_extracts_the_numeric_video_id_as_the_cache_key()
    {
        var post = Parse(TikTokExport).Posts[0];

        Assert.Equal("7679084446330473759", post.PlatformItemId);
        Assert.Equal(SavedPostKind.Video, post.Kind);
    }

    [Fact]
    public void TikTok_posts_carry_no_caption_or_creator()
    {
        // Nothing but a date and a link until stage 1 metadata fetch runs.
        var post = Parse(TikTokExport).Posts[0];

        Assert.Null(post.Captions);
        Assert.Null(post.CreatorHandle);
    }

    // ------------------------------------------------------------- Detection

    [Fact]
    public void Rejects_html_with_an_actionable_message()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<html><body>Saved posts</body></html>"));

        var ex = Assert.Throws<DomainValidationException>(() => ExportParser.Parse(stream));

        Assert.Contains("JSON", ex.Message);
    }

    [Fact]
    public void Rejects_json_that_is_not_a_recognised_export()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""{"something":"else"}"""));

        Assert.Throws<DomainValidationException>(() => ExportParser.Parse(stream));
    }
}
