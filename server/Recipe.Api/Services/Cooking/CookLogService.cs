using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Cooking;
using Recipe.Api.Models.Cooking;
using Recipe.Api.Services.Pantry;

namespace Recipe.Api.Services.Cooking;

public interface ICookLogService
{
    /// <summary>Records a cook, and learns its ingredients.</summary>
    Task<CookLogDto> LogAsync(
        string userId, Guid recipeId, LogCookRequest request, CancellationToken cancellationToken = default);

    Task<RecipeHistoryDto> HistoryAsync(
        string userId, Guid recipeId, CancellationToken cancellationToken = default);
}

public class CookLogService(AppDbContext db, IPantryService pantry, TimeProvider timeProvider)
    : ICookLogService
{
    public async Task<CookLogDto> LogAsync(
        string userId,
        Guid recipeId,
        LogCookRequest request,
        CancellationToken cancellationToken = default)
    {
        var recipe = await db.Recipes
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var log = new CookLog
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RecipeId = recipeId,
            CookedAt = now,
            Servings = request.Servings,
            Rating = request.Rating,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim()
        };

        db.CookLogs.Add(log);
        await db.SaveChangesAsync(cancellationToken);

        // Cooking something is proof the ingredients are ones this person works with —
        // stronger evidence than saving the recipe, which only shows intent. This is what
        // makes substitution suggestions come from their own shelf over time, with no
        // separate step for the user to remember.
        var ingredients = recipe.Ingredients.Select(i => i.Item).ToList();
        await pantry.AddAsync(userId, ingredients, addedByUser: false, cancellationToken);

        return new CookLogDto(
            log.Id, recipeId, log.CookedAt, log.Servings, log.Rating, log.Notes, ingredients);
    }

    public async Task<RecipeHistoryDto> HistoryAsync(
        string userId,
        Guid recipeId,
        CancellationToken cancellationToken = default)
    {
        var owned = await db.Recipes
            .AnyAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken);

        if (!owned)
        {
            throw new KeyNotFoundException($"Recipe {recipeId} was not found.");
        }

        var entries = await db.CookLogs
            .Where(l => l.UserId == userId && l.RecipeId == recipeId)
            .OrderByDescending(l => l.CookedAt)
            .Select(l => new CookLogDto(
                l.Id, l.RecipeId, l.CookedAt, l.Servings, l.Rating, l.Notes, new List<string>()))
            .ToListAsync(cancellationToken);

        return new RecipeHistoryDto(
            recipeId, entries.Count, entries.FirstOrDefault()?.CookedAt, entries);
    }
}
