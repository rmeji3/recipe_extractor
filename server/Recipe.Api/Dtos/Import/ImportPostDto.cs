using System.ComponentModel.DataAnnotations;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Dtos.Import;

/// <summary>
/// One normalised saved post, as produced by the on-device zip parser.
/// </summary>
public record ImportPostDto
{
    /// <summary>Instagram shortcode or TikTok numeric video id, resolved on device.</summary>
    [Required, StringLength(128, MinimumLength = 1)]
    public required string PlatformItemId { get; init; }

    [Required, StringLength(2048, MinimumLength = 1)]
    [Url]
    public required string Url { get; init; }

    public SavedPostKind Kind { get; init; } = SavedPostKind.Unknown;

    /// <summary>
    /// Every caption found on the post. Instagram repeats a caption verbatim across
    /// carousel slides, so the server deduplicates before joining — send them all
    /// rather than guessing which is canonical.
    /// </summary>
    public List<string>? Captions { get; init; }

    [StringLength(128)]
    public string? CreatorHandle { get; init; }

    [StringLength(256)]
    public string? CreatorName { get; init; }

    public List<string>? Hashtags { get; init; }

    /// <summary>When the user saved it on the platform.</summary>
    public DateTime? SavedAt { get; init; }
}
