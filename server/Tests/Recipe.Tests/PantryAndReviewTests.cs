using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Dtos.Cooking;
using Recipe.Api.Dtos.Pantry;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Queue;
using Recipe.Api.Services.Substitution;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Tests;

/// <summary>
/// The pantry — cooking from what is already in the house — and the review pile that lets
/// the uncertain classification tier actually go somewhere.
/// </summary>
public class PantryAndReviewTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubJobQueue Queue => fixture.Services.GetRequiredService<StubJobQueue>();
    private StubModifier Modifier => fixture.Services.GetRequiredService<StubModifier>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private Guid SeedRecipe(string userId, string title, params (double? Qty, string? Unit, string Item)[] items)
    {
        using var db = fixture.CreateDbContext();

        var job = new ImportJob
        {
            Id = Guid.NewGuid(), UserId = userId,
            Platform = SourcePlatform.Instagram, CreatedAt = DateTime.UtcNow
        };
        db.ImportJobs.Add(job);

        var post = new SavedPost
        {
            Id = Guid.NewGuid(), UserId = userId, ImportJobId = job.Id,
            Platform = SourcePlatform.Instagram,
            PlatformItemId = Guid.NewGuid().ToString("N")[..12],
            Url = "https://www.instagram.com/p/seed/", CreatedAt = DateTime.UtcNow
        };
        db.SavedPosts.Add(post);

        var recipe = new RecipeEntity
        {
            Id = Guid.NewGuid(), UserId = userId, SavedPostId = post.Id,
            Status = ExtractionStatus.Extracted, Title = title, Servings = 4,
            Ingredients = [.. items.Select(i =>
                new RecipeIngredient(null, i.Qty, i.Unit, i.Item, null, 0.9, null))],
            Steps = [new RecipeStep("Cook it.", null, null)],
            SearchText = title, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();

        return recipe.Id;
    }

    /// <summary>Puts a post into the uncertain tier, as classification would.</summary>
    private Guid SeedUncertain(string userId, string caption, double confidence)
    {
        using var db = fixture.CreateDbContext();

        var job = new ImportJob
        {
            Id = Guid.NewGuid(), UserId = userId,
            Platform = SourcePlatform.TikTok, CreatedAt = DateTime.UtcNow
        };
        db.ImportJobs.Add(job);

        var post = new SavedPost
        {
            Id = Guid.NewGuid(), UserId = userId, ImportJobId = job.Id,
            Platform = SourcePlatform.TikTok,
            PlatformItemId = Guid.NewGuid().ToString("N")[..12],
            Url = "https://www.tiktok.com/@chef/video/1",
            Caption = caption,
            CreatorHandle = "chef",
            ClassificationStatus = ClassificationStatus.Uncertain,
            FoodConfidence = confidence,
            ClassifiedBy = "model",
            MetadataStatus = MetadataStatus.Fetched,
            CreatedAt = DateTime.UtcNow
        };
        db.SavedPosts.Add(post);
        db.SaveChanges();

        return post.Id;
    }

    // --------------------------------------------------------------- pantry

    [Fact]
    public async Task Staples_can_be_recorded_by_hand()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "olive oil" }, new AddPantryItem { Item = "salt" }]
        });

        var pantry = await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);

        Assert.Equal(2, pantry!.Count);
        Assert.All(pantry, p => Assert.True(p.AddedByUser));
    }

    [Fact]
    public async Task Recording_the_same_ingredient_again_reinforces_it()
    {
        // Familiarity, not stock: cooking with something twice is more evidence, not a
        // second jar.
        var client = ClientFor(Guid.NewGuid().ToString());

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "flour" }]
        });
        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "Flour" }]
        });

        var pantry = await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);

        var flour = Assert.Single(pantry!);
        Assert.Equal(2, flour.TimesUsed);
    }

    [Fact]
    public async Task An_item_can_be_removed()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "saffron" }]
        });
        var pantry = await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);

        var response = await client.DeleteAsync($"/api/pantry/{pantry!.Single().Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions) ?? []);
    }

    [Fact]
    public async Task Another_users_pantry_item_cannot_be_removed()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        await owner.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "truffle" }]
        });
        var mine = await owner.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);

        var response = await ClientFor(Guid.NewGuid().ToString())
            .DeleteAsync($"/api/pantry/{mine!.Single().Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------- cookability

    [Fact]
    public async Task Recipes_are_ranked_by_how_little_is_unfamiliar()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        SeedRecipe(userId, "Nearly there", (1, "cup", "rice"), (2, "tbsp", "soy sauce"));
        SeedRecipe(userId, "Big shop", (1, null, "duck"), (1, null, "hoisin"), (1, null, "pancakes"));

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "rice" }, new AddPantryItem { Item = "soy sauce" }]
        });

        var cookable = await client.GetFromJsonAsync<List<CookabilityDto>>(
            "/api/pantry/familiar", AppFixture.JsonOptions);

        Assert.Equal("Nearly there", cookable![0].Title);
        Assert.Equal(1.0, cookable[0].Coverage);
        Assert.Empty(cookable[0].MissingItems);
    }

    [Fact]
    public async Task Unfamiliar_ingredients_are_named_so_a_shopping_trip_can_be_judged()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        SeedRecipe(userId, "Stir fry", (1, "cup", "rice"), (2, "tbsp", "soy sauce"), (1, null, "ginger"));

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "rice" }]
        });

        var cookable = await client.GetFromJsonAsync<List<CookabilityDto>>(
            "/api/pantry/familiar", AppFixture.JsonOptions);

        var dish = Assert.Single(cookable!);
        Assert.Equal(1, dish.Have);
        Assert.Equal(2, dish.Missing);
        Assert.Contains("ginger", dish.MissingItems);
    }

    [Fact]
    public async Task A_loosely_worded_ingredient_still_counts_as_familiar()
    {
        // A recipe says "boneless chicken thighs" and the pantry says "chicken". Sending
        // someone shopping for what is already in the fridge is the worse mistake.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        SeedRecipe(userId, "Roast", (1, "kg", "boneless chicken thighs"));

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "chicken" }]
        });

        var cookable = await client.GetFromJsonAsync<List<CookabilityDto>>(
            "/api/pantry/familiar", AppFixture.JsonOptions);

        Assert.Equal(1.0, Assert.Single(cookable!).Coverage);
    }

    [Fact]
    public async Task An_empty_pantry_suggests_nothing_rather_than_everything()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        SeedRecipe(userId, "Anything", (1, "cup", "rice"));

        var cookable = await client.GetFromJsonAsync<List<CookabilityDto>>(
            "/api/pantry/familiar", AppFixture.JsonOptions);

        Assert.Empty(cookable!);
    }

    [Fact]
    public async Task A_pantry_ingredient_outranks_one_merely_in_the_library()
    {
        // Something they can use tonight beats something they have merely cooked with.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        // Greek yoghurt is in the library; vegan block butter is in the house.
        SeedRecipe(userId, "Old dish", (1, "cup", "greek yoghurt"));
        var recipeId = SeedRecipe(userId, "Bake", (200, "g", "butter"));

        await client.PostAsJsonAsync("/api/pantry", new AddPantryItemsRequest
        {
            Items = [new AddPantryItem { Item = "vegan block butter" }]
        });

        await client.PostAsJsonAsync($"/api/recipes/{recipeId}/modify",
            new ModifyRequest { Goal = "make it vegan" });

        var options = Assert.Single(Modifier.LastPrompt!.Candidates).Options;
        Assert.True(options[0].InPantry);
        Assert.Equal("vegan block butter", options[0].Replacement);
    }

    // ------------------------------------------------------- the cook loop

    [Fact]
    public async Task Cooking_a_recipe_teaches_the_pantry_its_ingredients()
    {
        // The whole point of the loop: familiarity accumulates from what someone actually
        // makes, with no separate step for them to remember.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, "Curry", (2, "tbsp", "gochujang"), (1, "cup", "rice"));

        var log = await (await client.PostAsJsonAsync($"/api/recipes/{recipeId}/cooked",
                new LogCookRequest { Rating = 5, Notes = "Needed longer." }))
            .Content.ReadFromJsonAsync<CookLogDto>(AppFixture.JsonOptions);

        Assert.Equal(["gochujang", "rice"], log!.LearnedIngredients);

        var pantry = await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);
        Assert.Equal(2, pantry!.Count);
        // Inferred from a cook, not stated by the user.
        Assert.All(pantry, p => Assert.False(p.AddedByUser));
    }

    [Fact]
    public async Task Cooking_something_twice_reinforces_rather_than_duplicates()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, "Weeknight", (1, "cup", "rice"));

        await client.PostAsJsonAsync($"/api/recipes/{recipeId}/cooked", new LogCookRequest());
        await client.PostAsJsonAsync($"/api/recipes/{recipeId}/cooked", new LogCookRequest());

        var pantry = await client.GetFromJsonAsync<List<PantryItemDto>>("/api/pantry", AppFixture.JsonOptions);

        Assert.Equal(2, Assert.Single(pantry!).TimesUsed);
    }

    [Fact]
    public async Task History_keeps_every_cook_with_its_own_note()
    {
        // One row per cook, not a counter: the second time someone makes a dish they change
        // something, and that note is the most valuable text in the app.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, "Chilli", (1, null, "beans"));

        await client.PostAsJsonAsync($"/api/recipes/{recipeId}/cooked",
            new LogCookRequest { Rating = 3, Notes = "Too hot." });
        await client.PostAsJsonAsync($"/api/recipes/{recipeId}/cooked",
            new LogCookRequest { Rating = 5, Notes = "Half the chilli. Perfect." });

        var history = await client.GetFromJsonAsync<RecipeHistoryDto>(
            $"/api/recipes/{recipeId}/history", AppFixture.JsonOptions);

        Assert.Equal(2, history!.TimesCooked);
        // Newest first — the most recent attempt is the one worth reading.
        Assert.Equal("Half the chilli. Perfect.", history.Entries[0].Notes);
        Assert.NotNull(history.LastCookedAt);
    }

    [Fact]
    public async Task A_recipe_never_cooked_reports_an_empty_history()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, "Untried", (1, null, "flour"));

        var history = await client.GetFromJsonAsync<RecipeHistoryDto>(
            $"/api/recipes/{recipeId}/history", AppFixture.JsonOptions);

        Assert.Equal(0, history!.TimesCooked);
        Assert.Null(history.LastCookedAt);
    }

    [Fact]
    public async Task Cooking_makes_later_substitutions_prefer_what_was_used()
    {
        // The loop closing: cook something, and its ingredients start winning as swaps.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        var cooked = SeedRecipe(userId, "Yoghurt bowl", (1, "cup", "greek yoghurt"));
        await client.PostAsJsonAsync($"/api/recipes/{cooked}/cooked", new LogCookRequest());

        var bake = SeedRecipe(userId, "Bake", (200, "g", "butter"));
        await client.PostAsJsonAsync($"/api/recipes/{bake}/modify",
            new ModifyRequest { Goal = "healthier" });

        var options = Assert.Single(Modifier.LastPrompt!.Candidates).Options;
        Assert.True(options[0].InPantry);
        Assert.Equal("greek yoghurt", options[0].Replacement);
    }

    [Fact]
    public async Task Another_users_recipe_cannot_be_logged_as_cooked()
    {
        var theirs = SeedRecipe(Guid.NewGuid().ToString(), "Not mine", (1, null, "flour"));

        var response = await ClientFor(Guid.NewGuid().ToString())
            .PostAsJsonAsync($"/api/recipes/{theirs}/cooked", new LogCookRequest());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---------------------------------------------------------- review pile

    [Fact]
    public async Task The_review_pile_lists_uncertain_posts_most_likely_first()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        SeedUncertain(userId, "maybe a recipe", 0.45);
        SeedUncertain(userId, "probably a recipe", 0.7);

        var pile = await client.GetFromJsonAsync<PaginatedResult<SavedPostDto>>(
            "/api/import/review", AppFixture.JsonOptions);

        Assert.Equal(2, pile!.TotalCount);
        Assert.Equal("probably a recipe", pile.Items[0].Caption);
    }

    [Fact]
    public async Task Approving_marks_it_food_and_queues_extraction()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var postId = SeedUncertain(userId, "dinner tonight", 0.6);
        Queue.Clear();

        var result = await (await client.PostAsJsonAsync("/api/import/review",
                new ReviewDecisionRequest { Approve = [postId] }))
            .Content.ReadFromJsonAsync<ReviewResultDto>(AppFixture.JsonOptions);

        Assert.Equal(1, result!.Approved);
        Assert.Equal(0, result.RemainingToReview);
        Assert.Single(Queue.Enqueued, j => j.Type == JobType.Extract && j.TargetId == postId);

        using var db = fixture.CreateDbContext();
        var post = db.SavedPosts.Single(p => p.Id == postId);
        Assert.Equal(ClassificationStatus.Food, post.ClassificationStatus);
        // A person decided, so there is nothing uncertain left about it.
        Assert.Equal(1.0, post.FoodConfidence);
        Assert.Equal("user", post.ClassifiedBy);
    }

    [Fact]
    public async Task Rejecting_marks_it_skipped_and_queues_nothing()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var postId = SeedUncertain(userId, "gym clip", 0.5);
        Queue.Clear();

        var result = await (await client.PostAsJsonAsync("/api/import/review",
                new ReviewDecisionRequest { Reject = [postId] }))
            .Content.ReadFromJsonAsync<ReviewResultDto>(AppFixture.JsonOptions);

        Assert.Equal(1, result!.Rejected);
        Assert.Empty(Queue.Enqueued);

        using var db = fixture.CreateDbContext();
        // Skipped, not deleted — that list is the safety valve that makes it safe to tune
        // classification for precision.
        Assert.Equal(ClassificationStatus.NotFood,
            db.SavedPosts.Single(p => p.Id == postId).ClassificationStatus);
    }

    [Fact]
    public async Task A_whole_pile_can_be_settled_in_one_call()
    {
        // Bulk is the point: reviewing a backlog one tap at a time is how the feature gets
        // abandoned halfway.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var keep = new[] { SeedUncertain(userId, "a", 0.6), SeedUncertain(userId, "b", 0.6) };
        var drop = new[] { SeedUncertain(userId, "c", 0.5), SeedUncertain(userId, "d", 0.5) };

        var result = await (await client.PostAsJsonAsync("/api/import/review",
                new ReviewDecisionRequest { Approve = [.. keep], Reject = [.. drop] }))
            .Content.ReadFromJsonAsync<ReviewResultDto>(AppFixture.JsonOptions);

        Assert.Equal(2, result!.Approved);
        Assert.Equal(2, result.Rejected);
        Assert.Equal(0, result.RemainingToReview);
    }

    [Fact]
    public async Task A_post_in_both_lists_is_kept()
    {
        // A wrongly kept post is a line in a cookbook; a wrongly dropped one is invisible.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var postId = SeedUncertain(userId, "ambiguous", 0.6);

        await client.PostAsJsonAsync("/api/import/review",
            new ReviewDecisionRequest { Approve = [postId], Reject = [postId] });

        using var db = fixture.CreateDbContext();
        Assert.Equal(ClassificationStatus.Food,
            db.SavedPosts.Single(p => p.Id == postId).ClassificationStatus);
    }

    [Fact]
    public async Task Another_users_post_cannot_be_reviewed()
    {
        var theirs = SeedUncertain(Guid.NewGuid().ToString(), "not mine", 0.6);
        var client = ClientFor(Guid.NewGuid().ToString());

        var result = await (await client.PostAsJsonAsync("/api/import/review",
                new ReviewDecisionRequest { Approve = [theirs] }))
            .Content.ReadFromJsonAsync<ReviewResultDto>(AppFixture.JsonOptions);

        Assert.Equal(0, result!.Approved);

        using var db = fixture.CreateDbContext();
        Assert.Equal(ClassificationStatus.Uncertain,
            db.SavedPosts.Single(p => p.Id == theirs).ClassificationStatus);
    }
}
