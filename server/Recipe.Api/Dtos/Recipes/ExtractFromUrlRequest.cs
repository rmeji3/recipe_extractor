using System.ComponentModel.DataAnnotations;

namespace Recipe.Api.Dtos.Recipes;

/// <summary>A single post to turn into a recipe — the share-sheet path.</summary>
public record ExtractFromUrlRequest
{
    /// <summary>
    /// A TikTok or Instagram link in any form the share sheet or a paste produces,
    /// including <c>vm.tiktok.com</c> short links.
    /// </summary>
    [Required, StringLength(2048, MinimumLength = 8)]
    public required string Url { get; init; }
}
