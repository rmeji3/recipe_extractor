using System.Net;
using System.Net.Http.Json;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;

namespace Recipe.Tests;

public class ImportEndpointTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private static CreateImportRequest InstagramBatch(params string[] itemIds) => new()
    {
        Platform = SourcePlatform.Instagram,
        Posts = [.. itemIds.Select(id => new ImportPostDto
        {
            PlatformItemId = id,
            Url = $"https://www.instagram.com/p/{id}/",
            Kind = SavedPostKind.Post,
            Captions = ["Mix the flour."]
        })]
    };

    [Fact]
    public async Task Post_stores_the_batch_and_reports_counts()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/import", InstagramBatch("aaa1", "aaa2"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal(SourcePlatform.Instagram, summary.Platform);
        Assert.Equal(2, summary.SubmittedCount);
        Assert.Equal(2, summary.ImportedCount);
        Assert.Equal(0, summary.DuplicateCount);
    }

    [Fact]
    public async Task Post_skips_items_the_user_already_imported()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        await client.PostAsJsonAsync("/api/import", InstagramBatch("dup1", "dup2"));

        var response = await client.PostAsJsonAsync("/api/import", InstagramBatch("dup2", "dup3"));
        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.SubmittedCount);
        Assert.Equal(1, summary.ImportedCount);
        Assert.Equal(1, summary.DuplicateCount);
    }

    [Fact]
    public async Task Post_collapses_duplicates_inside_one_payload()
    {
        // A duplicate within the batch is not a duplicate against the database, but it
        // must not produce two rows either — the unique index would reject the insert.
        var client = ClientFor(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/import", InstagramBatch("same", "same"));
        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        Assert.NotNull(summary);
        Assert.Equal(2, summary.SubmittedCount);
        Assert.Equal(1, summary.ImportedCount);
        Assert.Equal(1, summary.DuplicateCount);
    }

    [Fact]
    public async Task Post_deduplicates_captions_before_storing()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        var request = new CreateImportRequest
        {
            Platform = SourcePlatform.Instagram,
            Posts =
            [
                new ImportPostDto
                {
                    PlatformItemId = "carousel1",
                    Url = "https://www.instagram.com/p/carousel1/",
                    Kind = SavedPostKind.Post,
                    Captions = ["Mix the flour.", "Mix the flour."]
                }
            ]
        };

        var created = await client.PostAsJsonAsync("/api/import", request);
        var summary = await created.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        var posts = await client.GetFromJsonAsync<PaginatedResult<SavedPostDto>>(
            $"/api/import/{summary!.Id}/posts", AppFixture.JsonOptions);

        Assert.Equal("Mix the flour.", Assert.Single(posts!.Items).Caption);
    }

    [Fact]
    public async Task Post_rejects_an_empty_batch()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/import", new CreateImportRequest
        {
            Platform = SourcePlatform.TikTok,
            Posts = []
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Get_returns_not_found_for_another_users_import()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        var stranger = ClientFor(Guid.NewGuid().ToString());

        var created = await owner.PostAsJsonAsync("/api/import", InstagramBatch("private1"));
        var summary = await created.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        var mine = await owner.GetAsync($"/api/import/{summary!.Id}");
        var theirs = await stranger.GetAsync($"/api/import/{summary.Id}");

        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }

    [Fact]
    public async Task ListPosts_returns_not_found_for_another_users_import()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        var stranger = ClientFor(Guid.NewGuid().ToString());

        var created = await owner.PostAsJsonAsync("/api/import", InstagramBatch("private2"));
        var summary = await created.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        var theirs = await stranger.GetAsync($"/api/import/{summary!.Id}/posts");

        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
    }

    [Fact]
    public async Task List_only_returns_the_callers_imports()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        await client.PostAsJsonAsync("/api/import", InstagramBatch("mine1"));
        await client.PostAsJsonAsync("/api/import", InstagramBatch("mine2"));

        var page = await client.GetFromJsonAsync<PaginatedResult<ImportSummaryDto>>(
            "/api/import", AppFixture.JsonOptions);

        Assert.Equal(2, page!.TotalCount);
    }

    [Fact]
    public async Task Platform_enum_serialises_as_a_string()
    {
        // The app sends ints and reads strings; both halves are a wire contract.
        var client = ClientFor(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/import", InstagramBatch("enum1"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("\"Instagram\"", body);
    }
}
