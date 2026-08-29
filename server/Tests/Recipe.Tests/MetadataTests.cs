using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;
using Recipe.Api.Services.Metadata;

namespace Recipe.Tests;

/// <summary>
/// Stands in for TikTok's oEmbed endpoint. <see cref="Respond"/> maps a platform item id
/// to a result, null for a video the platform no longer serves, or throws for a transient
/// failure.
/// </summary>
public class StubOEmbed : IOEmbedClient
{
    public Func<string, OEmbedResult?>? Respond { get; set; }
    public Func<string, Exception?>? Throw { get; set; }
    public List<string> Requested { get; } = [];

    public Task<OEmbedResult?> FetchAsync(string platformItemId, CancellationToken cancellationToken = default)
    {
        Requested.Add(platformItemId);

        if (Throw?.Invoke(platformItemId) is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(Respond?.Invoke(platformItemId));
    }

    public static OEmbedResult Result(string handle = "cj.eats", string? caption = null) => new(
        Title: caption ?? "SUPER CRISPY Korean style Honey Butter Fried Chicken! #recipe",
        AuthorUniqueId: handle,
        AuthorName: "CJ Eats",
        ThumbnailUrl: "https://p16.tiktokcdn.com/thumb.jpg");
}

public class MetadataTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubOEmbed OEmbed => fixture.Services.GetRequiredService<StubOEmbed>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    /// <summary>Imports posts the way a real TikTok export arrives: a link and a date, nothing else.</summary>
    private static CreateImportRequest TikTokBatch(params string[] ids) => new()
    {
        Platform = SourcePlatform.TikTok,
        // Distinct descending dates so batch order is deterministic: the service works
        // newest first, and equal timestamps would leave the selection undefined.
        Posts = [.. ids.Select((id, index) => new ImportPostDto
        {
            PlatformItemId = id,
            Url = $"https://www.tiktokv.com/share/video/{id}/",
            Kind = SavedPostKind.Video,
            SavedAt = new DateTime(2026, 8, 28).AddDays(-index)
        })]
    };

