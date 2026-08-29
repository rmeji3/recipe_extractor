using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Metadata;

namespace Recipe.Tests;

/// <summary>Stands in for following a share-sheet short link.</summary>
public class StubShortLinkResolver : IShortLinkResolver
{
    public Dictionary<string, string> Map { get; } = [];

    public Task<string> ResolveAsync(string url, CancellationToken cancellationToken = default) =>
        Task.FromResult(Map.TryGetValue(url, out var resolved) ? resolved : url);
}

/// <summary>
/// The single-video path: paste or share one link, get a recipe. This is the app's primary
/// flow — bulk import runs behind it.
/// </summary>
public class ShareSheetTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubSidecar Sidecar => fixture.Services.GetRequiredService<StubSidecar>();
    private StubOEmbed OEmbed => fixture.Services.GetRequiredService<StubOEmbed>();
    private StubShortLinkResolver ShortLinks => fixture.Services.GetRequiredService<StubShortLinkResolver>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private void HappyPath()
    {
        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        Sidecar.Throws = null;
        Sidecar.Next = (_, _) => StubSidecar.Narrated();
    }

    private static Task<HttpResponseMessage> Share(HttpClient client, string url) =>
        client.PostAsJsonAsync("/api/recipes/from-url", new ExtractFromUrlRequest { Url = url });

    [Fact]
    public async Task A_pasted_tiktok_link_becomes_a_recipe()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();

        var response = await Share(client, "https://www.tiktok.com/@cj.eats/video/7000000000000000001");
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
        Assert.Equal(3, recipe.Ingredients.Count);
    }

    [Theory]
    [InlineData("https://www.tiktokv.com/share/video/7000000000000000002/")]
    [InlineData("https://www.tiktok.com/video/7000000000000000002")]
    [InlineData("https://www.tiktok.com/@someone/video/7000000000000000002?is_from_webapp=1")]
    public async Task Every_tiktok_link_shape_resolves_to_the_same_post(string url)
    {
        // The same video arrives as an export share link, an oEmbed-style id link, and a
        // creator link with tracking params. Parsing them inconsistently would split the
        // cross-user cache.
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();

        var response = await Share(client, url);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(recipe);

        using var db = fixture.CreateDbContext();
        var post = db.SavedPosts.Single(p => p.Id == recipe!.SavedPostId);
        Assert.Equal("7000000000000000002", post.PlatformItemId);
    }

    [Fact]
    public async Task A_share_sheet_short_link_is_followed_first()
    {
        // vm.tiktok.com links carry no video id at all, and the share sheet emits them.
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();
        ShortLinks.Map["https://vm.tiktok.com/ZMabc123/"] =
            "https://www.tiktok.com/@cj.eats/video/7000000000000000003";

        var response = await Share(client, "https://vm.tiktok.com/ZMabc123/");
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
    }

    [Fact]
    public async Task An_instagram_reel_link_works_without_any_metadata_fetch()
    {
        // Instagram exports and links carry captions already; there is nothing to fetch.
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();
        OEmbed.Requested.Clear();

        var response = await Share(client, "https://www.instagram.com/reel/AbCdEfGh1/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(OEmbed.Requested);
        Assert.Equal("https://www.instagram.com/reel/AbCdEfGh1/", Sidecar.LastUrl);
    }

    [Fact]
    public async Task A_second_user_sharing_the_same_video_pays_nothing()
    {
        // The cross-user cache: viral recipes are extracted once and reused. This is both
        // the cost story and, on the share path, the speed story.
        const string url = "https://www.tiktok.com/@cj.eats/video/7000000000000000004";
        var first = ClientFor(Guid.NewGuid().ToString());
        var second = ClientFor(Guid.NewGuid().ToString());
        HappyPath();

        await Share(first, url);

        Sidecar.Next = null;
        Sidecar.Throws = new Exception("the sidecar must not be called on a cache hit");
        OEmbed.Respond = null;
        OEmbed.Throw = _ => new Exception("oEmbed must not be called on a cache hit");

        var response = await Share(second, url);
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
        Assert.Equal(3, recipe.Ingredients.Count);
    }

    [Fact]
    public async Task A_cache_hit_gives_the_second_user_their_own_editable_row()
    {
        // Recipes are per-user because every field is user-editable — two people cannot
        // share a row.
        const string url = "https://www.tiktok.com/@cj.eats/video/7000000000000000005";
        var firstId = Guid.NewGuid().ToString();
        var secondId = Guid.NewGuid().ToString();
        HappyPath();

        var a = await (await Share(ClientFor(firstId), url))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);
        var b = await (await Share(ClientFor(secondId), url))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.NotEqual(a!.Id, b!.Id);
        Assert.NotEqual(a.SavedPostId, b.SavedPostId);
        Assert.Equal(a.Title, b.Title);
    }

    [Fact]
    public async Task Sharing_the_same_link_twice_does_not_duplicate_the_post()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        const string url = "https://www.tiktok.com/@cj.eats/video/7000000000000000006";
        HappyPath();

        var a = await (await Share(client, url)).Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);
        var b = await (await Share(client, url)).Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(a!.SavedPostId, b!.SavedPostId);
    }

    [Fact]
    public async Task Stage_1_runs_automatically_so_the_user_never_sees_the_handle_problem()
    {
        // On the bulk path a missing creator handle is a 400 the caller must resolve. On
        // the share path the user pasted a link and expects a recipe, so it is handled.
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();

        var response = await Share(client, "https://www.tiktokv.com/share/video/7000000000000000007/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("https://www.tiktok.com/@cj.eats/video/7000000000000000007", Sidecar.LastUrl);
    }

    [Fact]
    public async Task A_silent_video_reports_needing_vision_rather_than_failing()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();
        Sidecar.Next = (_, _) => StubSidecar.Silent();

        var response = await Share(client, "https://www.tiktok.com/@cj.eats/video/7000000000000000008");
        var recipe = await response.Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ExtractionStatus.NeedsVision, recipe!.Status);
    }

    [Theory]
    [InlineData("https://example.com/not-a-post")]
    [InlineData("https://www.youtube.com/watch?v=abc123")]
    [InlineData("just some text")]
    public async Task An_unrecognised_link_is_rejected_with_a_readable_message(string url)
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        HappyPath();

        var response = await Share(client, url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_shared_post_joins_the_users_cookbook()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        HappyPath();

        await Share(client, "https://www.tiktok.com/@cj.eats/video/7000000000000000009");

        var page = await client.GetFromJsonAsync<PaginatedResult<RecipeSummaryDto>>(
            "/api/recipes", AppFixture.JsonOptions);

        Assert.Contains(page!.Items, r => r.Status == ExtractionStatus.Extracted);
    }

    [Fact]
    public async Task Sharing_a_post_already_held_from_a_bulk_import_reuses_it()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);
        HappyPath();

        var imported = await client.PostAsJsonAsync("/api/import", new Recipe.Api.Dtos.Import.CreateImportRequest
        {
            Platform = SourcePlatform.TikTok,
            Posts =
            [
                new Recipe.Api.Dtos.Import.ImportPostDto
                {
                    PlatformItemId = "7000000000000000010",
                    Url = "https://www.tiktokv.com/share/video/7000000000000000010/",
                    Kind = SavedPostKind.Video
                }
            ]
        });
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);

        var recipe = await (await Share(client, "https://www.tiktok.com/@cj.eats/video/7000000000000000010"))
            .Content.ReadFromJsonAsync<RecipeDto>(AppFixture.JsonOptions);

        using var db = fixture.CreateDbContext();
        var posts = db.SavedPosts.Count(p => p.UserId == userId && p.PlatformItemId == "7000000000000000010");

        Assert.Equal(1, posts);
        Assert.Equal(ExtractionStatus.Extracted, recipe!.Status);
    }
}
