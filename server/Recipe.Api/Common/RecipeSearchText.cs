using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;

namespace Recipe.Api.Common;

/// <summary>
/// Builds the flattened text the search index is made from.
/// </summary>
/// <remarks>
/// Shared because every write path has to produce it identically. Ingredients and steps
/// live in JSON columns no provider indexes usefully, so the searchable text is maintained
/// alongside them — and a path that forgets to sets it makes the recipe silently
/// unfindable, which is exactly what happened to substitution variants.
/// </remarks>
public static class RecipeSearchText
{
    public static string Build(
        string title,
        IEnumerable<RecipeIngredient> ingredients,
        IEnumerable<string> equipment,
        SavedPost? post)
    {
        var parts = new List<string> { title };
        parts.AddRange(ingredients.Select(i => i.Item));
        parts.AddRange(equipment);

        if (!string.IsNullOrWhiteSpace(post?.CreatorHandle))
        {
            parts.Add(post.CreatorHandle);
        }

        if (!string.IsNullOrWhiteSpace(post?.CreatorName))
        {
            parts.Add(post.CreatorName);
        }

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    public static string Build(Models.Recipes.Recipe recipe, SavedPost? post) =>
        Build(recipe.Title, recipe.Ingredients, recipe.Equipment, post);
}
