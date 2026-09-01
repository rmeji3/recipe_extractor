namespace Recipe.Api.Models.Substitution;

/// <summary>
/// A curated entry: what an ingredient does, and what can stand in for it.
/// </summary>
/// <remarks>
/// This table exists so the model has something to choose *from*. Asked to invent a
/// substitution, a language model will produce something plausible-sounding at any
/// confidence — and a wrong substitution ruins dinner, which is a far worse failure than a
/// wrong search result. Every swap the app proposes traces back to a row here.
/// </remarks>
public class IngredientRule
{
    public Guid Id { get; set; }

    /// <summary>Normalised name this rule is keyed by, e.g. "butter".</summary>
    public required string Canonical { get; set; }

    /// <summary>Other spellings that resolve here — "unsalted butter", "sweet cream butter".</summary>
    public List<string> Aliases { get; set; } = [];

    public IngredientFunction Function { get; set; }

    public List<Substitution> Substitutions { get; set; } = [];
}

/// <param name="Replacement">What to use instead.</param>
/// <param name="Ratio">
/// Multiplier on the original quantity. 1.0 is like-for-like; 0.75 means use three
/// quarters as much. Getting this wrong is the difference between a cake and a brick.
/// </param>
/// <param name="Tags">
/// What this swap achieves — "vegan", "dairy-free", "gluten-free", "lower-calorie",
/// "higher-protein". The goal filters on these.
/// </param>
/// <param name="Effect">
/// The honest knock-on. Surfaced to the user rather than hidden: they are cooking this,
/// and "the crumb will be denser" is information they need before they start.
/// </param>
/// <param name="Note">Any extra handling the swap requires.</param>
public record Substitution(
    string Replacement,
    double Ratio,
    List<string> Tags,
    string Effect,
    string? Note = null);
