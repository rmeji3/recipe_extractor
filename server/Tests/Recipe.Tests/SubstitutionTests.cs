using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Models.Substitution;
using Recipe.Api.Services.Substitution;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Tests;

/// <summary>
/// Stands in for the model's selection step.
/// </summary>
/// <remarks>
/// Set <see cref="Respond"/> to whatever the model might return — including things it was
/// told not to return. That is the point: the guarantee under test is that an invented
/// ingredient never reaches the user, and the only way to prove it is to have the stub
/// invent one.
/// </remarks>
public class StubModifier : IModificationClient
{
    public Func<ModificationPrompt, ModificationSelection>? Respond { get; set; }
    public ModificationPrompt? LastPrompt { get; private set; }

    public Task<ModificationSelection> SelectAsync(
        ModificationPrompt prompt, CancellationToken cancellationToken = default)
    {
        LastPrompt = prompt;

        return Task.FromResult(
            Respond?.Invoke(prompt) ?? new ModificationSelection([], "No changes."));
    }
}

public class SubstitutionTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubModifier Modifier => fixture.Services.GetRequiredService<StubModifier>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    /// <summary>Writes a recipe straight to the database — no extraction involved.</summary>
    private Guid SeedRecipe(string userId, params (double? Qty, string Unit, string Item)[] items)
    {
        using var db = fixture.CreateDbContext();

        var post = new Recipe.Api.Models.Import.SavedPost
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ImportJobId = SeedJob(db, userId),
            Platform = Recipe.Api.Models.Import.SourcePlatform.Instagram,
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
            Title = "Seeded Dish",
            Servings = 4,
            Ingredients = [.. items.Select(i =>
                new RecipeIngredient(null, i.Qty, i.Unit, i.Item, null, 0.9, null))],
            Steps = [new RecipeStep("Cook it.", null, null)],
            SearchText = "Seeded Dish",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.Recipes.Add(recipe);
        db.SaveChanges();

        return recipe.Id;
    }

    private static Guid SeedJob(Recipe.Api.Data.App.AppDbContext db, string userId)
    {
        var job = new Recipe.Api.Models.Import.ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Platform = Recipe.Api.Models.Import.SourcePlatform.Instagram,
            CreatedAt = DateTime.UtcNow
        };
        db.ImportJobs.Add(job);
        return job.Id;
    }

    private static Task<HttpResponseMessage> Modify(HttpClient client, Guid recipeId, string goal) =>
        client.PostAsJsonAsync($"/api/recipes/{recipeId}/modify", new ModifyRequest { Goal = goal });

    // ------------------------------------------------------------- grounding

    [Fact]
    public async Task An_invented_ingredient_never_reaches_the_user()
    {
        // The guarantee the whole design exists for. A model told to choose from a list
        // will sometimes not, and a wrong substitution ruins dinner.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "unicorn tears")], "Trust me.");

        var response = await Modify(client, recipe, "make it vegan");
        var proposal = await response.Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        Assert.Empty(proposal!.Changes);
        Assert.Contains(proposal.Warnings, w => w.Contains("unicorn tears"));
    }

    [Fact]
    public async Task A_change_to_an_ingredient_that_is_not_in_the_recipe_is_discarded()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("caviar", "olive oil")], "");

        var proposal = await (await Modify(client, recipe, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        Assert.Empty(proposal!.Changes);
        Assert.Single(proposal.Warnings);
    }

    [Fact]
    public async Task The_model_is_only_ever_offered_vetted_options()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (200, "g", "butter"), (2, "cups", "white flour"));

        await Modify(client, recipe, "make it vegan");

        var prompt = Modifier.LastPrompt!;
        Assert.All(prompt.Candidates, c => Assert.NotEmpty(c.Options));
        // Vegan was asked for, so every option offered must actually achieve it.
        Assert.All(prompt.Candidates,
            c => Assert.All(c.Options, o => Assert.Contains("vegan", o.Tags)));
    }

    [Fact]
    public async Task A_recipe_with_nothing_substitutable_says_so_instead_of_inventing()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (1, "pinch", "saffron"));

        var proposal = await (await Modify(client, recipe, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        Assert.Null(proposal!.Id);
        Assert.Empty(proposal.Changes);
        Assert.Contains("left alone", proposal.Summary);
    }

    // ------------------------------------------------------------- the rules

    [Fact]
    public async Task The_ratio_comes_from_the_rule_not_from_the_model()
    {
        // Butter is about 15% water and oil is not, so the swap is three quarters. This
        // number decides whether the dish works, and the model never supplies it.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "Swapped.");

        var proposal = await (await Modify(client, recipe, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        var change = Assert.Single(proposal!.Changes);
        Assert.Equal(150, change.Quantity);
        Assert.Equal("g", change.Unit);
    }

    [Fact]
    public async Task Every_change_carries_the_rule_it_came_from_and_its_knock_on_effect()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "");

        var proposal = await (await Modify(client, recipe, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        var change = Assert.Single(proposal!.Changes);
        Assert.NotEqual(Guid.Empty, change.RuleId);
        // The warning about browning is written by a person, not improvised per request.
        Assert.Contains("browning", change.Effect);
    }

    [Fact]
    public async Task A_messy_ingredient_line_still_finds_its_rule()
    {
        // Real extraction produces "boneless, skinless chicken thighs, cut into pieces".
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipe = SeedRecipe(userId, (1.5, "lbs", "boneless skinless chicken thighs"));

        await Modify(client, recipe, "vegetarian");

        var candidate = Assert.Single(Modifier.LastPrompt!.Candidates);
        Assert.Contains(candidate.Options, o => o.Replacement == "extra-firm tofu");
    }

    // ----------------------------------------------------------- the profile

    [Fact]
    public async Task An_avoided_ingredient_is_never_offered_whatever_the_goal()
    {
        // Allergies are a filter, not a hint. A model merely told about one will eventually
        // ignore it.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        await client.PutAsJsonAsync("/api/profile", new UpdateProfileRequest
        {
            Diet = DietaryPattern.None,
            Avoid = ["olive oil"]
        });

        var recipe = SeedRecipe(userId, (200, "g", "butter"));
        await Modify(client, recipe, "make it vegan");

        var candidate = Assert.Single(Modifier.LastPrompt!.Candidates);
        Assert.DoesNotContain(candidate.Options, o => o.Replacement == "olive oil");
    }

    [Fact]
    public async Task A_standing_diet_applies_even_when_the_goal_never_mentions_it()
    {
        // Someone vegan asking to "make it healthier" still wants a vegan result.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        await client.PutAsJsonAsync("/api/profile", new UpdateProfileRequest
        {
            Diet = DietaryPattern.Vegan
        });

        var recipe = SeedRecipe(userId, (200, "g", "butter"));
        await Modify(client, recipe, "make it healthier");

        var candidate = Assert.Single(Modifier.LastPrompt!.Candidates);
        Assert.All(candidate.Options,
            o => Assert.True(o.Tags.Contains("vegan") || o.Tags.Contains("lower-calorie")));
    }

    [Fact]
    public async Task The_profile_round_trips()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        await client.PutAsJsonAsync("/api/profile", new UpdateProfileRequest
        {
            Diet = DietaryPattern.Vegetarian,
            Avoid = ["Mushrooms", "mushrooms", " "],
            Goals = ["higher-protein"],
            Notes = "Cooking for two."
        });

        var profile = await client.GetFromJsonAsync<UserProfileDto>("/api/profile", AppFixture.JsonOptions);

        Assert.Equal(DietaryPattern.Vegetarian, profile!.Diet);
        // Normalised and de-duplicated on the way in.
        Assert.Equal(["mushrooms"], profile.Avoid);
        Assert.Equal("Cooking for two.", profile.Notes);
    }

    [Fact]
    public async Task A_user_with_no_profile_gets_defaults_rather_than_a_404()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        var profile = await client.GetFromJsonAsync<UserProfileDto>("/api/profile", AppFixture.JsonOptions);

        Assert.Equal(DietaryPattern.None, profile!.Diet);
        Assert.Empty(profile.Avoid);
    }

    // ------------------------------------------------------ corpus grounding

    [Fact]
    public async Task Options_the_user_already_cooks_with_are_marked_and_ranked_first()
    {
        // The half no competitor can copy: it needs an imported library to exist.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        SeedRecipe(userId, (1, "cup", "greek yoghurt"));
        var recipe = SeedRecipe(userId, (200, "g", "butter"));

        await Modify(client, recipe, "healthier");

        var candidate = Assert.Single(Modifier.LastPrompt!.Candidates);
        Assert.True(candidate.Options[0].InCorpus);
        Assert.Equal("greek yoghurt", candidate.Options[0].Replacement);
    }

    // ------------------------------------------------------------- accepting

    [Fact]
    public async Task Accepting_creates_a_new_recipe_and_leaves_the_original_alone()
    {
        // The original came from a real video. Overwriting it would destroy the thing the
        // substitution was derived from.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "Now vegan.");

        var proposal = await (await Modify(client, recipeId, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        var accepted = await client.PostAsync(
            $"/api/recipes/modifications/{proposal!.Id}/accept", null);
        var modified = await accepted.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.NotEqual(recipeId, modified!.Id);
        Assert.Equal("olive oil", Assert.Single(modified.Ingredients).Item);
        Assert.Equal(150, modified.Ingredients[0].Quantity);

        var original = await client.GetFromJsonAsync<RecipeDto>(
            $"/api/recipes/{recipeId}", AppFixture.JsonOptions);
        Assert.Equal("butter", Assert.Single(original!.Ingredients).Item);
    }

    [Fact]
    public async Task A_variant_keeps_the_dishs_name_and_carries_a_label()
    {
        // Appending the goal to every title made a search for one dish read as several.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "");

        var proposal = await (await Modify(client, recipeId, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);
        var variant = await (await client.PostAsync($"/api/recipes/modifications/{proposal!.Id}/accept", null))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal("Seeded Dish", variant!.Title);
        Assert.Equal("vegan", variant.VariantLabel);
        Assert.Equal(recipeId, variant.DerivedFromRecipeId);
    }

    [Fact]
    public async Task A_dish_and_its_variants_are_one_search_result_with_tabs()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "");

        foreach (var goal in (string[])["make it vegan", "make it healthier"])
        {
            var proposal = await (await Modify(client, recipeId, goal))
                .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);
            await client.PostAsync($"/api/recipes/modifications/{proposal!.Id}/accept", null);
        }

        var page = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes?q=Seeded", AppFixture.JsonOptions);

        // One dish, not three rows.
        var dish = Assert.Single(page!.Items);
        Assert.Equal(recipeId, dish.Id);
        Assert.Equal(["vegan", "healthier"], dish.Variants.Select(v => v.Label));
    }

    [Fact]
    public async Task Searching_for_a_variants_ingredient_surfaces_the_dish()
    {
        // "Vegetarian butter chicken" is still butter chicken. A match on the variant has
        // to lead the user to the dish, with the variant offered as a tab.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "");

        var proposal = await (await Modify(client, recipeId, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);
        await client.PostAsync($"/api/recipes/modifications/{proposal!.Id}/accept", null);

        // "olive oil" only exists on the variant.
        var page = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes?q=olive", AppFixture.JsonOptions);

        var dish = Assert.Single(page!.Items);
        Assert.Equal(recipeId, dish.Id);
        Assert.Single(dish.Variants);
    }

    [Fact]
    public async Task Accepting_twice_returns_the_same_recipe_rather_than_making_another()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        Modifier.Respond = _ => new ModificationSelection(
            [new ProposedChange("butter", "olive oil")], "");

        var proposal = await (await Modify(client, recipeId, "make it vegan"))
            .Content.ReadFromJsonAsync<ModificationDto>(AppFixture.JsonOptions);

        var first = await (await client.PostAsync($"/api/recipes/modifications/{proposal!.Id}/accept", null))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);
        var second = await (await client.PostAsync($"/api/recipes/modifications/{proposal.Id}/accept", null))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(first!.Id, second!.Id);
    }

    [Fact]
    public async Task Another_users_recipe_cannot_be_modified()
    {
        var userId = Guid.NewGuid().ToString();
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        var response = await Modify(ClientFor(Guid.NewGuid().ToString()), recipeId, "vegan");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_goal_is_rejected()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        var recipeId = SeedRecipe(userId, (200, "g", "butter"));

        var response = await Modify(client, recipeId, "   ");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
