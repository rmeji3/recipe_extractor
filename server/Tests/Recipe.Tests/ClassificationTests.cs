using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Auth;
using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;
using Recipe.Api.Models.Import;
using Recipe.Api.Services.Classification;
using Recipe.Api.Services.Queue;

namespace Recipe.Tests;

/// <summary>
/// The ranked queue: three passes, cheapest first, so only the ambiguous middle costs
/// anything. Tuned for precision — a missed recipe is recoverable from the skipped list,
/// a cookbook full of memes is not.
/// </summary>
public class ClassificationTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubClassifier Classifier => fixture.Services.GetRequiredService<StubClassifier>();
    private StubJobQueue Queue => fixture.Services.GetRequiredService<StubJobQueue>();

    private HttpClient ClientFor(string userId)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add(DevAuthenticationHandler.UserHeader, userId);
        return client;
    }

    private async Task<Guid> Import(HttpClient client, params (string Id, string? Caption, string? Handle)[] posts)
    {
        var response = await client.PostAsJsonAsync("/api/import", new CreateImportRequest
        {
            Platform = SourcePlatform.Instagram,
            Posts = [.. posts.Select(p => new ImportPostDto
            {
                PlatformItemId = p.Id,
                Url = $"https://www.instagram.com/p/{p.Id}/",
                Kind = SavedPostKind.Post,
                Captions = p.Caption is null ? null : [p.Caption],
                CreatorHandle = p.Handle
            })]
        });

        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);
        return summary!.Id;
    }

    private async Task<ClassificationRun> Classify(Guid importId, int limit = 100)
    {
        using var scope = fixture.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IClassificationService>()
            .ClassifyPendingAsync(importId, limit);
    }

    private ClassificationStatus StatusOf(string itemId)
    {
        using var db = fixture.CreateDbContext();
        return db.SavedPosts.Single(p => p.PlatformItemId == itemId).ClassificationStatus;
    }

    // --------------------------------------------------------- keyword pass

    [Fact]
    public async Task An_explicit_recipe_marker_resolves_without_a_model_call()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client, ("c1", "Creamy butter chicken #recipe 2 tbsp garam masala", "chef"));
        Classifier.BatchSizes.Clear();

        var run = await Classify(import);

        Assert.Equal(1, run.Keyword);
        Assert.Equal(0, run.Model);
        Assert.Empty(Classifier.BatchSizes);
        Assert.Equal(ClassificationStatus.Food, StatusOf("c1"));
    }

    [Fact]
    public async Task A_caption_with_no_food_vocabulary_is_rejected_for_free()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client, ("c2", "Sunset timelapse from the balcony #view", "someone"));
        Classifier.BatchSizes.Clear();

        var run = await Classify(import);

        Assert.Equal(1, run.Keyword);
        Assert.Empty(Classifier.BatchSizes);
        Assert.Equal(ClassificationStatus.NotFood, StatusOf("c2"));
    }

    [Fact]
    public async Task Gaming_vocabulary_beats_an_incidental_food_word()
    {
        // Measured false positives on a real corpus: a Warzone clip matched on "season",
        // a Cookie Monster cartoon on "cookie".
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client,
            ("c3", "Top 5 SMGs Season 3 #warzone #gaming", "gamer"),
            ("c4", "Elmo and Cookie Monster drop 10/4 #cartoon #gaming", "artist"));

        await Classify(import);

        Assert.Equal(ClassificationStatus.NotFood, StatusOf("c3"));
        Assert.Equal(ClassificationStatus.NotFood, StatusOf("c4"));
    }

    [Fact]
    public async Task A_deleted_post_is_unclassifiable_rather_than_not_food()
    {
        // Nothing will ever be known about it; that is different from judging it.
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client, ("c5", null, null));

        using (var db = fixture.CreateDbContext())
        {
            var post = db.SavedPosts.Single(p => p.PlatformItemId == "c5");
            post.MetadataStatus = MetadataStatus.Unavailable;
            db.SaveChanges();
        }

        await Classify(import);

        Assert.Equal(ClassificationStatus.Unclassifiable, StatusOf("c5"));
    }

    [Fact]
    public async Task A_post_with_no_caption_is_never_sent_to_the_model()
    {
        // Asked to classify an empty caption the model answers "food". On a real backlog
        // that marked 350 captionless posts as recipes and cost real money.
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client, ("c50", null, "chef"));
        Classifier.BatchSizes.Clear();

        var run = await Classify(import);

        Assert.Empty(Classifier.BatchSizes);
        Assert.Equal(0, run.Model);
        // Instagram captions arrive with the export, so a blank one is blank for good.
        Assert.Equal(ClassificationStatus.Unclassifiable, StatusOf("c50"));
    }

    [Fact]
    public async Task A_tiktok_post_still_awaiting_stage_one_stays_pending()
    {
        // Its caption has not arrived yet. Judging it now would burn the one verdict it
        // gets on the least information it will ever have.
        var client = ClientFor(Guid.NewGuid().ToString());

        var response = await client.PostAsJsonAsync("/api/import", new CreateImportRequest
        {
            Platform = SourcePlatform.TikTok,
            Posts = [new ImportPostDto
            {
                PlatformItemId = "c51",
                Url = "https://www.tiktokv.com/share/video/c51/",
                Kind = SavedPostKind.Video
            }]
        });
        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);
        Classifier.BatchSizes.Clear();

        var run = await Classify(summary!.Id);

        Assert.Empty(Classifier.BatchSizes);
        Assert.Equal(ClassificationStatus.Pending, StatusOf("c51"));
        Assert.Equal(1, run.Remaining);
        // Nothing was resolved, so the worker must not re-queue itself into a tight loop.
        Assert.Equal(0, run.Keyword + run.Creator + run.Model);
    }

    // -------------------------------------------------------- creator pass

    [Fact]
    public async Task An_ambiguous_post_inherits_its_creators_track_record()
    {
        // People save recipes from the same few food accounts repeatedly. This pass is
        // free and, on a real corpus, carried a third of all food videos.
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        var known = await Import(client,
            ("c6", "Garlic butter pasta #recipe 200g spaghetti", "pastaguy"),
            ("c7", "One pot orzo #recipe 2 cups stock", "pastaguy"));
        await Classify(known);

        var ambiguous = await Import(client, ("c8", "dinner tonight", "pastaguy"));
        Classifier.BatchSizes.Clear();

        var run = await Classify(ambiguous);

        Assert.Equal(1, run.Creator);
        Assert.Equal(0, run.Model);
        // Inherited evidence is weaker than evidence about this post, so it lands in the
        // review pile rather than straight into the cookbook.
        Assert.Equal(ClassificationStatus.Uncertain, StatusOf("c8"));
    }

    [Fact]
    public async Task One_creator_hit_is_not_enough_to_infer_from()
    {
        var userId = Guid.NewGuid().ToString();
        var client = ClientFor(userId);

        var known = await Import(client, ("c9", "Chana masala #recipe 2 tbsp cumin", "onehit"));
        await Classify(known);

        var ambiguous = await Import(client, ("c10", "dinner tonight", "onehit"));
        Classifier.BatchSizes.Clear();
        Classifier.Respond = _ => new ClassifierVerdict(true, 0.9);

        var run = await Classify(ambiguous);

        Assert.Equal(0, run.Creator);
        Assert.Equal(1, run.Model);
    }

    [Fact]
    public async Task A_creators_reputation_does_not_leak_between_users()
    {
        var mine = ClientFor(Guid.NewGuid().ToString());
        var theirs = ClientFor(Guid.NewGuid().ToString());

        var known = await Import(mine,
            ("c11", "Butter chicken #recipe 600g chicken", "sharedchef"),
            ("c12", "Dal tadka #recipe 1 cup lentils", "sharedchef"));
        await Classify(known);

        var other = await Import(theirs, ("c13", "dinner tonight", "sharedchef"));
        Classifier.Respond = _ => new ClassifierVerdict(true, 0.9);

        var run = await Classify(other);

        Assert.Equal(0, run.Creator);
        Assert.Equal(1, run.Model);
    }

    // ---------------------------------------------------------- model pass

    [Fact]
    public async Task The_ambiguous_middle_goes_to_the_model_in_one_batch()
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client,
            [.. Enumerable.Range(0, 12).Select(i => ($"c2{i:D2}", (string?)"dinner tonight", (string?)$"chef{i}"))]);
        Classifier.BatchSizes.Clear();
        Classifier.Respond = _ => new ClassifierVerdict(true, 0.9);

        var run = await Classify(import);

        Assert.Equal(12, run.Model);
        // One call, not twelve.
        Assert.Equal(12, Assert.Single(Classifier.BatchSizes));
    }

    [Theory]
    [InlineData(0.95, ClassificationStatus.Food)]
    [InlineData(0.60, ClassificationStatus.Uncertain)]
    [InlineData(0.20, ClassificationStatus.NotFood)]
    public async Task Model_confidence_maps_to_the_three_tiers(double confidence, ClassificationStatus expected)
    {
        var client = ClientFor(Guid.NewGuid().ToString());
        var id = $"c3{confidence:F2}";
        var import = await Import(client, (id, "dinner tonight", "chef"));
        Classifier.Respond = _ => new ClassifierVerdict(true, confidence);

        await Classify(import);

        Assert.Equal(expected, StatusOf(id));
    }

    [Fact]
    public async Task A_classifier_outage_leaves_posts_pending_for_retry()
    {
        // Returning nothing must not be read as "not food" — that verdict is terminal and
        // would quietly delete a recipe from the user's cookbook.
        var client = ClientFor(Guid.NewGuid().ToString());
        var import = await Import(client, ("c40", "dinner tonight", "chef"));
        Classifier.ReturnEmpty = true;

        try
        {
            var run = await Classify(import);

            Assert.Equal(0, run.Model);
            Assert.Equal(ClassificationStatus.Pending, StatusOf("c40"));
        }
        finally
        {
            Classifier.ReturnEmpty = false;
        }
    }

    // ---------------------------------------------------------------- queue

    [Fact]
    public async Task An_import_queues_its_own_follow_up_work()
    {
        // Bulk import must never be something the user waits on.
        var client = ClientFor(Guid.NewGuid().ToString());
        Queue.Clear();

        var response = await client.PostAsJsonAsync("/api/import", new CreateImportRequest
        {
            Platform = SourcePlatform.TikTok,
            Posts = [.. new[] { "q1", "q2" }.Select(id => new ImportPostDto
            {
                PlatformItemId = id,
                Url = $"https://www.tiktokv.com/share/video/{id}/",
                Kind = SavedPostKind.Video
            })]
        });
        var summary = await response.Content.ReadFromJsonAsync<ImportSummaryDto>(AppFixture.JsonOptions);

        // Stage 1 per post, because TikTok exports carry nothing but a link.
        Assert.Equal(2, Queue.Enqueued.Count(j => j.Type == JobType.FetchMetadata));
        // Classification once for the import, because it batches.
        var classify = Assert.Single(Queue.Enqueued, j => j.Type == JobType.Classify);
        Assert.Equal(summary!.Id, classify.TargetId);
    }

    [Fact]
    public async Task An_instagram_import_skips_stage_one()
    {
        // Instagram exports already carry captions; there is nothing to fetch.
        var client = ClientFor(Guid.NewGuid().ToString());
        Queue.Clear();

        await Import(client, ("q3", "Garlic butter pasta #recipe", "chef"));

        Assert.DoesNotContain(Queue.Enqueued, j => j.Type == JobType.FetchMetadata);
        Assert.Single(Queue.Enqueued, j => j.Type == JobType.Classify);
    }

    [Fact]
    public void A_job_past_the_retry_limit_is_dropped_not_requeued()
    {
        var job = new Job(JobType.Extract, "u", Guid.NewGuid(), 1, DateTime.UtcNow);

        var retried = job.Retry().Retry();

        Assert.Equal(3, retried.Attempt);
        Assert.True(retried.Attempt <= Recipe.Api.Services.Queue.RedisJobQueue.MaxAttempts);
        Assert.True(retried.Retry().Attempt > Recipe.Api.Services.Queue.RedisJobQueue.MaxAttempts);
    }
}
