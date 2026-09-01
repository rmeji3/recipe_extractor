using Recipe.Api.Common;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Models.Import;
using Recipe.Api.Models.Recipes;
using Recipe.Api.Services.Extraction;
using Recipe.Api.Services.Metadata;
using Recipe.Api.Services.Queue;
using RecipeEntity = Recipe.Api.Models.Recipes.Recipe;

namespace Recipe.Api.Services.Recipes;

public interface IRecipeService
{
    /// <summary>Runs the cascade for one saved post and stores the result.</summary>
    Task<RecipeDto> ExtractAsync(string userId, Guid savedPostId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The share-sheet path: takes a bare link, adds it to the user's cookbook, and returns
    /// a recipe row immediately.
    /// </summary>
    /// <remarks>
    /// Returns <see cref="ExtractionStatus.Extracted"/> straight away when anyone has
    /// already processed the same video — the cross-user cache makes that the common case
    /// for anything popular. Otherwise the row comes back <see cref="ExtractionStatus.Processing"/>
    /// with the work queued, and the caller polls <c>GET /api/recipes/{id}</c>. A cold
    /// extraction takes up to a minute, which is longer than a phone will reliably hold a
    /// request open.
    /// </remarks>
    Task<RecipeDto> ExtractFromUrlAsync(string userId, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Everything one queued post needs: stage 1 when its creator handle is missing, then
    /// the extraction cascade. This is what the worker runs.
    /// </summary>
    Task<RecipeDto> ProcessAsync(string userId, Guid savedPostId, CancellationToken cancellationToken = default);

    Task<RecipeDto> GetAsync(string userId, Guid recipeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists a user's recipes, most confident first. <c>query</c> searches title,
    /// ingredients, equipment, and creator; null or blank lists everything.
    /// </summary>
    Task<PaginatedResult<RecipeSummaryDto>> ListAsync(
        string userId, ExtractionStatus? status, string? query, int pageNumber, int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the user-editable fields of a recipe.</summary>
    Task<RecipeDto> UpdateAsync(string userId, Guid recipeId, UpdateRecipeRequest request,
        CancellationToken cancellationToken = default);
}

public class RecipeService(
    AppDbContext db,
    ISidecarClient sidecar,
    IMetadataService metadata,
    IShortLinkResolver shortLinks,
    IJobQueue queue,
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

        var existing = await db.Recipes.FirstOrDefaultAsync(r => r.SavedPostId == post.Id, cancellationToken);

        // Already done, or already queued. Re-sharing a link must not queue the work twice.
        if (existing is not null
            && existing.Status is ExtractionStatus.Extracted or ExtractionStatus.Processing)
        {
            return ToDto(existing, post);
        }

        var recipe = existing ?? new RecipeEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            SavedPostId = post.Id,
            CreatedAt = now
        };

        if (existing is null)
        {
            db.Recipes.Add(recipe);
        }

        recipe.Status = ExtractionStatus.Processing;
        recipe.FailureReason = null;
        recipe.UpdatedAt = now;
        recipe.SearchText = BuildSearchText(recipe, post);

        await db.SaveChangesAsync(cancellationToken);

        await queue.EnqueueAsync(new Job(JobType.Extract, userId, post.Id, 1, now), cancellationToken);

        return ToDto(recipe, post);
    }

    public async Task<RecipeDto> ProcessAsync(
        string userId,
        Guid savedPostId,
        CancellationToken cancellationToken = default)
    {
        var post = await db.SavedPosts
            .FirstOrDefaultAsync(p => p.Id == savedPostId && p.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Saved post {savedPostId} was not found.");

        // yt-dlp cannot address a TikTok video without its creator handle, so stage 1 has
        // to run first. On the share path the user pasted a link and expects a recipe, so
        // this is handled rather than reported.
        if (post.Platform == SourcePlatform.TikTok && string.IsNullOrWhiteSpace(post.CreatorHandle))
        {
            await metadata.FetchAsync(userId, savedPostId, cancellationToken);
        }

        return await ExtractAsync(userId, savedPostId, cancellationToken);
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
        mine.SearchText = BuildSearchText(mine, post);

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

            // The fetch reads the post's own description on the way past. On the share
            // path an Instagram post has no caption at all — nothing was uploaded and its
            // oEmbed needs an app token — so this is the only place one ever arrives, and
            // it is usually the only source carrying exact amounts.
            if (string.IsNullOrWhiteSpace(post.Caption) && !string.IsNullOrWhiteSpace(result.Caption))
            {
                post.Caption = CaptionText.Normalise([result.Caption]);
                post.MetadataStatus = MetadataStatus.Fetched;
                post.MetadataFetchedAt = now;
            }

            if (string.IsNullOrWhiteSpace(post.CreatorHandle) && !string.IsNullOrWhiteSpace(result.CreatorHandle))
            {
                post.CreatorHandle = result.CreatorHandle;
            }

            Apply(recipe, result, now);
        }
        catch (SidecarException ex)
        {
            // A fetch failure is a normal outcome on an old backlog, not an exception the
            // caller should see as a 500 — record it and hand back the row.
            recipe.Status = ExtractionStatus.Failed;
            recipe.FailureReason = ex.Message;
        }

        recipe.SearchText = BuildSearchText(recipe, post);
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
            new RecipeIngredient(i.Group, i.Quantity, i.Unit, i.Item, i.PrepNote, i.Confidence, i.SourceTs))];

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
        string? search,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var rows = db.Recipes
            .Where(r => r.UserId == userId)
            .Where(r => status == null || r.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();

            rows = db.IsNpgsql
                // Postgres does the real thing: stemming, ranking, GIN-indexed.
                ? rows.Where(r => r.SearchVector!.Matches(EF.Functions.PlainToTsQuery("english", term)))
                // SQLite has no full-text equivalent, and the suite runs on it. Substring
                // matching is weaker — no stemming, so "tomatoes" will not find "tomato" —
                // but it keeps tests provider-agnostic.
                : rows.Where(r => EF.Functions.Like(r.SearchText, $"%{term}%"));
        }

        var query = rows
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
                r.IsEdited,
                r.UpdatedAt));

        return PaginatedResult<RecipeSummaryDto>.CreateAsync(query, pageNumber, pageSize, cancellationToken);
    }

    public async Task<RecipeDto> UpdateAsync(
        string userId,
        Guid recipeId,
        UpdateRecipeRequest request,
        CancellationToken cancellationToken = default)
    {
        var recipe = await db.Recipes
            .Include(r => r.SavedPost)
            .FirstOrDefaultAsync(r => r.Id == recipeId && r.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException($"Recipe {recipeId} was not found.");

        recipe.Title = request.Title.Trim();
        recipe.Servings = request.Servings;
        recipe.PrepMinutes = request.PrepMinutes;
        recipe.CookMinutes = request.CookMinutes;

        recipe.Ingredients = [.. request.Ingredients.Select(i => new RecipeIngredient(
            string.IsNullOrWhiteSpace(i.Group) ? null : i.Group.Trim(),
            i.Quantity, i.Unit, i.Item.Trim(), i.PrepNote,
            // A value the user typed is certain by definition.
            Confidence: 1.0, i.SourceTs))];

        recipe.Steps = [.. request.Steps.Select(s => new RecipeStep(s.Text.Trim(), s.TsStart, s.TsEnd))];
        recipe.Equipment = [.. request.Equipment.Select(e => e.Trim()).Where(e => e.Length > 0)];

        // An edited recipe is one the user has corrected. Re-extraction must not silently
        // undo that work, and a hand-written recipe is no longer "needs vision".
        recipe.IsEdited = true;
        recipe.Status = ExtractionStatus.Extracted;
        recipe.FailureReason = null;
        recipe.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        recipe.SearchText = BuildSearchText(recipe, recipe.SavedPost);

        await db.SaveChangesAsync(cancellationToken);

        return ToDto(recipe, recipe.SavedPost);
    }

    /// <summary>
    /// Flattens everything worth searching into one string. Ingredients and steps live in
    /// JSON columns that no provider indexes usefully, so the searchable text is
    /// maintained alongside them on every write.
    /// </summary>
    private static string BuildSearchText(RecipeEntity recipe, SavedPost? post)
    {
        var parts = new List<string> { recipe.Title };
        parts.AddRange(recipe.Ingredients.Select(i => i.Item));
        parts.AddRange(recipe.Equipment);

        if (!string.IsNullOrWhiteSpace(post?.CreatorHandle))
        {
            parts.Add(post.CreatorHandle);
        }

        if (!string.IsNullOrWhiteSpace(post?.CreatorName))
        {
            parts.Add(post.CreatorName);
        }

        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
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
        recipe.IsEdited,
        post?.CreatorHandle,
        post?.Url,
        recipe.ExtractedAt,
        recipe.UpdatedAt);
}
