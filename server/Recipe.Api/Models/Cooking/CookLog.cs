namespace Recipe.Api.Models.Cooking;

/// <summary>
/// A record that someone actually cooked something.
/// </summary>
/// <remarks>
/// The only honest signal the product has. Saves and imports measure intent; this measures
/// use — and the plan's success criteria are about people returning in week two, which is
/// unanswerable without it.
///
/// One row per cook, not a counter on the recipe. The second time someone makes a dish
/// they usually change something, and a counter throws that away.
/// </remarks>
public class CookLog
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public Guid RecipeId { get; set; }

    public DateTime CookedAt { get; set; }

    /// <summary>How many it was scaled to, when it was scaled.</summary>
    public int? Servings { get; set; }

    /// <summary>1-5, optional. Whether they would make it again is the question that matters.</summary>
    public int? Rating { get; set; }

    /// <summary>
    /// What they would do differently — "needed longer", "half the chilli". The most
    /// valuable text in the app and the least likely to be written, so it is never required.
    /// </summary>
    public string? Notes { get; set; }
}
