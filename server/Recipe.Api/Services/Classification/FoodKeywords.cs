using System.Text.RegularExpressions;

namespace Recipe.Api.Services.Classification;

/// <summary>
/// The free first pass. Resolves a chunk of any library confidently in both directions
/// with no model call, leaving only the ambiguous middle to pay for.
/// </summary>
/// <remarks>
/// Measured on a real corpus, keywords alone are not precise enough to import on: the
/// vocabulary flagged a Warzone clip on "season" and a Cookie Monster cartoon on "cookie".
/// So a keyword hit is a ranking hint that decides what to *ask about first*, never a
/// verdict on its own — only the strongest signals short-circuit the model.
/// </remarks>
public static partial class FoodKeywords
{
    /// <summary>
    /// Unambiguous recipe markers. A caption carrying one of these is food with no further
    /// argument — nobody writes "#recipe" or an ingredient list over a gaming clip.
    /// </summary>
    [GeneratedRegex(
        @"#recipe\b|#foodtok\b|#easyrecipe|#mealprep|\bfull recipe\b|\brecipe below\b|"
        + @"\bingredients?\s*[:：]|\b\d+\s*(tbsp|tsp|teaspoons?|tablespoons?|cups?|"
        + @"grams?|g\b|kg|oz|ounces?|ml|lbs?|pounds?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StrongFood { get; }

    /// <summary>General cooking vocabulary. Suggestive, not conclusive.</summary>
    [GeneratedRegex(
        @"\b(recipe|recipes|cook|cooking|cooked|bake|baking|baked|grill|grilled|roast|"
        + @"roasted|fried|air ?fryer|marinate|marinade|simmer|saute|sauté|whisk|knead|"
        + @"ingredient|ingredients|dinner|lunch|breakfast|brunch|dessert|snack|meal|meals|"
        + @"mealprep|dish|chicken|beef|pork|steak|salmon|shrimp|pasta|noodle|rice|bread|"
        + @"dough|cake|cookie|brownie|sauce|soup|stew|salad|taco|pizza|burger|garlic|onion|"
        + @"butter|cheese|eggs?|foodtok|homemade|kitchen|chef|protein|calories?)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex WeakFood { get; }

    /// <summary>
    /// Vocabulary that marks a caption as belonging to another world entirely. Present
    /// because the weak list has real false positives — "season" and "cookie" both appear
    /// in gaming and cartoon captions.
    /// </summary>
    /// <remarks>
    /// Every term here must be one that never appears in a recipe. "stock" was in this list
    /// as a financial word and silently rejected "one pot orzo #recipe 2 cups stock" — it
    /// would have dropped every soup and risotto in a library. Financial senses now need
    /// their disambiguating word.
    /// </remarks>
    [GeneratedRegex(
        @"\b(warzone|fortnite|minecraft|roblox|gameplay|gaming|loadout|smg|"
        + @"skincare|makeup|outfit|streetwear|crypto|nft|stock market|"
        + @"softwareengineer|programming|coding|leetcode|systemdesign|startup)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotFood { get; }

    public enum Verdict
    {
        /// <summary>Certain enough to skip the model.</summary>
        Food,
        /// <summary>Certain enough to skip the model.</summary>
        NotFood,
        /// <summary>Needs the model. This is the only tier that costs anything.</summary>
        Unsure
    }

    /// <summary>Judges a caption without a model call.</summary>
    public static (Verdict Verdict, double Confidence) Judge(string? caption)
    {
        if (string.IsNullOrWhiteSpace(caption))
        {
            return (Verdict.Unsure, 0);
        }

        var negative = NotFood.IsMatch(caption);

        if (StrongFood.IsMatch(caption))
        {
            // An explicit recipe marker outranks the negative list. Nobody writes
            // "#recipe" or a measured ingredient over a gaming clip, and a stray
            // collision should not cost a real recipe.
            return negative ? (Verdict.Unsure, 0.5) : (Verdict.Food, 0.95);
        }

        if (negative)
        {
            // A gaming or coding caption that also mentions "season" is still not a recipe.
            return WeakHits(caption) >= 4 ? (Verdict.Unsure, 0.3) : (Verdict.NotFood, 0.9);
        }

        var hits = WeakHits(caption);

        return hits switch
        {
            0 => (Verdict.NotFood, 0.75),
            >= 4 => (Verdict.Food, 0.85),
            _ => (Verdict.Unsure, 0.3 + 0.1 * hits)
        };
    }

    /// <summary>Distinct cooking terms, so one word repeated does not look like four.</summary>
    private static int WeakHits(string caption) =>
        WeakFood.Matches(caption)
            .Select(m => m.Value.ToLowerInvariant())
            .Distinct()
            .Count();
}
