using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Dtos.Recipes;

/// <param name="SavedPostId">Null on a variant — it came from another recipe, not a video.</param>
/// <param name="VariantLabel">Which adaptation this is: "vegetarian". Null on an original.</param>
/// <param name="DerivedFromRecipeId">The recipe this was adapted from. Null on an original.</param>
public record RecipeDto(
    Guid Id,
    Guid? SavedPostId,
    ExtractionStatus Status,
    string? FailureReason,
    string Title,
    int? Servings,
    int? PrepMinutes,
    int? CookMinutes,
    List<RecipeIngredient> Ingredients,
    List<RecipeStep> Steps,
    List<string> Equipment,
    double FoodConfidence,
    string? TranscriptLanguage,
    bool IsEdited,
    string? CreatorHandle,
    string? SourceUrl,
    DateTime? ExtractedAt,
    DateTime UpdatedAt,
    string? VariantLabel = null,
    Guid? DerivedFromRecipeId = null);

/// <summary>One adaptation of a dish, as a tab.</summary>
public record RecipeVariantDto(Guid Id, string Label, int IngredientCount, int StepCount);

/// <summary>
/// List row. Counts rather than full lists, so the query stays cheap.
/// </summary>
/// <remarks>
/// Only originals appear as rows. Adaptations hang off the dish they came from in
/// <see cref="Variants"/>, so a search for "butter chicken" returns one result with tabs
/// rather than four near-identical rows the user has to tell apart.
/// </remarks>
public record RecipeSummaryDto(
    Guid Id,
    Guid? SavedPostId,
    ExtractionStatus Status,
    string Title,
    int IngredientCount,
    int StepCount,
    double FoodConfidence,
    string? CreatorHandle,
    string? SourceUrl,
    bool IsEdited,
    DateTime UpdatedAt)
{
    /// <summary>Adaptations of this dish. Empty when there are none.</summary>
    public List<RecipeVariantDto> Variants { get; init; } = [];
}
