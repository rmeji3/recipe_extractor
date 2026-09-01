using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Cooking;
using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Services.Cooking;

public interface ICookingService
{
    /// <summary>A recipe prepared for cooking: numbered steps, parsed timers, optional scaling.</summary>
    Task<CookModeDto> GetCookModeAsync(
        string userId, Guid recipeId, int? servings, CancellationToken cancellationToken = default);

    /// <summary>One shopping list across several recipes, with amounts combined where possible.</summary>
    Task<GroceryListDto> BuildGroceryListAsync(
        string userId, IReadOnlyList<Guid> recipeIds, CancellationToken cancellationToken = default);
}

public class CookingService(AppDbContext db) : ICookingService
{
    /// <summary>
    /// Sanity bound on scaling. Beyond this the arithmetic stops being the hard part —
    /// pan sizes, cooking times, and oven capacity all change — so pretending a simple
    /// multiplication is enough would be misleading.
    /// </summary>
    private const int MaxServings = 100;

    public async Task<CookModeDto> GetCookModeAsync(
        string userId,
        Guid recipeId,
        int? servings,
        CancellationToken cancellationToken = default)
    {
        var recipe = await db.Recipes
            .Include(r => r.SavedPost)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        var factor = 1.0;

        if (servings is { } target)
        {
            if (target is < 1 or > MaxServings)
            {
                throw new DomainValidationException($"Servings must be between 1 and {MaxServings}.");
            }

            if (recipe.Servings is not { } original || original < 1)
            {
                // Scaling to "6 servings" is meaningless when nobody said what the recipe
                // currently makes. Saying so beats silently returning the original.
                throw new DomainValidationException(
                    "This recipe does not say how many it serves, so it cannot be scaled. "
                    + "Set the servings first.");
            }

            factor = (double)target / original;
        }

        var ingredients = factor == 1.0
            ? recipe.Ingredients
            : [.. recipe.Ingredients.Select(i =>
            {
                var (quantity, unit) = Units.Scale(i.Quantity, i.Unit, factor);
                return i with { Quantity = quantity, Unit = unit };
            })];

        var steps = recipe.Steps
            .Select((step, index) => new CookStepDto(
                index + 1,
                step.Text,
                step.TsStart,
                [.. StepTimers.Parse(step.Text).Select(t => new TimerDto(t.Seconds, t.Label))]))
            .ToList();

        return new CookModeDto(
            recipe.Id,
            recipe.Title,
            servings ?? recipe.Servings,
            Math.Round(factor, 3),
            // Times are not scaled. Doubling a recipe does not double the cooking time —
            // it usually changes it hardly at all, and guessing here would be dangerous.
            recipe.PrepMinutes,
            recipe.CookMinutes,
            ingredients,
            steps,
            recipe.Equipment,
            recipe.SavedPost?.Url);
    }

    public async Task<GroceryListDto> BuildGroceryListAsync(
        string userId,
        IReadOnlyList<Guid> recipeIds,
        CancellationToken cancellationToken = default)
    {
        var ids = recipeIds.Distinct().ToList();

        var recipes = await db.Recipes
            .Where(r => r.UserId == userId && ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Title, r.Ingredients })
            .ToListAsync(cancellationToken);

        if (recipes.Count == 0)
        {
            throw new KeyNotFoundException("None of those recipes were found.");
        }

        // Grouped by ingredient name, keeping the order they were first seen so the list
        // reads in the order the recipes were chosen.
        var grouped = new List<(string Key, string Display, string? Group, List<(Guid Id, string Title, RecipeIngredient Ing)> Entries)>();
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipe in recipes)
        {
            foreach (var ingredient in recipe.Ingredients)
            {
                var key = Normalise(ingredient.Item);

                if (key.Length == 0)
                {
                    continue;
                }

                if (!index.TryGetValue(key, out var at))
                {
                    index[key] = grouped.Count;
                    grouped.Add((key, ingredient.Item, ingredient.Group, []));
                    at = grouped.Count - 1;
                }

                grouped[at].Entries.Add((recipe.Id, recipe.Title, ingredient));
            }
        }

        var items = grouped.Select(g =>
        {
            var sources = g.Entries
                .Select(e => new GrocerySourceDto(e.Id, e.Title, e.Ing.Quantity, e.Ing.Unit))
                .ToList();

            var (quantity, unit) = Combine(g.Entries.Select(e => (e.Ing.Quantity, e.Ing.Unit)));

            return new GroceryItemDto(g.Display, quantity, unit, g.Group, sources);
        }).ToList();

        return new GroceryListDto(recipes.Count, items);
    }

    /// <summary>
    /// Folds a set of amounts into one, or gives up.
    /// </summary>
    /// <remarks>
    /// Giving up is a real answer. Two tablespoons of butter and a hundred grams of butter
    /// cannot be added without a density table, so the item still appears once with its
    /// sources listed separately — which is what a person would write on a shopping list
    /// anyway.
    /// </remarks>
    private static (double? Quantity, string? Unit) Combine(IEnumerable<(double? Quantity, string? Unit)> amounts)
    {
        double? runningQuantity = null;
        string? runningUnit = null;
        var first = true;

        foreach (var (quantity, unit) in amounts)
        {
            if (first)
            {
                runningQuantity = quantity;
                runningUnit = Units.Canonical(unit);
                first = false;
                continue;
            }

            var sum = Units.TryAdd(runningQuantity, runningUnit, quantity, unit);

            if (sum is null)
            {
                return (null, null);
            }

            runningQuantity = sum.Value.Quantity;
            runningUnit = sum.Value.Unit;
        }

        return (runningQuantity, runningUnit);
    }

    private static string Normalise(string item) =>
        new string(item.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray()).Trim();
}
