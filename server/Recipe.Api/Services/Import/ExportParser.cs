using System.Text.Json;
using System.Text.RegularExpressions;
using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Services.Import;

/// <summary>
/// Turns a raw platform export file into the normalised post array the import endpoint
/// takes. In production the app does this on device; this server-side path exists as the
/// documented fallback for exports the on-device parser cannot make sense of, and for
/// testing against real files.
/// </summary>
/// <remarks>
/// Every rule here was derived from real export files. The layout varies by account, so
/// the parsers detect rather than assume, and never index positionally.
/// </remarks>
public static partial class ExportParser
{
    /// <summary>Instagram post permalink: captures the path segment and the shortcode.</summary>
    [GeneratedRegex(@"instagram\.com/(p|reel|tv)/([^/?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InstagramPermalink { get; }

    /// <summary>TikTok share link: captures the numeric video id.</summary>
    [GeneratedRegex(@"/video/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TikTokVideoId { get; }

    /// <summary>
    /// Detects which platform a file came from and parses it.
    /// </summary>
    /// <param name="stream">The raw export JSON.</param>
    /// <param name="includeLikes">
    /// TikTok only. Likes are ambient scrolling — eight times the volume of favourites for
    /// worse results, and the export caps the list — so they are excluded by default.
    /// </param>
    public static ExportParseResult Parse(Stream stream, bool includeLikes = false)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(stream);
        }
        catch (JsonException ex)
        {
            throw new DomainValidationException(
                "That file is not valid JSON. Instagram and TikTok both offer a JSON export — " +
                $"if you requested HTML, request it again as JSON. ({ex.Message})");
        }

        using (document)
        {
            var root = document.RootElement;

            if (LooksLikeTikTok(root))
            {
                return ParseTikTok(root, includeLikes);
            }

            if (TryFindInstagramRecords(root, out var records))
            {
                return ParseInstagram(records);
            }

            throw new DomainValidationException(
                "Could not recognise this file. Expected an Instagram saved_posts.json " +
                "(an array of saved posts) or a TikTok user_data_tiktok.json " +
                "(with a \"Likes and Favorites\" section).");
        }
    }

    // ---------------------------------------------------------------- Instagram

    private static bool LooksLikeTikTok(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Likes and Favorites", out _);

    /// <summary>
    /// The wrapper shape varies by account: some exports are a bare array of records,
    /// others nest that array under a key such as <c>saved_saved_posts</c>. Detect it.
    /// </summary>
    private static bool TryFindInstagramRecords(JsonElement root, out JsonElement records)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            records = root;
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    records = property.Value;
                    return true;
                }
            }
        }

