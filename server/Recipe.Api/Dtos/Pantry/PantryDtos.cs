using System.ComponentModel.DataAnnotations;

namespace Recipe.Api.Dtos.Pantry;

/// <param name="TimesUsed">How many cooked recipes have contained it. A familiarity score.</param>
/// <param name="AddedByUser">True when told to us rather than inferred from a cook.</param>
public record PantryItemDto(
    Guid Id, string Item, int TimesUsed, bool AddedByUser, DateTime AddedAt);

public record AddPantryItemsRequest
{
    /// <summary>
    /// Items to add. A list rather than one at a time because the realistic entry points
    /// are a shopping trip and a finished grocery list, both of which are bulk.
    /// </summary>
    [Required, MinLength(1), MaxLength(100)]
    public required List<AddPantryItem> Items { get; init; }
}

public record AddPantryItem
{
    [Required, StringLength(128, MinimumLength = 1)]
    public required string Item { get; init; }
}

/// <param name="Have">Ingredients this cook already works with.</param>
/// <param name="Missing">Ingredients they have never used.</param>
/// <param name="Coverage">
/// 0-1. How familiar the recipe is, not whether it can be made tonight — that would need
/// real stock tracking. A high score means nothing here is unfamiliar.
/// </param>
public record CookabilityDto(
    Guid RecipeId,
    string Title,
    int Have,
    int Missing,
    double Coverage,
    List<string> MissingItems);
