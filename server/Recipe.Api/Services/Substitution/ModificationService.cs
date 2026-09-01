using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Models.Substitution;
using Recipe.Api.Services.Pantry;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Api.Services.Substitution;

public interface IModificationService
{
    /// <summary>Proposes a rewrite. Nothing is saved to the recipe until it is accepted.</summary>
    Task<ModificationDto> ProposeAsync(
        string userId, Guid recipeId, string goal, CancellationToken cancellationToken = default);

    /// <summary>Applies an accepted proposal, as a new recipe alongside the original.</summary>
    Task<Dtos.Recipes.RecipeDto> AcceptAsync(
        string userId, Guid modificationId, CancellationToken cancellationToken = default);
}

public class ModificationService(
    AppDbContext db,
    IIngredientRuleStore rules,
    IModificationClient client,
    IPantryService pantry,
    TimeProvider timeProvider,
    ILogger<ModificationService> logger) : IModificationService
{
    public async Task<ModificationDto> ProposeAsync(
        string userId,
        Guid recipeId,
        string goal,
        CancellationToken cancellationToken = default)
    {
        goal = goal.Trim();

        if (goal.Length == 0)
        {
            throw new DomainValidationException("Say what you want changed.");
        }

        var recipe = await db.Recipes
            .Include(r => r.SavedPost)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        if (recipe.Ingredients.Count == 0)
        {
            throw new DomainValidationException(
                "This recipe has no ingredients yet, so there is nothing to substitute.");
        }

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var goalTags = SubstitutionCandidates.TagsForGoal(goal, profile);
        var corpus = await CorpusIngredientsAsync(userId, recipeId, cancellationToken);

        var builder = new SubstitutionCandidates(await rules.GetAllAsync(cancellationToken));
        var candidates = builder.Build(
            recipe.Ingredients, goalTags, profile?.Avoid ?? [], corpus,
            await pantry.NamesAsync(userId, cancellationToken));

        if (candidates.Count == 0)
        {
            // Honest answer. The alternative — letting the model improvise — is exactly the
            // failure this design exists to prevent.
            return new ModificationDto(
                Id: null,
                RecipeId: recipe.Id,
                Goal: goal,
                Changes: [],
                Summary: "Nothing in this recipe has a substitution on file for that. "
                         + "Rather than invent one, it has been left alone.",
                Warnings: []);
        }

        var selection = await client.SelectAsync(
            new ModificationPrompt(recipe.Title, goal, profile?.Notes, candidates), cancellationToken);

        var (changes, warnings) = Validate(selection, candidates);

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var modification = new RecipeModification
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecipeId = recipe.Id,
            Goal = goal,
            Changes = changes,
            Summary = selection.Summary,
            CreatedAt = now
        };

        db.RecipeModifications.Add(modification);
        await db.SaveChangesAsync(cancellationToken);

        return new ModificationDto(
            modification.Id, recipe.Id, goal, changes, selection.Summary, warnings);
    }

    /// <summary>
    /// Discards anything the model produced that is not backed by a candidate.
    /// </summary>
    /// <remarks>
    /// The prompt says to choose only from the list. This assumes it will not. Every change
    /// is re-checked against the candidates by ingredient and by replacement, and the
    /// effect text is taken from the rule rather than from the response — so the warning a
    /// user reads about their crumb going dense is one a person wrote, not one a model
    /// improvised.
    /// </remarks>
    private (List<AppliedChange> Changes, List<string> Warnings) Validate(
        ModificationSelection selection,
        IReadOnlyList<SubstitutionCandidates.Candidate> candidates)
    {
        var changes = new List<AppliedChange>();
        var warnings = new List<string>();

        foreach (var proposed in selection.Changes)
        {
            var candidate = candidates.FirstOrDefault(c =>
                string.Equals(c.Ingredient.Item, proposed.From, StringComparison.OrdinalIgnoreCase));

            if (candidate is null)
            {
                logger.LogWarning("Discarded a change to an ingredient with no candidate: {From}", proposed.From);
                warnings.Add($"Ignored a suggested change to \"{proposed.From}\" — it is not in this recipe.");
                continue;
            }

            var option = candidate.Options.FirstOrDefault(o =>
                string.Equals(o.Replacement, proposed.To, StringComparison.OrdinalIgnoreCase));

            if (option is null)
            {
                logger.LogWarning(
                    "Discarded an ungrounded substitution: {From} -> {To}", proposed.From, proposed.To);
                warnings.Add(
                    $"Ignored \"{proposed.To}\" for \"{proposed.From}\" — there is no tested "
                    + "substitution on file for it.");
                continue;
            }

            changes.Add(new AppliedChange(
                From: candidate.Ingredient.Item,
                To: option.Replacement,
                // The ratio comes from the rule, never from the model. This is the number
                // that decides whether the dish works.
                Quantity: candidate.Ingredient.Quantity is { } q ? Math.Round(q * option.Ratio, 2) : null,
                Unit: candidate.Ingredient.Unit,
                Effect: option.Note is null ? option.Effect : $"{option.Effect} {option.Note}",
                RuleId: candidate.RuleId,
                InCorpus: option.InCorpus));
        }

        return (changes, warnings);
    }

    public async Task<Dtos.Recipes.RecipeDto> AcceptAsync(
        string userId,
        Guid modificationId,
        CancellationToken cancellationToken = default)
    {
        var modification = await db.RecipeModifications
            .FirstOrDefaultAsync(m => m.Id == modificationId && m.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Modification {modificationId} was not found.");

        if (modification.Accepted && modification.ResultRecipeId is { } already)
        {
            return await ReadAsync(userId, already, cancellationToken);
        }

        var original = await db.Recipes
            .Include(r => r.SavedPost)
            .FirstOrDefaultAsync(r => r.Id == modification.RecipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("The original recipe is gone.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var swaps = modification.Changes.ToDictionary(c => c.From, StringComparer.OrdinalIgnoreCase);

        var ingredients = original.Ingredients
            .Select(i => swaps.TryGetValue(i.Item, out var change)
                ? i with
                {
                    Item = change.To,
                    Quantity = change.Quantity,
                    // The user did not type this, and it came from a table rather than
                    // being read off the video — so it is confident, but not certain.
                    Confidence = 0.9,
                    PrepNote = null,
                    SourceTs = null
                }
                : i)
            .ToList();

        // A new row, not an edit. The original came from a real video and stays intact —
        // replacing it would destroy the thing the substitution was derived from.
        var modified = new RecipeEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            // No post of its own: one recipe per post is a unique index, and this did not
            // come from a video. It points at the recipe it was adapted from instead.
            SavedPostId = null,
            DerivedFromRecipeId = original.Id,
            VariantLabel = VariantLabel(modification.Goal),
            Status = ExtractionStatus.Extracted,
            // The dish keeps its name. The variant is distinguished by its label, which is
            // what a tab shows — appending the goal to every title made search results read
            // as four different dishes.
            Title = original.Title,
            Servings = original.Servings,
            PrepMinutes = original.PrepMinutes,
            CookMinutes = original.CookMinutes,
            Ingredients = ingredients,
            Steps = original.Steps,
            Equipment = [.. original.Equipment],
            FoodConfidence = original.FoodConfidence,
            TranscriptLanguage = original.TranscriptLanguage,
            IsEdited = true,
            ExtractedAt = original.ExtractedAt,
            CreatedAt = now,
            UpdatedAt = now
        };

        // Without this the variant is invisible to search — it has no post to inherit a
        // creator from and nothing else writes the column for it.
        modified.SearchText = RecipeSearchText.Build(
            modified.Title, modified.Ingredients, modified.Equipment, original.SavedPost);

        db.Recipes.Add(modified);
        modification.Accepted = true;
        modification.ResultRecipeId = modified.Id;

        await db.SaveChangesAsync(cancellationToken);

        return await ReadAsync(userId, modified.Id, cancellationToken);
    }

    /// <summary>
    /// Turns a spoken goal into a tab label: "make it vegetarian" becomes "vegetarian".
    /// </summary>
    private static string VariantLabel(string goal)
    {
        var label = goal.Trim().ToLowerInvariant();

        foreach (var prefix in (string[])["make it ", "make this ", "make ", "turn it ", "turn this "])
        {
            if (label.StartsWith(prefix, StringComparison.Ordinal))
            {
                label = label[prefix.Length..];
                break;
            }
        }

        label = label.Trim().TrimEnd('.', '!', '?');

        return label.Length == 0
            ? "variant"
            : label.Length <= 32 ? label : label[..32].TrimEnd();
    }

    /// <summary>
    /// Ingredients this user already cooks with, drawn from their other recipes.
    /// </summary>
    /// <remarks>
    /// The corpus-grounding layer. Knowing what someone actually cooks is only possible
    /// because they imported hundreds of recipes, which is the part no competitor has.
    /// </remarks>
    private async Task<List<string>> CorpusIngredientsAsync(
        string userId, Guid excludeRecipeId, CancellationToken cancellationToken)
    {
        var others = await db.Recipes
            .Where(r => r.UserId == userId && r.Id != excludeRecipeId
                        && r.Status == ExtractionStatus.Extracted)
            .Select(r => r.Ingredients)
            .ToListAsync(cancellationToken);

        return [.. others.SelectMany(list => list).Select(i => i.Item).Distinct()];
    }

    private async Task<Dtos.Recipes.RecipeDto> ReadAsync(
        string userId, Guid recipeId, CancellationToken cancellationToken)
    {
        var row = await db.Recipes
            .Include(r => r.SavedPost)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        return new Dtos.Recipes.RecipeDto(
            row.Id, row.SavedPostId, row.Status, row.FailureReason, row.Title, row.Servings,
            row.PrepMinutes, row.CookMinutes, row.Ingredients, row.Steps, row.Equipment,
            row.FoodConfidence, row.TranscriptLanguage, row.IsEdited,
            row.SavedPost?.CreatorHandle, row.SavedPost?.Url, row.ExtractedAt, row.UpdatedAt,
            row.VariantLabel, row.DerivedFromRecipeId);
    }
}
