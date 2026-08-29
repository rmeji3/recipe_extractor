namespace Recipe.Api.Models.Import;

/// <summary>
/// A post the user saved on a platform, as normalised by the on-device zip parser.
/// This is raw intake — classification and recipe extraction happen downstream.
/// </summary>
public class SavedPost
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public Guid ImportJobId { get; set; }
    public ImportJob? ImportJob { get; set; }

    public SourcePlatform Platform { get; set; }

    /// <summary>
    /// Instagram shortcode or TikTok numeric video id, resolved at parse time. Together
    /// with <see cref="Platform"/> this is the cross-user cache key for classification
    /// and extraction, and the deduplication key within a user.
    /// </summary>
    public required string PlatformItemId { get; set; }

    public required string Url { get; set; }

    public SavedPostKind Kind { get; set; }

    /// <summary>
    /// Distinct captions joined together. Instagram ships these in the export; TikTok
    /// has none until stage 1 metadata fetch, so this is null on that path.
    /// </summary>
    public string? Caption { get; set; }

    public string? CreatorHandle { get; set; }

    public string? CreatorName { get; set; }

    public List<string> Hashtags { get; set; } = [];

    /// <summary>
    /// When the user saved it on the platform, not when they imported it. UTC — stored as
    /// <see cref="DateTime"/> rather than <see cref="DateTimeOffset"/> because SQLite
    /// cannot translate a DateTimeOffset in an ORDER BY, and the test suite runs on it.
    /// </summary>
    public DateTime? SavedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>Stage 1 state. See <see cref="MetadataStatus"/>.</summary>
    public MetadataStatus MetadataStatus { get; set; }

    public DateTime? MetadataFetchedAt { get; set; }

    /// <summary>
    /// Poster frame from oEmbed. Worth storing: the review pile is unusable without
    /// thumbnails, and it costs nothing on a call already being made.
    /// </summary>
    public string? ThumbnailUrl { get; set; }
}
