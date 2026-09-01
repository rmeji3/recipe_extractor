using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Pantry;
using Recipe.Api.Models.Pantry;
using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Services.Pantry;

public interface IPantryService
{
    Task<List<PantryItemDto>> ListAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Records ingredients this person cooks with. Existing entries are reinforced, not duplicated.</summary>
    Task<List<PantryItemDto>> AddAsync(
        string userId, IEnumerable<string> items, bool addedByUser = true,
        CancellationToken cancellationToken = default);

    Task RemoveAsync(string userId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>The normalised names, for ranking substitutions.</summary>
    Task<HashSet<string>> NamesAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Recipes ranked by how little unfamiliar shopping they need.</summary>
    Task<List<CookabilityDto>> FamiliarAsync(
        string userId, int limit, CancellationToken cancellationToken = default);
}

/// <summary>
/// Tracks which ingredients someone actually cooks with.
/// </summary>
/// <remarks>
/// Not an inventory. It answers "would this person know what to do with gochujang", which
/// is what makes a substitution worth suggesting — not "is there gochujang in the cupboard
/// right now", which needs every use deducted accurately or it silently drifts wrong.
/// Stock tracking is a later, harder feature.
/// </remarks>
public class PantryService(AppDbContext db, TimeProvider timeProvider) : IPantryService
{
    public async Task<List<PantryItemDto>> ListAsync(
        string userId, CancellationToken cancellationToken = default) =>
        await db.PantryItems
            .Where(p => p.UserId == userId)
            // Most-used first: the staples someone reaches for constantly are the ones
            // worth seeing, and the tail is a long list of one-off spices.
            .OrderByDescending(p => p.TimesUsed)
            .ThenBy(p => p.Item)
            .Select(p => new PantryItemDto(p.Id, p.Item, p.TimesUsed, p.AddedByUser, p.AddedAt))
            .ToListAsync(cancellationToken);

    public async Task<List<PantryItemDto>> AddAsync(
        string userId,
        IEnumerable<string> items,
        bool addedByUser = true,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var existing = await db.PantryItems
            .Where(p => p.UserId == userId)
            .ToDictionaryAsync(p => p.NormalisedItem, cancellationToken);

        foreach (var raw in items)
        {
            var name = raw.Trim();
            var key = Normalise(name);

            if (key.Length == 0)
            {
                continue;
            }

            if (existing.TryGetValue(key, out var current))
            {
                // Cooking with something again is more evidence, not a second entry.
                current.TimesUsed++;
                current.AddedByUser |= addedByUser;
                current.UpdatedAt = now;
                continue;
            }

            var item = new PantryItem
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Item = name,
                NormalisedItem = key,
                TimesUsed = 1,
                AddedByUser = addedByUser,
                AddedAt = now,
                UpdatedAt = now
            };

            db.PantryItems.Add(item);
            existing[key] = item;
        }

        await db.SaveChangesAsync(cancellationToken);

        return await ListAsync(userId, cancellationToken);
    }

    public async Task RemoveAsync(string userId, Guid itemId, CancellationToken cancellationToken = default)
    {
        var item = await db.PantryItems
            .FirstOrDefaultAsync(p => p.Id == itemId && p.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("No such pantry item.");

        db.PantryItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<HashSet<string>> NamesAsync(
        string userId, CancellationToken cancellationToken = default) =>
        [.. await db.PantryItems
            .Where(p => p.UserId == userId)
            .Select(p => p.NormalisedItem)
            .ToListAsync(cancellationToken)];

    public async Task<List<CookabilityDto>> FamiliarAsync(
        string userId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pantry = await NamesAsync(userId, cancellationToken);

        if (pantry.Count == 0)
        {
            return [];
        }

        var recipes = await db.Recipes
            .Where(r => r.UserId == userId && r.Status == ExtractionStatus.Extracted)
            .Select(r => new { r.Id, r.Title, r.Ingredients })
            .ToListAsync(cancellationToken);

        return [.. recipes
            .Select(r =>
            {
                var unfamiliar = r.Ingredients
                    .Where(i => !Knows(pantry, i.Item))
                    .Select(i => i.Item)
                    .ToList();

                var total = r.Ingredients.Count;
                var known = total - unfamiliar.Count;

                return new CookabilityDto(
                    r.Id, r.Title, known, unfamiliar.Count,
                    total == 0 ? 0 : Math.Round((double)known / total, 2),
                    // Enough to judge a shopping trip by; forty names is not information.
                    [.. unfamiliar.Take(8)]);
            })
            .Where(c => c.Have > 0)
            .OrderByDescending(c => c.Coverage)
            .ThenBy(c => c.Missing)
            .Take(Math.Clamp(limit, 1, 100))];
    }

    /// <summary>
    /// Whether this cook already works with an ingredient.
    /// </summary>
    /// <remarks>
    /// Matched loosely on purpose: a recipe says "boneless chicken thighs" and the pantry
    /// says "chicken". Treating those as different would mark a familiar dish as full of
    /// unknowns, which is the more annoying error by far.
    /// </remarks>
    private static bool Knows(HashSet<string> pantry, string ingredient)
    {
        var normalised = Normalise(ingredient);

        if (normalised.Length == 0)
        {
            return false;
        }

        return pantry.Contains(normalised)
               || pantry.Any(p => p.Length >= 3
                                  && (normalised.Contains(p, StringComparison.Ordinal)
                                      || p.Contains(normalised, StringComparison.Ordinal)));
    }

    private static string Normalise(string value) =>
        new string(value.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Trim();
}
