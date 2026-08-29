using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Extraction;

namespace Recipe.Tests;

/// <summary>
/// Stands in for the Python sidecar. Tests set <see cref="Next"/> to the outcome they want
/// so the whole cascade can be exercised without media, Whisper, or a model call.
/// </summary>
public class StubSidecar : ISidecarClient
{
    public Func<string, string?, SidecarResult>? Next { get; set; }
    public Exception? Throws { get; set; }
    public string? LastUrl { get; private set; }
    public string? LastCaption { get; private set; }

    public Task<SidecarResult> TranscribeAsync(string url, string? caption, CancellationToken cancellationToken = default)
    {
        LastUrl = url;
        LastCaption = caption;

        if (Throws is not null)
        {
            throw Throws;
        }

        return Task.FromResult(Next!(url, caption));
    }

    public static SidecarResult Narrated(string title = "Korean Fried Chicken") => new(
        SourceId: "123",
        SecondsElapsed: 9.4,
        Transcript: new SidecarTranscript("Season the chicken and fry it twice.", "en", 46, IsSpeech: true),
        Recipe: new SidecarRecipe(
            IsRecipe: true,
            Title: title,
            Servings: 4,
            PrepMinutes: 10,
            CookMinutes: 15,
            Ingredients:
            [
                new SidecarIngredient(2, "tbsp", "soy sauce", "low sodium", 0.9, 12.5),
                new SidecarIngredient(null, null, "chicken tenders", null, 0.85, 9.1),
                new SidecarIngredient(1, "tsp", "garlic powder", null, 0.8, 13.3)
            ],
            Steps:
            [
                new SidecarStep("Season the chicken tenders.", 9.1, 13.3),
                new SidecarStep("Fry twice until golden.", 32.0, 37.9)
            ],
            Equipment: ["deep fryer"],
            FoodConfidence: 0.98),
        Note: null);

    /// <summary>Nothing usable came back — neither narration nor frames yielded a recipe.</summary>
    public static SidecarResult Silent() => new(
        SourceId: "123",
        SecondsElapsed: 3.1,
        Transcript: new SidecarTranscript("", "en", 40, IsSpeech: false),
        Recipe: null,
        Note: "Neither the narration nor the frames carried a full recipe.",
        Path: "none");

    /// <summary>
    /// A silent video the sidecar recovered by reading frames. Note the absent quantities:
    /// on-screen text carries ingredients and method, never amounts.
    /// </summary>
    public static SidecarResult FromFrames() => new(
        SourceId: "123",
        SecondsElapsed: 26.7,
        Transcript: new SidecarTranscript("", "en", 45, IsSpeech: false),
        Recipe: new SidecarRecipe(
            IsRecipe: true,
            Title: "One-Pot Halal Cart Chicken Over Rice",
            Servings: null,
            PrepMinutes: null,
            CookMinutes: null,
            Ingredients:
            [
                new SidecarIngredient(null, null, "chicken", null, 0.8, null),
                new SidecarIngredient(null, null, "rice", null, 0.85, null),
                new SidecarIngredient(null, null, "cumin", null, 0.95, null)
            ],
            Steps:
            [
                new SidecarStep("Combine the spice mix.", null, null),
                new SidecarStep("Cook the chicken and rice in one pot.", null, null)
            ],
            Equipment: ["pot"],
            FoodConfidence: 0.9),
        Note: null,
        Path: "vision",
        FramesUsed: 10);
}