        records = default;
        return false;
    }

    private static ExportParseResult ParseInstagram(JsonElement records)
    {
        var posts = new List<ImportPostDto>();
        var skipped = 0;

        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object)
            {
                skipped++;
                continue;
            }

            var post = ParseInstagramRecord(record);

            if (post is null)
            {
                skipped++;
                continue;
            }

            posts.Add(post);
        }

        return new ExportParseResult(SourcePlatform.Instagram, posts, skipped);
    }

    private static ImportPostDto? ParseInstagramRecord(JsonElement record)
    {
        string? url = null;
        string? handle = null;
        string? name = null;
        var captions = new List<string>();
        var hashtags = new List<string>();

        if (record.TryGetProperty("label_values", out var labelValues)
            && labelValues.ValueKind == JsonValueKind.Array)
        {
            foreach (var (path, label, value) in Leaves(labelValues))
            {
                // Labels collide across groups: "Name" appears under Hashtags, Owner and
                // Brand partner, and "URL" is both the post permalink and the owner's
                // link-in-bio. Match on the group path, never the bare label.
                switch (path.Count, label)
                {
                    case (0, "URL"):
                        url ??= value;
                        break;
                    case (0, "Caption"):
                        captions.Add(value);
                        break;
                    case (1, "Username") when path[0] == "Owner":
                        handle ??= CaptionText.RepairMojibake(value);
                        break;
                    case (1, "Name") when path[0] == "Owner":
                        name ??= CaptionText.RepairMojibake(value);
                        break;
                    case (1, "Name") when path[0] == "Hashtags":
                        hashtags.Add(CaptionText.RepairMojibake(value));
                        break;
                }
            }
        }

        // Some export variants carry the permalink in string_map_data instead.
        url ??= TryStringMapHref(record);

        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var match = InstagramPermalink.Match(url);

        if (!match.Success)
        {
            return null;
        }

        return new ImportPostDto
        {
            PlatformItemId = match.Groups[2].Value,
            Url = url,
            // The path segment is a ranking hint only. Carousels carry real recipes, so it
            // must never become a filter.
            Kind = match.Groups[1].Value.Equals("reel", StringComparison.OrdinalIgnoreCase)
                ? SavedPostKind.Reel
                : SavedPostKind.Post,
            Captions = captions.Count == 0 ? null : captions,
            CreatorHandle = Truncate(handle, 128),
            CreatorName = Truncate(name, 256),
            Hashtags = hashtags.Count == 0 ? null : hashtags,
            SavedAt = ReadUnixTimestamp(record)
        };
    }

    /// <summary>
    /// Walks <c>label_values</c> depth-first, yielding each leaf with the group titles it
    /// sits under. Group entries carry a <c>dict</c> and no <c>label</c> at all, so a flat
    /// loop either crashes or silently drops the owner data. Unnamed intermediate groups
    /// (<c>title: ""</c>) are skipped so paths stay meaningful.
    /// </summary>
    private static IEnumerable<(IReadOnlyList<string> Path, string Label, string Value)> Leaves(
        JsonElement entries,
        List<string>? path = null)
    {
        path ??= [];

        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (entry.TryGetProperty("dict", out var nested) && nested.ValueKind == JsonValueKind.Array)
            {
                var title = entry.TryGetProperty("title", out var t) ? t.GetString() : null;
                var pushed = !string.IsNullOrEmpty(title);

                if (pushed)
                {
                    path.Add(title!);
                }

                foreach (var leaf in Leaves(nested, path))
                {
                    yield return leaf;
                }

                if (pushed)
                {
                    path.RemoveAt(path.Count - 1);
                }

                continue;
            }

            if (entry.TryGetProperty("label", out var labelElement)
                && labelElement.GetString() is { } label
                && entry.TryGetProperty("value", out var valueElement)
                && valueElement.GetString() is { Length: > 0 } value)
            {
                yield return (path.ToArray(), label, value);
            }
        }
    }

    private static string? TryStringMapHref(JsonElement record)
    {
        if (!record.TryGetProperty("string_map_data", out var map) || map.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("href", out var href)
                && href.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Instagram timestamps are unix seconds, not milliseconds.</summary>
    private static DateTime? ReadUnixTimestamp(JsonElement record) =>
        record.TryGetProperty("timestamp", out var ts)
        && ts.ValueKind == JsonValueKind.Number
        && ts.TryGetInt64(out var seconds)
        && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;

    // ------------------------------------------------------------------ TikTok

    private static ExportParseResult ParseTikTok(JsonElement root, bool includeLikes)
    {
        var section = root.GetProperty("Likes and Favorites");
        var posts = new List<ImportPostDto>();
        var skipped = 0;

        // Favourites and likes use different casing for the same fields in the same file
        // ("Date"/"Link" versus "date"/"link"), so every read here is case-insensitive.
        skipped += ReadTikTokList(section, "Favorite Videos", "FavoriteVideoList", posts);

        if (includeLikes)
        {
            skipped += ReadTikTokList(section, "Like List", "ItemFavoriteList", posts);
        }

        return new ExportParseResult(SourcePlatform.TikTok, posts, skipped);
    }

    private static int ReadTikTokList(
        JsonElement section,
        string sectionName,
        string listName,
        List<ImportPostDto> posts)
    {
        if (!TryGetPropertyIgnoreCase(section, sectionName, out var container)
            || !TryGetPropertyIgnoreCase(container, listName, out var list)
            || list.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        var skipped = 0;
        var seen = posts.Select(p => p.PlatformItemId).ToHashSet(StringComparer.Ordinal);

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(item, "link", out var linkElement)
                || linkElement.GetString() is not { Length: > 0 } link)
            {
                skipped++;
                continue;
            }

            var match = TikTokVideoId.Match(link);

            if (!match.Success)
            {
                skipped++;
                continue;
            }

            var id = match.Groups[1].Value;

            // The favourites and like lists overlap; keep the favourite.
            if (!seen.Add(id))
            {
                continue;
            }

            posts.Add(new ImportPostDto
            {
                PlatformItemId = id,
                Url = link,
                Kind = SavedPostKind.Video,
                // No caption and no creator on this path — nothing but a date and a link.
                // Stage 1 metadata fetch fills those in later.
                SavedAt = ReadTikTokDate(item)
            });
        }

        return skipped;
    }

    private static DateTime? ReadTikTokDate(JsonElement item) =>
        TryGetPropertyIgnoreCase(item, "date", out var dateElement)
        && dateElement.GetString() is { Length: > 0 } raw
        && DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];
}
