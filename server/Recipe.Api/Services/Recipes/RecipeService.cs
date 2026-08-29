using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Extraction;
using Recipe.Api.Services.Metadata;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Api.Services.Recipes;

public interface IRecipeService
{
    /// <summary>Runs the cascade for one saved post and stores the result.</summary>
    Task<RecipeDto> ExtractAsync(string userId, Guid savedPostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The share-sheet path: takes a bare link, adds it to the user's cookbook, and
    /// returns a recipe. Serves an already-extracted result instantly when anyone has
    /// processed the same video before.
    /// </summary>
    Task<RecipeDto> ExtractFromUrlAsync(string userId, string url, CancellationToken cancellationToken = default);

    Task<RecipeDto> GetAsync(string userId, Guid recipeId, CancellationToken cancellationToken = default);

    Task<PaginatedResult<RecipeSummaryDto>> ListAsync(
        string userId, ExtractionStatus? status, int pageNumber, int pageSize,
        CancellationToken cancellationToken = default);
}

public class RecipeService(
    AppDbContext db,
    ISidecarClient sidecar,
    IMetadataService metadata,
    IShortLinkResolver shortLinks,
    TimeProvider timeProvider) : IRecipeService
{
    public async Task<RecipeDto> ExtractFromUrlAsync(
        string userId,
        string url,
        CancellationToken cancellationToken = default)
    {
        // Share-sheet links carry no video id; they have to be followed first.
        var resolved = await shortLinks.ResolveAsync(url, cancellationToken);

        if (!PostUrl.TryParse(resolved, out var parsed))
        {
            throw new DomainValidationException(
                "That does not look like a TikTok or Instagram post link.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // The user may already have this from a bulk import, or from sharing it before.
        var post = await db.SavedPosts.FirstOrDefaultAsync(
            p => p.UserId == userId
                 && p.Platform == parsed.Platform
                 && p.PlatformItemId == parsed.ItemId,
            cancellationToken);

        if (post is null)
        {
            post = new SavedPost
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                ImportJobId = await ShareJobIdAsync(userId, parsed.Platform, now, cancellationToken),
                Platform = parsed.Platform,
                PlatformItemId = parsed.ItemId,
                Url = parsed.CanonicalUrl,
                Kind = parsed.Kind,
                CreatedAt = now
            };
            db.SavedPosts.Add(post);
            await db.SaveChangesAsync(cancellationToken);
        }

        // Cross-user cache. A viral recipe is extracted once and reused for everyone —
        // the second person to share it pays nothing and waits for nothing.
        var cached = await db.Recipes
            .Where(r => r.Status == ExtractionStatus.Extracted
                        && r.SavedPost!.Platform == parsed.Platform
                        && r.SavedPost.PlatformItemId == parsed.ItemId)
            .OrderByDescending(r => r.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (cached is not null && cached.SavedPostId != post.Id)
        {
            return ToDto(await CopyForUserAsync(cached, userId, post, now, cancellationToken), post);
        }

        // Stage 1 first when the post has no creator handle: yt-dlp cannot address a
        // TikTok video without it, so extraction would fail outright.
        if (parsed.Platform == SourcePlatform.TikTok && string.IsNullOrWhiteSpace(post.CreatorHandle))
        {
            await metadata.FetchAsync(userId, post.Id, cancellationToken);
        }

        return await ExtractAsync(userId, post.Id, cancellationToken);
    }

    /// <summary>
    /// Shared posts still need an import job to hang from. One per user per platform,
    /// reused, so a cookbook built by sharing does not accumulate hundreds of jobs.
    /// </summary>
    private async Task<Guid> ShareJobIdAsync(
        string userId, SourcePlatform platform, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await db.ImportJobs
            .Where(j => j.UserId == userId && j.Platform == platform && j.SubmittedCount == 0)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (existing is { } id)
        {
            return id;
        }

        var job = new ImportJob
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Platform = platform,
            SubmittedCount = 0,
            CreatedAt = now
        };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        return job.Id;
    }

    /// <summary>
    /// Copies a cached extraction onto this user's own row. Recipes are per-user because
    /// the user can edit any field, so they cannot share a row with a stranger.
    /// </summary>
    private async Task<RecipeEntity> CopyForUserAsync(
        RecipeEntity source, string userId, SavedPost post, DateTime now,
        CancellationToken cancellationToken)
    {
        var mine = await db.Recipes.FirstOrDefaultAsync(r => r.SavedPostId == post.Id, cancellationToken);

        if (mine is null)
        {
            mine = new RecipeEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SavedPostId = post.Id,
                CreatedAt = now
            };
            db.Recipes.Add(mine);
        }

        mine.Status = source.Status;
        mine.Title = source.Title;
        mine.Servings = source.Servings;
        mine.PrepMinutes = source.PrepMinutes;
        mine.CookMinutes = source.CookMinutes;
        mine.Ingredients = [.. source.Ingredients];
        mine.Steps = [.. source.Steps];
        mine.Equipment = [.. source.Equipment];
        mine.FoodConfidence = source.FoodConfidence;
        mine.TranscriptLanguage = source.TranscriptLanguage;
        mine.FailureReason = null;
        mine.ExtractedAt = source.ExtractedAt;
        mine.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        return mine;
    }

    public async Task<RecipeDto> ExtractAsync(
        string userId,
        Guid savedPostId,
        CancellationToken cancellationToken = default)
    {
        var post = await db.SavedPosts
            .FirstOrDefaultAsync(p => p.Id == savedPostId && p.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Saved post {savedPostId} was not found.");

        var url = MediaUrl.For(post);

        if (url is null)
        {
            // A TikTok row imported straight from the export has no creator handle, and
            // yt-dlp cannot address the video without one. Stage 1 metadata has to run
            // before this post can be extracted.
            throw new DomainValidationException(
                "This TikTok post has no creator handle yet, so its video cannot be located. "
                + "Run stage 1 metadata for it first.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var recipe = await db.Recipes
            .FirstOrDefaultAsync(r => r.SavedPostId == post.Id, cancellationToken);

        if (recipe is null)
        {
            recipe = new RecipeEntity
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                SavedPostId = post.Id,
                CreatedAt = now
            };
            db.Recipes.Add(recipe);
        }

        recipe.UpdatedAt = now;

        try
        {
            var result = await sidecar.TranscribeAsync(url, post.Caption, cancellationToken);
            Apply(recipe, result, now);
        }
        catch (SidecarException ex)
        {
            // A fetch failure is a normal outcome on an old backlog, not an exception the
            // caller should see as a 500 — record it and hand back the row.
            recipe.Status = ExtractionStatus.Failed;
            recipe.FailureReason = ex.Message;
        }

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(recipe, post);
    }

    private static void Apply(RecipeEntity recipe, SidecarResult result, DateTime now)
    {
        recipe.FailureReason = null;
        recipe.TranscriptLanguage = result.Transcript.Language;
        recipe.ExtractedAt = now;

        if (result.Recipe is null)
        {
            // The sidecar reads frames itself now, so a silent video is not automatically
            // a vision case — only one that produced nothing at all is. Keying this off
            // the transcript would mark every successful vision extraction as pending.
            recipe.Status = ExtractionStatus.NeedsVision;
            return;
        }

        var extracted = result.Recipe;

        if (!extracted.IsRecipe)
        {
            recipe.Status = ExtractionStatus.NotARecipe;
            recipe.Title = extracted.Title;
            return;
        }

        recipe.Status = ExtractionStatus.Extracted;
        recipe.Title = extracted.Title;
        recipe.Servings = extracted.Servings;
        recipe.PrepMinutes = extracted.PrepMinutes;
        recipe.CookMinutes = extracted.CookMinutes;
        recipe.FoodConfidence = extracted.FoodConfidence;
        recipe.Equipment = extracted.Equipment ?? [];

        recipe.Ingredients = [.. (extracted.Ingredients ?? []).Select(i =>
            new RecipeIngredient(i.Quantity, i.Unit, i.Item, i.PrepNote, i.Confidence, i.SourceTs))];

        recipe.Steps = [.. (extracted.Steps ?? []).Select(s =>
            new RecipeStep(s.Text, s.TsStart, s.TsEnd))];
    }

    public async Task<RecipeDto> GetAsync(
        string userId,
        Guid recipeId,
        CancellationToken cancellationToken = default)
    {
        var row = await db.Recipes
            .Where(r => r.Id == recipeId && r.UserId == userId)
            .Select(r => new { Recipe = r, r.SavedPost })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        return ToDto(row.Recipe, row.SavedPost);
    }

    public Task<PaginatedResult<RecipeSummaryDto>> ListAsync(
        string userId,
        ExtractionStatus? status,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var query = db.Recipes
            .Where(r => r.UserId == userId)
            .Where(r => status == null || r.Status == status)
            .OrderByDescending(r => r.FoodConfidence)
            .ThenByDescending(r => r.UpdatedAt)
            .Select(r => new RecipeSummaryDto(
                r.Id,
                r.SavedPostId,
                r.Status,
                r.Title,
                r.Ingredients.Count,
                r.Steps.Count,
                r.FoodConfidence,
                r.SavedPost!.CreatorHandle,
                r.SavedPost.Url,
                r.UpdatedAt));

        return PaginatedResult<RecipeSummaryDto>.CreateAsync(query, pageNumber, pageSize, cancellationToken);
    }

    private static RecipeDto ToDto(RecipeEntity recipe, SavedPost? post) => new(
        recipe.Id,
        recipe.SavedPostId,
        recipe.Status,
        recipe.FailureReason,
        recipe.Title,
        recipe.Servings,
        recipe.PrepMinutes,
        recipe.CookMinutes,
        recipe.Ingredients,
        recipe.Steps,
        recipe.Equipment,
        recipe.FoodConfidence,
        recipe.TranscriptLanguage,
        post?.CreatorHandle,
        post?.Url,
        recipe.ExtractedAt,
        recipe.UpdatedAt);
}
