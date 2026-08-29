using System.ComponentModel.DataAnnotations;
using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Dtos.Recipes;

/// <summary>
/// A user's edits to an extracted recipe. Every field is replaced, so send the whole
/// recipe back, not a delta.
/// </summary>
/// <remarks>
/// Extraction is confident but not infallible — narration mishears ingredients and
/// on-screen text never carries amounts — so the user has to be able to fix anything.
/// An edited recipe is marked as such and is never overwritten by a later re-extraction.
/// </remarks>
public record UpdateRecipeRequest
{
    [Required, StringLength(512, MinimumLength = 1)]
    public required string Title { get; init; }

    [Range(1, 100)]
    public int? Servings { get; init; }

    [Range(0, 1440)]
    public int? PrepMinutes { get; init; }

    [Range(0, 1440)]
    public int? CookMinutes { get; init; }

    [MaxLength(200)]
    public List<UpdateIngredient> Ingredients { get; init; } = [];

    [MaxLength(100)]
    public List<UpdateStep> Steps { get; init; } = [];

    [MaxLength(50)]
    public List<string> Equipment { get; init; } = [];
}

public record UpdateIngredient
{
    [Range(0, 100000)]
    public double? Quantity { get; init; }

    [StringLength(32)]
    public string? Unit { get; init; }

    [Required, StringLength(256, MinimumLength = 1)]
    public required string Item { get; init; }

    [StringLength(256)]
    public string? PrepNote { get; init; }

    /// <summary>Preserved from extraction so a tap-to-source jump still works after edits.</summary>
    public double? SourceTs { get; init; }
}

public record UpdateStep
{
    [Required, StringLength(2048, MinimumLength = 1)]
    public required string Text { get; init; }

    public double? TsStart { get; init; }
    public double? TsEnd { get; init; }
}
