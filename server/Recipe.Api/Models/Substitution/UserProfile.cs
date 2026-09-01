namespace Recipe.Api.Models.Substitution;

/// <summary>
/// What the app remembers about how someone eats.
/// </summary>
/// <remarks>
/// Stated preferences only. The inferred half — what this person actually cooks — is
/// derived from their recipe corpus at request time rather than stored here, because it
/// changes every time they import or save something and a stale copy would be worse than
/// no copy.
/// </remarks>
public class UserProfile
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public DietaryPattern Diet { get; set; }

    /// <summary>
    /// Ingredients to keep out entirely — allergies and hard dislikes. Enforced as a
    /// filter, never as a suggestion: an allergy the model merely takes into account is an
    /// allergy that eventually gets ignored.
    /// </summary>
    public List<string> Avoid { get; set; } = [];

    /// <summary>
    /// What they are generally aiming for — "lower-calorie", "higher-protein",
    /// "higher-fibre". Matches the tags on <see cref="Substitution"/>.
    /// </summary>
    public List<string> Goals { get; set; } = [];

    /// <summary>Free text the user wrote about themselves. Context for the model, never a filter.</summary>
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>Values are pinned and are a wire contract — see server/CLAUDE.md.</summary>
public enum DietaryPattern
{
    None = 0,
    Vegetarian = 1,
    Vegan = 2,
    Pescatarian = 3
}
