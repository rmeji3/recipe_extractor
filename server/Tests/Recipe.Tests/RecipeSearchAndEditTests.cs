using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Extraction;
using Recipe.Api.Services.Metadata;

namespace Recipe.Tests;

/// <summary>
/// Search and manual editing — the half of the definition of done that is about finding a
/// recipe a week later, and about fixing what extraction got wrong.
/// </summary>
/// <remarks>
/// Search runs on SQLite here and Postgres in production. The two are not equivalent:
/// Postgres stems and ranks, SQLite matches substrings. These tests assert only behaviour
/// both providers share.
/// </remarks>
public class RecipeSearchAndEditTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubSidecar Sidecar => fixture.Services.GetRequiredService<StubSidecar>();
    private StubOEmbed OEmbed => fixture.Services.GetRequiredService<StubOEmbed>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private static SidecarResult WithIngredients(string title, params string[] items) => new(
        SourceId: "1",
        SecondsElapsed: 1,
        Transcript: new SidecarTranscript("spoken", "en", 30, IsSpeech: true),
        Recipe: new SidecarRecipe(
            IsRecipe: true, Title: title, Servings: 2, PrepMinutes: 5, CookMinutes: 10,
            Ingredients: [.. items.Select(i => new SidecarIngredient(1, "cup", i, null, 0.9, 1.0))],
            Steps: [new SidecarStep("Cook it.", 1, 2), new SidecarStep("Serve it.", 3, 4)],
            Equipment: ["pan"],
            FoodConfidence: 0.9),
        Note: null,
        Path: "narration");

    /// <summary>Shares a link and returns the created recipe.</summary>
    private async Task<RecipeDto> Add(HttpClient client, string itemId, SidecarResult result)
    {
        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => result;

        var response = await client.PostAsJsonAsync("/api/recipes/from-url",
            new ExtractFromUrlRequest { Url = $"https://www.tiktok.com/@chef/video/{itemId}" });

        return (await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions))!;
    }

    private static Task<PaginatedResult<RecipeSummaryDto>?> Search(HttpClient client, string term) =>
        client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            $"/api/recipes?q={Uri.EscapeDataString(term)}", AppFixture.JsonOptions);

    // ------------------------------------------------------------------ search

    [Fact]
    public async Task Search_finds_a_recipe_by_title()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        await Add(client, "9000000000000000001", WithIngredients("Garlic Butter Pasta", "butter"));
        await Add(client, "9000000000000000002", WithIngredients("Beef Stew", "beef"));

        var found = await Search(client, "pasta");

        Assert.Equal(1, found!.TotalCount);
        Assert.Equal("Garlic Butter Pasta", found.Items[0].Title);
    }

    [Fact]
    public async Task Search_finds_a_recipe_by_an_ingredient_it_contains()
    {
        // The reason a flattened SearchText column exists: ingredients live in a JSON
        // column that neither provider can index usefully.
        var client = ClientFor(Guid.NewGuid().ToString());
        await Add(client, "9000000000000000003", WithIngredients("Weeknight Noodles", "gochujang", "sesame oil"));
        await Add(client, "9000000000000000004", WithIngredients("Plain Rice", "rice"));

        var found = await Search(client, "gochujang");

        Assert.Equal(1, found!.TotalCount);
        Assert.Equal("Weeknight Noodles", found.Items[0].Title);
    }

    [Fact]
    public async Task Search_finds_a_recipe_by_creator()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        await Add(client, "9000000000000000005", WithIngredients("Some Dish", "salt"));

        // StubOEmbed reports the creator as cj.eats.
        var found = await Search(client, "cj.eats");

        Assert.Equal(1, found!.TotalCount);
    }

    [Fact]
    public async Task Search_only_returns_the_callers_recipes()
    {
        var mine = ClientFor(Guid.NewGuid().ToString());
        var theirs = ClientFor(Guid.NewGuid().ToString());
        await Add(mine, "9000000000000000006", WithIngredients("Secret Chilli", "chilli"));

        var found = await Search(theirs, "chilli");

        Assert.Equal(0, found!.TotalCount);
    }

    [Fact]
    public async Task An_empty_query_lists_everything()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        await Add(client, "9000000000000000007", WithIngredients("Dish One", "salt"));
        await Add(client, "9000000000000000008", WithIngredients("Dish Two", "pepper"));

        var all = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes", AppFixture.JsonOptions);

        Assert.Equal(2, all!.TotalCount);
    }

    [Fact]
    public async Task Search_combines_with_the_status_filter()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        await Add(client, "9000000000000000009", WithIngredients("Findable Soup", "stock"));

        var wrongStatus = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes?q=soup&status=NeedsVision", AppFixture.JsonOptions);
        var rightStatus = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes?q=soup&status=Extracted", AppFixture.JsonOptions);

        Assert.Equal(0, wrongStatus!.TotalCount);
        Assert.Equal(1, rightStatus!.TotalCount);
    }

    // ------------------------------------------------------------------- edit

    private static UpdateRecipeRequest Edit(string title, params (double? Qty, string Item)[] items) => new()
    {
        Title = title,
        Servings = 4,
        PrepMinutes = 15,
        CookMinutes = 25,
        Ingredients = [.. items.Select(i => new UpdateIngredient
        {
            Quantity = i.Qty, Unit = i.Qty is null ? null : "tbsp", Item = i.Item
        })],
        Steps = [new UpdateStep { Text = "Do the first thing." }],
        Equipment = ["wok"]
    };

    [Fact]
    public async Task Update_replaces_the_editable_fields()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(client, "9100000000000000001", WithIngredients("Wrong Title", "wrong"));

        var response = await client.PutAsJsonAsync($"/api/recipes/{recipe.Id}",
            Edit("Correct Title", (2, "soy sauce"), (1, "sesame oil")));
        var updated = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Correct Title", updated!.Title);
        Assert.Equal(2, updated.Ingredients.Count);
        Assert.Equal(4, updated.Servings);
        Assert.Equal("wok", Assert.Single(updated.Equipment));
    }

    [Fact]
    public async Task An_edited_ingredient_is_recorded_at_full_confidence()
    {
        // Extraction guesses; a person does not. The UI shows low confidence as a warning,
        // so a corrected value must stop being flagged.
        var client = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(client, "9100000000000000002", WithIngredients("Dish", "mystery"));

        var updated = await (await client.PutAsJsonAsync($"/api/recipes/{recipe.Id}",
            Edit("Dish", (2, "soy sauce")))).Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(1.0, Assert.Single(updated!.Ingredients).Confidence);
    }

    [Fact]
    public async Task Editing_marks_the_recipe_and_clears_a_vision_status()
    {
        // A user who typed the recipe out has resolved it; it is no longer pending work.
        var client = ClientFor(Guid.NewGuid().ToString());
        Sidecar.Throws = null;
        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        Sidecar.Next = (_, _) => StubSidecar.Silent();

        var created = await (await client.PostAsJsonAsync("/api/recipes/from-url",
                new ExtractFromUrlRequest { Url = "https://www.tiktok.com/@chef/video/9100000000000000003" }))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);
        Assert.Equal(ExtractionStatus.NeedsVision, created!.Status);
        Assert.False(created.IsEdited);

        var updated = await (await client.PutAsJsonAsync($"/api/recipes/{created.Id}",
            Edit("Hand Written", (1, "flour")))).Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.True(updated!.IsEdited);
        Assert.Equal(ExtractionStatus.Extracted, updated.Status);
    }

    [Fact]
    public async Task An_edit_is_immediately_searchable()
    {
        // The search index is maintained on write, so a renamed recipe must be findable
        // under its new name and not its old one.
        var client = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(client, "9100000000000000004", WithIngredients("Old Name", "thing"));

        await client.PutAsJsonAsync($"/api/recipes/{recipe.Id}", Edit("Shakshuka", (6, "eggs")));

        Assert.Equal(1, (await Search(client, "Shakshuka"))!.TotalCount);
        Assert.Equal(1, (await Search(client, "eggs"))!.TotalCount);
        Assert.Equal(0, (await Search(client, "Old Name"))!.TotalCount);
    }

    [Fact]
    public async Task Update_keeps_timestamps_the_user_passes_back()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(client, "9100000000000000005", WithIngredients("Dish", "thing"));

        var request = new UpdateRecipeRequest
        {
            Title = "Dish",
            Ingredients = [new UpdateIngredient { Item = "soy sauce", Quantity = 2, SourceTs = 12.5 }],
            Steps = [new UpdateStep { Text = "Season.", TsStart = 9.1, TsEnd = 13.3 },
                     new UpdateStep { Text = "Fry.", TsStart = 32, TsEnd = 37.9 }]
        };

        var updated = await (await client.PutAsJsonAsync($"/api/recipes/{recipe.Id}", request))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(12.5, updated!.Ingredients[0].SourceTs);
        Assert.Equal(9.1, updated.Steps[0].TsStart);
    }

    [Fact]
    public async Task Update_rejects_an_empty_title()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(client, "9100000000000000006", WithIngredients("Dish", "thing"));

        var response = await client.PutAsJsonAsync($"/api/recipes/{recipe.Id}",
            new UpdateRecipeRequest { Title = "" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Another_users_recipe_cannot_be_edited()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        var stranger = ClientFor(Guid.NewGuid().ToString());
        var recipe = await Add(owner, "9100000000000000007", WithIngredients("Mine", "thing"));

        var response = await stranger.PutAsJsonAsync($"/api/recipes/{recipe.Id}", Edit("Theirs", (1, "x")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
