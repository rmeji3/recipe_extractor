using System.ComponentModel.DataAnnotations;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Dtos.Import;

/// <summary>A batch of saved posts from one platform export.</summary>
public record CreateImportRequest
{
    [Required]
    public required SourcePlatform Platform { get; init; }

    /// <summary>
    /// TikTok favourites only — never the like list. A like list is ambient scrolling,
    /// eight times the volume, and is itself capped at 6,000 by the export.
    /// </summary>
    [Required]
    [MinLength(1)]
    [MaxLength(MaxPosts)]
    public required List<ImportPostDto> Posts { get; init; }

    /// <summary>
    /// Ceiling on one batch. The largest real export seen is 786 favourites; this leaves
    /// headroom without letting a single request become unbounded work.
    /// </summary>
    public const int MaxPosts = 5000;
}
