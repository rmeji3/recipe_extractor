namespace Recipe.Api.Models.Substitution;

/// <summary>
/// A proposed rewrite of a recipe, and whether the user took it.
/// </summary>
/// <remarks>
/// Kept after the fact on purpose. Accepted and rejected proposals are the only honest
/// signal about whether the substitutions are any good, and they are what lets the profile
/// learn — someone who declines every tofu swap has told you something.
/// </remarks>
public class RecipeModification
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public Guid RecipeId { get; set; }

    /// <summary>What was asked for, verbatim: "make it vegetarian", "healthier".</summary>
    public required string Goal { get; set; }

    /// <summary>The swaps proposed, each traceable to a rule.</summary>
    public List<AppliedChange> Changes { get; set; } = [];

    /// <summary>What the model said about the result as a whole.</summary>
    public string? Summary { get; set; }

    public bool Accepted { get; set; }

    /// <summary>Set when accepted — the new recipe this produced.</summary>
    public Guid? ResultRecipeId { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <param name="From">The original ingredient.</param>
/// <param name="To">What replaced it.</param>
/// <param name="Quantity">Adjusted amount, with the rule's ratio already applied.</param>
/// <param name="Unit">Unit for <paramref name="Quantity"/>.</param>
/// <param name="Effect">The honest knock-on, copied from the rule rather than written by the model.</param>
/// <param name="RuleId">
/// The curated rule this came from. Present on every change: a swap with no rule behind it
/// is one the model invented, and those are discarded before the user ever sees them.
/// </param>
/// <param name="InCorpus">
/// True when the replacement already appears in this user's own recipes — they have cooked
/// with it, so it is a better suggestion than something they have never bought.
/// </param>
public record AppliedChange(
    string From,
    string To,
    double? Quantity,
    string? Unit,
    string Effect,
    Guid RuleId,
    bool InCorpus);