    private async Task<(Guid ImportId, List<SavedPostDto> Posts)> Import(
        HttpClient client, CreateImportRequest request)
    {
        var created = await client.PostAsJsonAsync("/api/import", request);
        var summary = await created.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);
        var posts = await client.GetFromJsonAsync<PaginatedResult<SavedPostDto>>(
            $"/api/import/{summary!.Id}/posts?pageSize=100", AppFixture.JsonOptions);
        return (summary.Id, [.. posts!.Items]);
    }

    [Fact]
    public async Task Imported_tiktok_posts_start_pending_with_no_creator()
    {
        var client = ClientFor(Guid.NewGuid().ToString());

        var (_, posts) = await Import(client, TikTokBatch("m1"));

        Assert.Equal(MetadataStatus.Pending, posts[0].MetadataStatus);
        Assert.Null(posts[0].CreatorHandle);
        Assert.Null(posts[0].Caption);
    }

    [Fact]
    public async Task Fetch_populates_the_caption_creator_and_thumbnail()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var (_, posts) = await Import(client, TikTokBatch("m2"));
        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();

        var response = await client.PostAsync($"/api/import/posts/{posts[0].Id}/metadata", null);
        var updated = await response.Content.ReadFromJsonAsync<SavedPostDto>(AppFixture.JsonOptions);

        Assert.Equal(MetadataStatus.Fetched, updated!.MetadataStatus);
        Assert.Equal("cj.eats", updated.CreatorHandle);
        Assert.Contains("Korean", updated.Caption);
        Assert.NotNull(updated.ThumbnailUrl);
    }

    [Fact]
    public async Task Fetch_unblocks_extraction_by_supplying_the_handle()
    {
        // Without a handle, yt-dlp cannot address the video and extraction 400s. This is
        // the whole reason stage 1 has to run first.
        var client = ClientFor(Guid.NewGuid().ToString());
        var (_, posts) = await Import(client, TikTokBatch("7204147181404556586"));

        var before = await client.PostAsync($"/api/recipes/extract/{posts[0].Id}", null);
        Assert.Equal(HttpStatusCode.BadRequest, before.StatusCode);

        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        await client.PostAsync($"/api/import/posts/{posts[0].Id}/metadata", null);

        var sidecar = fixture.Services.GetRequiredService<StubSidecar>();
        sidecar.Throws = null;
        sidecar.Next = (_, _) => StubSidecar.Narrated();

        var after = await client.PostAsync($"/api/recipes/extract/{posts[0].Id}", null);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        Assert.Equal("https://www.tiktok.com/@cj.eats/video/7204147181404556586", sidecar.LastUrl);
    }

    [Fact]
    public async Task A_deleted_video_is_marked_unavailable_and_never_retried()
    {
        // 38% of a 2019-onward backlog. Terminal, and not an error.
        var client = ClientFor(Guid.NewGuid().ToString());
        var (importId, posts) = await Import(client, TikTokBatch("gone1"));
        OEmbed.Throw = null;
        OEmbed.Respond = _ => null;

        var response = await client.PostAsync($"/api/import/posts/{posts[0].Id}/metadata", null);
        var updated = await response.Content.ReadFromJsonAsync<SavedPostDto>(AppFixture.JsonOptions);
        Assert.Equal(MetadataStatus.Unavailable, updated!.MetadataStatus);

        OEmbed.Requested.Clear();
        var run = await (await client.PostAsync($"/api/import/{importId}/metadata", null))
            .Content.ReadFromJsonAsync<MetadataRunDto>(AppFixture.JsonOptions);

        Assert.Empty(OEmbed.Requested);
        Assert.Equal(0, run!.Remaining);
    }

    [Fact]
    public async Task A_transient_failure_stays_retryable()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var (importId, posts) = await Import(client, TikTokBatch("flaky1"));
        OEmbed.Respond = null;
        OEmbed.Throw = _ => new OEmbedException("TikTok timed out");

        await client.PostAsync($"/api/import/posts/{posts[0].Id}/metadata", null);

        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        var run = await (await client.PostAsync($"/api/import/{importId}/metadata", null))
            .Content.ReadFromJsonAsync<MetadataRunDto>(AppFixture.JsonOptions);

        Assert.Equal(1, run!.Fetched);
        Assert.Equal(0, run.Remaining);
    }

    [Fact]
    public async Task Batch_reports_counts_and_what_is_left()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var (importId, _) = await Import(client, TikTokBatch("b1", "b2", "b3", "b4"));
        OEmbed.Throw = null;
        OEmbed.Respond = id => id == "b2" ? null : StubOEmbed.Result();

        var run = await (await client.PostAsync($"/api/import/{importId}/metadata?limit=3", null))
            .Content.ReadFromJsonAsync<MetadataRunDto>(AppFixture.JsonOptions);

        Assert.Equal(3, run!.Attempted);
        Assert.Equal(2, run.Fetched);
        Assert.Equal(1, run.Unavailable);
        Assert.Equal(1, run.Remaining);
        Assert.False(run.StoppedEarly);
    }

    [Fact]
    public async Task Rate_limiting_stops_the_run_instead_of_burning_the_backlog()
    {
        // Marking hundreds of live videos dead because the platform said 429 would be
        // unrecoverable — the status is terminal.
        var client = ClientFor(Guid.NewGuid().ToString());
        var (importId, _) = await Import(client, TikTokBatch("r1", "r2", "r3", "r4", "r5"));
        OEmbed.Respond = null;
        OEmbed.Throw = _ => new OEmbedException("TikTok is rate-limiting requests; back off and retry later.");
        OEmbed.Requested.Clear();

        var run = await (await client.PostAsync($"/api/import/{importId}/metadata", null))
            .Content.ReadFromJsonAsync<MetadataRunDto>(AppFixture.JsonOptions);

        Assert.True(run!.StoppedEarly);
        // Stopped after the first refusal — the other four were never called.
        Assert.Equal(1, run.Attempted);
        Assert.Single(OEmbed.Requested);
        // Failed is retryable, so all five are still outstanding.
        Assert.Equal(5, run.Remaining);
    }

    [Fact]
    public async Task Instagram_posts_are_skipped_because_the_export_already_has_captions()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var (_, posts) = await Import(client, new CreateImportRequest
        {
            Platform = SourcePlatform.Instagram,
            Posts =
            [
                new ImportPostDto
                {
                    PlatformItemId = "ig1",
                    Url = "https://www.instagram.com/p/ig1/",
                    Kind = SavedPostKind.Post,
                    Captions = ["Garlic butter pasta"]
                }
            ]
        });
        OEmbed.Requested.Clear();

        var response = await client.PostAsync($"/api/import/posts/{posts[0].Id}/metadata", null);
        var updated = await response.Content.ReadFromJsonAsync<SavedPostDto>(AppFixture.JsonOptions);

        Assert.Equal(MetadataStatus.NotNeeded, updated!.MetadataStatus);
        Assert.Empty(OEmbed.Requested);
        Assert.Equal("Garlic butter pasta", updated.Caption);
    }

    [Fact]
    public async Task Batch_works_newest_first()
    {
        // Old saves are disproportionately deleted, so the newest are both likelier to
        // resolve and likelier to be what the user still cares about.
        var client = ClientFor(Guid.NewGuid().ToString());
        var (importId, _) = await Import(client, new CreateImportRequest
        {
            Platform = SourcePlatform.TikTok,
            Posts =
            [
                new ImportPostDto
                {
                    PlatformItemId = "old", Url = "https://www.tiktokv.com/share/video/old/",
                    Kind = SavedPostKind.Video, SavedAt = new DateTime(2019, 10, 27)
                },
                new ImportPostDto
                {
                    PlatformItemId = "new", Url = "https://www.tiktokv.com/share/video/new/",
                    Kind = SavedPostKind.Video, SavedAt = new DateTime(2026, 8, 28)
                }
            ]
        });
        OEmbed.Throw = null;
        OEmbed.Respond = _ => StubOEmbed.Result();
        OEmbed.Requested.Clear();

        await client.PostAsync($"/api/import/{importId}/metadata?limit=1", null);

        Assert.Equal("new", Assert.Single(OEmbed.Requested));
    }

    [Fact]
    public async Task Another_users_import_cannot_be_fetched()
    {
        var owner = ClientFor(Guid.NewGuid().ToString());
        var stranger = ClientFor(Guid.NewGuid().ToString());
        var (importId, _) = await Import(owner, TikTokBatch("p1"));

        var response = await stranger.PostAsync($"/api/import/{importId}/metadata", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
