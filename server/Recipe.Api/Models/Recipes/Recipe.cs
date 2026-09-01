using Recipe.Api.Models.Import;

namespace Recipe.Api.Models.Recipes;

/// <summary>
/// A structured recipe extracted from one saved post. One row per post, created the first
/// time extraction runs and updated on re-runs.
/// </summary>
public class Recipe
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    /// <summary>
    /// The post this was extracted from. Null on a variant produced by substitution — that
    /// came from another recipe, not from a video.
    /// </summary>
    public Guid? SavedPostId { get; set; }
    public SavedPost? SavedPost { get; set; }

    /// <summary>
    /// Set when this recipe is an adaptation of another. The original keeps its post and
    /// stays untouched: it is what the substitution was derived from, and overwriting it
    /// would destroy the source.
    /// </summary>
    public Guid? DerivedFromRecipeId { get; set; }

    /// <summary>
    /// Short name for a variant — "vegetarian", "higher protein". What a tab is labelled.
    /// Null on an original.
    /// </summary>
    public string? VariantLabel { get; set; }

    public ExtractionStatus Status { get; set; }

    /// <summary>Set when <see cref="Status"/> is Failed — the sidecar's message.</summary>
    public string? FailureReason { get; set; }

    public string Title { get; set; } = string.Empty;
    public int? Servings { get; set; }
    public int? PrepMinutes { get; set; }
    public int? CookMinutes { get; set; }

    public List<RecipeIngredient> Ingredients { get; set; } = [];
    public List<RecipeStep> Steps { get; set; } = [];
    public List<string> Equipment { get; set; } = [];

    public double FoodConfidence { get; set; }

    /// <summary>Language Whisper detected, when the narration path ran.</summary>
    public string? TranscriptLanguage { get; set; }

    /// <summary>
    /// Flattened text the search index is built from: title, ingredient items, equipment,
    /// creator. Maintained on every write rather than derived at query time, because the
    /// real fields live in JSON columns that no provider can index usefully.
    /// </summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>
    /// Postgres full-text vector, generated from <see cref="SearchText"/>. Mapped only on
    /// Npgsql — SQLite has no equivalent and ignores this property, falling back to LIKE.
    /// </summary>
    public NpgsqlTypes.NpgsqlTsVector? SearchVector { get; set; }

    /// <summary>True once the user has edited any field by hand.</summary>
    public bool IsEdited { get; set; }

    public DateTime? ExtractedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <param name="Group">
/// The part of the dish this belongs to — "Chicken", "Sauce", "Marinade" — taken from the
/// recipe's own headings. Null when the source is one undivided list. Flattening a recipe
/// that has a separate sauce makes it materially harder to cook from.
/// </param>
/// <param name="Quantity">Numeric amount, null when the source never stated one.</param>
/// <param name="Unit">tbsp, g, cup. Null when unstated.</param>
/// <param name="Item">The ingredient itself, e.g. "soy sauce".</param>
/// <param name="PrepNote">e.g. "low sodium", "finely diced".</param>
/// <param name="Confidence">
/// 0-1. Narration gives method but rarely amounts, so anything heard rather than read
/// should score lower than anything taken from a typed caption.
/// </param>
/// <param name="SourceTs">
/// Seconds into the video where this was first mentioned. Free to capture while the media
/// is already being processed, and what later lets a user tap an amount and jump to it.
/// </param>
public record RecipeIngredient(
    string? Group,
    double? Quantity,
    string? Unit,
    string Item,
    string? PrepNote,
    double Confidence,
    double? SourceTs);

/// <param name="Text">One instruction, paraphrased in neutral voice — never the creator's wording.</param>
/// <param name="TsStart">Seconds where this step begins, when a transcript supplied it.</param>
/// <param name="TsEnd">Seconds where this step ends.</param>
public record RecipeStep(string Text, double? TsStart, double? TsEnd);
