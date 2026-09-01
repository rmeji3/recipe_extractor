using System.Net;
using System.Net.Http.Json;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Cooking;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Tests;

/// <summary>
/// Turning a stored recipe into something you can cook from: numbered steps with timers,
/// scaling, and a shopping list across several recipes.
/// </summary>
public class CookingTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private Guid SeedRecipe(
        string userId,
        string title,
        int? servings,
        (double? Qty, string? Unit, string Item)[] ingredients,
        string[]? steps = null)
    {
        using var db = fixture.CreateDbContext();

        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Platform = SourcePlatform.Instagram,
            CreatedAt = DateTime.UtcNow
        };
        db.ImportJobs.Add(job);

        var post = new SavedPost
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ImportJobId = job.Id,
            Platform = SourcePlatform.Instagram,
            PlatformItemId = Guid.NewGuid().ToString("N")[..12],
            Url = "https://www.instagram.com/p/seed/",
            CreatedAt = DateTime.UtcNow
        };
        db.SavedPosts.Add(post);

        var recipe = new RecipeEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SavedPostId = post.Id,
            Status = ExtractionStatus.Extracted,
            Title = title,
            Servings = servings,
            PrepMinutes = 10,
            CookMinutes = 25,
            Ingredients = [.. ingredients.Select(i =>
                new RecipeIngredient(null, i.Qty, i.Unit, i.Item, null, 0.9, null))],
            Steps = [.. (steps ?? ["Cook it."]).Select(s => new RecipeStep(s, null, null))],
            SearchText = title,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();

        return recipe.Id;
    }

    // ------------------------------------------------------------ cook mode

    [Fact]
    public async Task Cook_mode_numbers_the_steps_and_finds_their_timers()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Braise", 4, [(1.0, "kg", "beef")],
            ["Sear the beef for 3 minutes a side.", "Simmer for 2 hours until tender."]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook", AppFixture.JsonOptions);

        Assert.Equal([1, 2], cook!.Steps.Select(s => s.Number));
        Assert.Equal(180, cook.Steps[0].Timers.Single().Seconds);
        Assert.Equal(7200, cook.Steps[1].Timers.Single().Seconds);
    }

    [Fact]
    public async Task A_step_with_two_timers_yields_both()
    {
        // "Sear 3 minutes, then rest 10" is two timers; collapsing them loses the rest.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Steak", 2, [(2.0, null, "steaks")],
            ["Sear for 3 minutes a side, then rest for 10 minutes."]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook", AppFixture.JsonOptions);

        Assert.Equal([180, 600], cook!.Steps[0].Timers.Select(t => t.Seconds));
    }

    [Fact]
    public async Task A_range_uses_the_longer_end()
    {
        // Finishing early is recoverable; under-cooking chicken is not.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Fry", 2, [(1.0, null, "chicken")],
            ["Fry for 4 to 5 minutes until golden."]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook", AppFixture.JsonOptions);

        Assert.Equal(300, cook!.Steps[0].Timers.Single().Seconds);
    }

    [Fact]
    public async Task An_oven_temperature_is_not_mistaken_for_a_timer()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Bake", 4, [(200.0, "g", "flour")],
            ["Heat the oven to 200 degrees C.", "Bake at 350 F for 25 minutes."]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook", AppFixture.JsonOptions);

        Assert.Empty(cook!.Steps[0].Timers);
        // The real timer in the second step still survives alongside the temperature.
        Assert.Equal(1500, cook.Steps[1].Timers.Single().Seconds);
    }

    // -------------------------------------------------------------- scaling

    [Fact]
    public async Task Scaling_multiplies_the_quantities()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Pasta", 4,
            [(400.0, "g", "spaghetti"), (2.0, "tbsp", "olive oil")]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook?servings=6", AppFixture.JsonOptions);

        Assert.Equal(1.5, cook!.ScaledBy);
        Assert.Equal(600, cook.Ingredients[0].Quantity);
        Assert.Equal(3, cook.Ingredients[1].Quantity);
        Assert.Equal(6, cook.Servings);
    }

    [Fact]
    public async Task Scaling_leaves_cooking_times_alone()
    {
        // Doubling a recipe barely changes how long it cooks. Scaling that number would be
        // actively dangerous.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Roast", 2, [(1.0, "kg", "chicken")]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook?servings=8", AppFixture.JsonOptions);

        Assert.Equal(25, cook!.CookMinutes);
        Assert.Equal(10, cook.PrepMinutes);
    }

    [Fact]
    public async Task A_pinch_stays_a_pinch_however_far_it_is_scaled()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Soup", 2, [(1.0, "pinch", "saffron"), (1.0, "cup", "stock")]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook?servings=8", AppFixture.JsonOptions);

        Assert.Equal(1, cook!.Ingredients[0].Quantity);
        Assert.Equal(4, cook.Ingredients[1].Quantity);
    }

    [Fact]
    public async Task Counts_scale_to_halves_rather_than_awkward_decimals()
    {
        // "1.5 eggs" is usable in a kitchen. "1.33 eggs" is not.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Cake", 4, [(2.0, null, "eggs")]);

        var cook = await client.GetFromJsonAsync<CookModeDto>(
            $"/api/recipes/{id}/cook?servings=6", AppFixture.JsonOptions);

        Assert.Equal(3, cook!.Ingredients[0].Quantity);
    }

    [Fact]
    public async Task A_recipe_that_never_said_its_servings_cannot_be_scaled()
    {
        // Scaling to six is meaningless without knowing what it makes now.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Mystery", null, [(1.0, "cup", "rice")]);

        var response = await client.GetAsync($"/api/recipes/{id}/cook?servings=6");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task Absurd_serving_counts_are_refused(int servings)
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var id = SeedRecipe(userId, "Pasta", 4, [(400.0, "g", "spaghetti")]);

        var response = await client.GetAsync($"/api/recipes/{id}/cook?servings={servings}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // --------------------------------------------------------- grocery list

    [Fact]
    public async Task The_same_ingredient_across_recipes_is_added_up()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var a = SeedRecipe(userId, "Dish A", 2, [(2.0, "tbsp", "olive oil"), (1.0, null, "onion")]);
        var b = SeedRecipe(userId, "Dish B", 2, [(1.0, "tbsp", "olive oil")]);

        var list = await (await client.PostAsJsonAsync("/api/grocery-list",
                new GroceryListRequest { RecipeIds = [a, b] }))
            .Content.ReadFromJsonAsync<GroceryListDto>(AppFixture.JsonOptions);

        var oil = list!.Items.Single(i => i.Item == "olive oil");
        Assert.Equal(3, oil.Quantity);
        Assert.Equal("tbsp", oil.Unit);
        Assert.Equal(2, oil.Sources.Count);
    }

    [Fact]
    public async Task Compatible_units_are_converted_before_adding()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var a = SeedRecipe(userId, "Dish A", 2, [(1.0, "kg", "flour")]);
        var b = SeedRecipe(userId, "Dish B", 2, [(500.0, "g", "flour")]);

        var list = await (await client.PostAsJsonAsync("/api/grocery-list",
                new GroceryListRequest { RecipeIds = [a, b] }))
            .Content.ReadFromJsonAsync<GroceryListDto>(AppFixture.JsonOptions);

        var flour = list!.Items.Single();
        // Reported in the larger unit: 1.5 kg reads better than 1500 g.
        Assert.Equal(1.5, flour.Quantity);
        Assert.Equal("kg", flour.Unit);
    }

    [Fact]
    public async Task Amounts_that_cannot_honestly_be_added_stay_separate()
    {
        // Mass and volume of the same ingredient need a density table nobody has. One
        // line with both sources beats one confidently wrong number.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var a = SeedRecipe(userId, "Dish A", 2, [(100.0, "g", "butter")]);
        var b = SeedRecipe(userId, "Dish B", 2, [(2.0, "tbsp", "butter")]);

        var list = await (await client.PostAsJsonAsync("/api/grocery-list",
                new GroceryListRequest { RecipeIds = [a, b] }))
            .Content.ReadFromJsonAsync<GroceryListDto>(AppFixture.JsonOptions);

        var butter = list!.Items.Single();
        Assert.Null(butter.Quantity);
        Assert.Equal(2, butter.Sources.Count);
        Assert.Contains(butter.Sources, s => s.Unit == "g");
        Assert.Contains(butter.Sources, s => s.Unit == "tbsp");
    }

    [Fact]
    public async Task Ingredient_names_are_matched_case_and_punctuation_insensitively()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var a = SeedRecipe(userId, "Dish A", 2, [(1.0, "tsp", "Garlic Powder")]);
        var b = SeedRecipe(userId, "Dish B", 2, [(2.0, "tsp", "garlic powder")]);

        var list = await (await client.PostAsJsonAsync("/api/grocery-list",
                new GroceryListRequest { RecipeIds = [a, b] }))
            .Content.ReadFromJsonAsync<GroceryListDto>(AppFixture.JsonOptions);

        Assert.Equal(3, list!.Items.Single().Quantity);
    }

    [Fact]
    public async Task Another_users_recipes_are_not_shoppable()
    {
        var mine = Guid.NewGuid().ToString();
        var theirs = SeedRecipe(Guid.NewGuid().ToString(), "Secret", 2, [(1.0, "cup", "rice")]);

        var response = await ClientFor(mine).PostAsJsonAsync("/api/grocery-list",
            new GroceryListRequest { RecipeIds = [theirs] });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
