using System.ComponentModel.DataAnnotations;
using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Dtos.Cooking;

/// <param name="Seconds">Duration in seconds; the upper bound when the step gave a range.</param>
/// <param name="Label">The phrase it came from, so the UI can name the timer.</param>
public record TimerDto(int Seconds, string Label);

/// <param name="Timers">
/// Parsed from the step text, not asked of a model — free, instant, and incapable of
/// inventing a duration nobody wrote.
/// </param>
public record CookStepDto(int Number, string Text, double? TsStart, List<TimerDto> Timers);

/// <param name="Servings">The scaled figure, when scaling was applied.</param>
/// <param name="ScaledBy">1.0 when untouched.</param>
public record CookModeDto(
    Guid RecipeId,
    string Title,
    int? Servings,
    double ScaledBy,
    int? PrepMinutes,
    int? CookMinutes,
    List<RecipeIngredient> Ingredients,
    List<CookStepDto> Steps,
    List<string> Equipment,
    string? SourceUrl);

public record GroceryListRequest
{
    /// <summary>Recipes to shop for. Duplicated ingredients across them are combined.</summary>
    [Required, MinLength(1), MaxLength(50)]
    public required List<Guid> RecipeIds { get; init; }
}

/// <param name="Quantity">
/// Null when the amounts could not honestly be combined — see <paramref name="Sources"/>,
/// which then lists them separately.
/// </param>
/// <param name="Sources">Which recipes wanted it, and how much each asked for.</param>
public record GroceryItemDto(
    string Item,
    double? Quantity,
    string? Unit,
    string? Group,
    List<GrocerySourceDto> Sources);

public record GrocerySourceDto(Guid RecipeId, string RecipeTitle, double? Quantity, string? Unit);

public record GroceryListDto(int RecipeCount, List<GroceryItemDto> Items);
