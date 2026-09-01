namespace Recipe.Api.Models.Pantry;

/// <summary>
/// An ingredient this person actually cooks with.
/// </summary>
/// <remarks>
/// Deliberately not an inventory. This tracks *familiarity*, not stock: whether someone
/// has used an ingredient before, which is what makes it a good substitution to suggest.
/// Telling a cook to swap in gochujang they have never bought is a worse suggestion than
/// one built from their own shelf, whatever the quantity.
///
/// The distinction that matters is against the corpus signal: the library says what they
/// *saved*, this says what they have actually *made*. Saving is intent; cooking is proof.
///
/// Populated automatically when a recipe is cooked, and by hand for staples. Quantities
/// are recorded when offered but nothing depends on them yet — real stock tracking is a
/// later feature and a much harder one, since it needs every use deducted accurately or
/// it silently drifts wrong.
/// </remarks>
public class PantryItem
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    /// <summary>As the user wrote it — "olive oil", "greek yoghurt".</summary>
    public required string Item { get; set; }

    /// <summary>
    /// Lowercased and stripped of punctuation. Matching happens on this, so "Olive Oil"
    /// and "olive oil" are the same jar.
    /// </summary>
    public required string NormalisedItem { get; set; }

    /// <summary>
    /// Optional and currently unused for decisions — kept so stock tracking can be added
    /// later without a migration. Nothing should branch on it until that exists.
    /// </summary>
    public double? Quantity { get; set; }

    public string? Unit { get; set; }

    /// <summary>Set for perishables. Null means "keeps". Also awaiting real stock tracking.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>How many times a cooked recipe has contained this. A crude familiarity score.</summary>
    public int TimesUsed { get; set; }

    /// <summary>
    /// True when the user added it themselves rather than it arriving from a cook.
    /// A staple they told us about outranks one inferred from a single recipe.
    /// </summary>
    public bool AddedByUser { get; set; }

    public DateTime AddedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
