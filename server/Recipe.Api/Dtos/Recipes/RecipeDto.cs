using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Dtos.Recipes;

/// <summary>One extracted recipe, with the source it came from.</summary>
public record RecipeDto(
    Guid Id,
    Guid SavedPostId,
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
    string? CreatorHandle,
    string? SourceUrl,
    DateTime? ExtractedAt,
    DateTime UpdatedAt);

/// <summary>List row. Counts rather than full lists, so the query stays cheap.</summary>
public record RecipeSummaryDto(
    Guid Id,
    Guid SavedPostId,
    ExtractionStatus Status,
    string Title,
    int IngredientCount,
    int StepCount,
    double FoodConfidence,
    string? CreatorHandle,
    string? SourceUrl,
    DateTime UpdatedAt);