public class RecipeExtractionTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubSidecar Sidecar => fixture.Services.GetRequiredService<StubSidecar>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private async Task<Guid> ImportPost(HttpClient client, SourcePlatform platform,
        string itemId, string? handle, string? caption = null)
    {
        var url = platform == SourcePlatform.TikTok
            ? $"https://www.tiktokv.com/share/video/{itemId}/"
            : $"https://www.instagram.com/p/{itemId}/";

        var created = await client.PostAsJsonAsync("/api/import", new CreateImportRequest
        {
            Platform = platform,
            Posts =
            [
                new ImportPostDto
                {
                    PlatformItemId = itemId,
                    Url = url,
                    Kind = platform == SourcePlatform.TikTok ? SavedPostKind.Video : SavedPostKind.Post,
                    CreatorHandle = handle,
                    Captions = caption is null ? null : [caption]
                }
            ]
        });

        var summary = await created.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);
        var posts = await client.GetFromJsonAsync<PaginatedResult<SavedPostDto>>(
            $"/api/import/{summary!.Id}/posts", AppFixture.JsonOptions);

        return posts!.Items[0].Id;
    }

    [Fact]
    public async Task Extract_stores_the_structured_recipe()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e1", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
        Assert.Equal(3, recipe.Ingredients.Count);
        Assert.Equal(2, recipe.Steps.Count);
        Assert.Equal(4, recipe.Servings);
    }

    [Fact]
    public async Task Extract_keeps_timestamps_from_the_transcript()
    {
        // Free to capture while the media is already being processed, and what v3 needs.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e2", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(9.1, recipe!.Steps[0].TsStart);
        Assert.Equal(12.5, recipe.Ingredients[0].SourceTs);
    }

    [Fact]
    public async Task Extract_builds_the_canonical_tiktok_url_yt_dlp_needs()
    {
        // yt-dlp 404s on the share link and on the id-only form oEmbed accepts.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "7204147181404556586", "cj.eats");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();

        await client.PostAsync($"/api/recipes/extract/{postId}", null);

        Assert.Equal("https://www.tiktok.com/@cj.eats/video/7204147181404556586", Sidecar.LastUrl);
    }

    [Fact]
    public async Task Extract_rejects_a_tiktok_post_with_no_creator_handle()
    {
        // Straight from the export, TikTok rows have only a date and a link. Without the
        // handle the video cannot be addressed at all — stage 1 has to run first.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e3", handle: null);

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Extract_passes_the_caption_through_for_quantities()
    {
        // Narration gives method; the creator's typed caption gives amounts.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e4", "somechef", "2 tbsp soy sauce");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();

        await client.PostAsync($"/api/recipes/extract/{postId}", null);

        Assert.Equal("2 tbsp soy sauce", Sidecar.LastCaption);
    }

    [Fact]
    public async Task A_silent_video_recovered_from_frames_counts_as_extracted()
    {
        // The sidecar reads frames itself, so "no narration" no longer means "pending".
        // Keying the status off the transcript would mark every vision success as unfinished.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e11", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.FromFrames();

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
        Assert.Equal(3, recipe.Ingredients.Count);
        // Frames carry ingredients and method, never amounts.
        Assert.All(recipe.Ingredients, i => Assert.Null(i.Quantity));
    }

    [Fact]
    public async Task Silent_video_is_routed_to_vision_not_recorded_as_a_failure()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e5", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Silent();

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.NeedsVision, recipe!.Status);
        Assert.Null(recipe.FailureReason);
    }

    [Fact]
    public async Task A_dead_video_is_recorded_as_failed_rather_than_a_500()
    {
        // 38% of a 2019-onward backlog no longer resolves. That is a normal outcome.
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e6", "somechef");
        Sidecar.Next = null;
        Sidecar.Throws = new SidecarException("could not fetch media: video unavailable");

        var response = await client.PostAsync($"/api/recipes/extract/{postId}", null);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.Failed, recipe!.Status);
        Assert.Contains("unavailable", recipe.FailureReason);
    }

    [Fact]
    public async Task Re_extracting_updates_the_same_row()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.TikTok, "e7", "somechef");

        Sidecar.Next = null;
        Sidecar.Throws = new SidecarException("temporary glitch");
        var first = await (await client.PostAsync($"/api/recipes/extract/{postId}", null))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated("Second Attempt");
        var second = await (await client.PostAsync($"/api/recipes/extract/{postId}", null))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(first!.Id, second!.Id);
        Assert.Equal(ExtractionStatus.Extracted, second.Status);
        Assert.Null(second.FailureReason);
    }

    [Fact]
    public async Task List_filters_by_status_so_the_vision_backlog_is_findable()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        var narrated = await ImportPost(client, SourcePlatform.TikTok, "e8", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();
        await client.PostAsync($"/api/recipes/extract/{narrated}", null);

        var silent = await ImportPost(client, SourcePlatform.TikTok, "e9", "somechef");
        Sidecar.Next = (_, _) => StubSidecar.Silent();
        await client.PostAsync($"/api/recipes/extract/{silent}", null);

        var vision = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes?status=NeedsVision", AppFixture.JsonOptions);

        Assert.Equal(1, vision!.TotalCount);
        Assert.Equal(ExtractionStatus.NeedsVision, vision.Items[0].Status);
    }

    [Fact]
    public async Task Another_users_post_cannot_be_extracted()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        var stranger = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(owner, SourcePlatform.TikTok, "e10", "somechef");

        var response = await stranger.PostAsync($"/api/recipes/extract/{postId}", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Instagram_posts_use_their_permalink_directly()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var postId = await ImportPost(client, SourcePlatform.Instagram, "AbCd1234", "somechef");
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();

        await client.PostAsync($"/api/recipes/extract/{postId}", null);

        Assert.Equal("https://www.instagram.com/p/AbCd1234/", Sidecar.LastUrl);
    }
}
