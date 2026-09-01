using Recipe.Api.Models.Recipes;
using Recipe.Api.Models.Substitution;

namespace Recipe.Api.Services.Substitution;

/// <summary>
/// Works out which substitutions are legitimately available for one recipe, before any
/// model is involved.
/// </summary>
/// <remarks>
/// This is what makes the feature grounded rather than a prompt. The model is never asked
/// "how would you make this vegetarian" — it is handed a fixed list of swaps drawn from the
/// curated table, filtered by the goal and the user's profile, and asked to choose. A
/// replacement that is not on the list cannot survive validation, so the failure mode is
/// "no suggestion" rather than "confident nonsense".
/// </remarks>
public class SubstitutionCandidates(IReadOnlyList<IngredientRule> rules)
{
    /// <summary>Tags implied by each dietary pattern. A vegan swap is also a vegetarian one.</summary>
    private static readonly Dictionary<DietaryPattern, string[]> DietTags = new()
    {
        [DietaryPattern.Vegetarian] = ["vegetarian", "vegan"],
        [DietaryPattern.Vegan] = ["vegan"],
        [DietaryPattern.Pescatarian] = ["vegetarian", "vegan"],
    };

    /// <summary>Free text the user might type, mapped onto the tags the table actually uses.</summary>
    private static readonly (string[] Words, string Tag)[] GoalWords =
    [
        (["vegan", "plant based", "plant-based"], "vegan"),
        (["vegetarian", "veggie", "meat free", "meat-free"], "vegetarian"),
        (["dairy free", "dairy-free", "lactose"], "dairy-free"),
        (["gluten free", "gluten-free", "coeliac", "celiac"], "gluten-free"),
        (["healthier", "healthy", "lighter", "lower calorie", "fewer calories", "leaner", "cut calories"], "lower-calorie"),
        (["protein", "higher protein", "more protein", "bulking"], "higher-protein"),
        (["fibre", "fiber", "wholegrain", "whole grain"], "higher-fibre"),
        (["low carb", "low-carb", "keto", "fewer carbs"], "lower-carb"),
        (["salt", "sodium", "less salty"], "lower-sodium"),
    ];

    /// <param name="Ingredient">The recipe line this applies to.</param>
    /// <param name="RuleId">The curated rule it came from.</param>
    /// <param name="Options">Legal replacements. The model picks one of these or nothing.</param>
    public record Candidate(RecipeIngredient Ingredient, Guid RuleId, IReadOnlyList<Option> Options);

    /// <param name="InCorpus">
    /// Whether this user already cooks with it — it appears elsewhere in their library, so
    /// they have bought it before and liked the result.
    /// </param>
    /// <param name="InPantry">
    /// Whether it is in the house right now. Outranks <paramref name="InCorpus"/>: a swap
    /// they can make tonight without shopping beats one they merely know.
    /// </param>
    public record Option(
        string Replacement, double Ratio, string Effect, string? Note,
        bool InCorpus, bool InPantry, List<string> Tags);

    /// <summary>Turns a free-text goal into the tags the rules table is filtered by.</summary>
    public static List<string> TagsForGoal(string goal, UserProfile? profile)
    {
        var lower = goal.ToLowerInvariant();
        var tags = GoalWords.Where(g => g.Words.Any(lower.Contains)).Select(g => g.Tag).ToList();

        if (profile is not null && DietTags.TryGetValue(profile.Diet, out var dietTags))
        {
            // A standing dietary pattern applies whether or not it was mentioned. Someone
            // vegan asking to "make it healthier" still wants a vegan result.
            tags.AddRange(dietTags);
        }

        if (profile is not null)
        {
            tags.AddRange(profile.Goals);
        }

        return [.. tags.Distinct()];
    }

    /// <summary>
    /// Builds the choices for a recipe.
    /// </summary>
    /// <param name="ingredients">The recipe's current ingredients.</param>
    /// <param name="goalTags">Tags from <see cref="TagsForGoal"/>. Empty means any swap is on the table.</param>
    /// <param name="avoid">Ingredients to exclude entirely — allergies and hard dislikes.</param>
    /// <param name="corpus">Ingredient names appearing across this user's other recipes.</param>
    /// <param name="pantry">Ingredient names the user has in the house right now.</param>
    public IReadOnlyList<Candidate> Build(
        IEnumerable<RecipeIngredient> ingredients,
        IReadOnlyCollection<string> goalTags,
        IReadOnlyCollection<string> avoid,
        IReadOnlyCollection<string> corpus,
        IReadOnlyCollection<string>? pantry = null)
    {
        var avoidSet = avoid.Select(Normalise).ToHashSet();
        var corpusSet = corpus.Select(Normalise).ToHashSet();
        var pantrySet = (pantry ?? []).Select(Normalise).ToHashSet();
        var candidates = new List<Candidate>();

        foreach (var ingredient in ingredients)
        {
            var rule = Match(ingredient.Item);

            if (rule is null)
            {
                continue;
            }

            var options = rule.Substitutions
                // An empty goal means "anything sensible"; otherwise the swap has to
                // actually achieve what was asked for.
                .Where(s => goalTags.Count == 0 || s.Tags.Any(goalTags.Contains))
                // Never offer something the user cannot eat, whatever the goal.
                .Where(s => !avoidSet.Contains(Normalise(s.Replacement)))
                .Select(s => new Option(
                    s.Replacement, s.Ratio, s.Effect, s.Note,
                    corpusSet.Contains(Normalise(s.Replacement)),
                    pantrySet.Contains(Normalise(s.Replacement)),
                    s.Tags))
                // Already in the house first, then already in their library. This is the
                // half no competitor can copy — it needs an imported corpus and a pantry
                // to exist at all.
                .OrderByDescending(o => o.InPantry)
                .ThenByDescending(o => o.InCorpus)
                .ToList();

            if (options.Count > 0)
            {
                candidates.Add(new Candidate(ingredient, rule.Id, options));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Finds the rule for an ingredient line.
    /// </summary>
    /// <remarks>
    /// Recipe text is messy — "boneless, skinless chicken thighs, cut into bite-sized
    /// pieces" has to reach the chicken rule. Exact match first, then the longest alias
    /// contained in the text, so "chicken thighs" wins over a bare "chicken" and
    /// "wholemeal flour" is never mistaken for "flour".
    /// </remarks>
    private IngredientRule? Match(string item)
    {
        var normalised = Normalise(item);

        var exact = rules.FirstOrDefault(r =>
            Normalise(r.Canonical) == normalised
            || r.Aliases.Any(a => Normalise(a) == normalised));

        if (exact is not null)
        {
            return exact;
        }

        return rules
            .Select(rule => new
            {
                Rule = rule,
                Length = rule.Aliases.Append(rule.Canonical)
                    .Where(name => ContainsWord(normalised, Normalise(name)))
                    .Select(name => name.Length)
                    .DefaultIfEmpty(0)
                    .Max()
            })
            .Where(x => x.Length > 0)
            .OrderByDescending(x => x.Length)
            .Select(x => x.Rule)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whole-word containment. Substring matching alone would find "rice" inside "liquorice".
    /// </summary>
    private static bool ContainsWord(string haystack, string needle)
    {
        var index = haystack.IndexOf(needle, StringComparison.Ordinal);

        while (index >= 0)
        {
            var startsClean = index == 0 || haystack[index - 1] == ' ';
            var end = index + needle.Length;
            var endsClean = end == haystack.Length || haystack[end] == ' ';

            if (startsClean && endsClean)
            {
                return true;
            }

            index = haystack.IndexOf(needle, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Normalise(string value) =>
        new(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray());
}
