using System.ComponentModel.DataAnnotations;

namespace Recipe.Api.Dtos.Cooking;

public record LogCookRequest
{
    [Range(1, 100)]
    public int? Servings { get; init; }

    [Range(1, 5)]
    public int? Rating { get; init; }

    /// <summary>
    /// What they would do differently — "needed longer", "half the chilli". The most
    /// valuable text in the app and the least likely to be written, so never required.
    /// </summary>
    [StringLength(1000)]
    public string? Notes { get; init; }
}

/// <param name="LearnedIngredients">
/// Ingredients added to the pantry because this cook proved they are used. Substitution
/// prefers these afterwards.
/// </param>
public record CookLogDto(
    Guid Id,
    Guid RecipeId,
    DateTime CookedAt,
    int? Servings,
    int? Rating,
    string? Notes,
    List<string> LearnedIngredients);

public record RecipeHistoryDto(
    Guid RecipeId, int TimesCooked, DateTime? LastCookedAt, List<CookLogDto> Entries);

public record PurchasedRequest
{
    /// <summary>
    /// Items ticked off a shopping list. They join the pantry — buying something is decent
    /// evidence you cook with it, and a shopping trip is when that list is most accurate.
    /// </summary>
    [Required, MinLength(1), MaxLength(200)]
    public required List<string> Items { get; init; }
}
