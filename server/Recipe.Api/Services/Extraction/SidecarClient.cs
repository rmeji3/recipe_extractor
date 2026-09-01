using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Recipe.Api.Models.Import;

namespace Recipe.Api.Services.Extraction;

/// <summary>Transcribes a video and structures it, via the Python extraction sidecar.</summary>
public interface ISidecarClient
{
    Task<SidecarResult> TranscribeAsync(string url, string? caption, CancellationToken cancellationToken = default);
}

/// <summary>Raised when the sidecar could not process the media. Carries a user-safe message.</summary>
public class SidecarException(string message) : Exception(message);

/// <summary>
/// The sidecar's response. It speaks snake_case; these records mirror it exactly.
/// <c>Path</c> reports which route produced the recipe — "narration", "vision", or "none" —
/// and <c>FramesUsed</c> is zero unless the vision path ran.
/// </summary>
public record SidecarResult(
    [property: JsonPropertyName("source_id")] string? SourceId,
    [property: JsonPropertyName("seconds_elapsed")] double SecondsElapsed,
    [property: JsonPropertyName("transcript")] SidecarTranscript Transcript,
    [property: JsonPropertyName("recipe")] SidecarRecipe? Recipe,
    [property: JsonPropertyName("note")] string? Note,
    [property: JsonPropertyName("path")] string? Path = null,
    [property: JsonPropertyName("frames_used")] int FramesUsed = 0,
    /// <summary>The post's own description, read from the fetch.</summary>
    [property: JsonPropertyName("caption")] string? Caption = null,
    [property: JsonPropertyName("creator_handle")] string? CreatorHandle = null);

public record SidecarTranscript(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("duration")] double Duration,
    [property: JsonPropertyName("is_speech")] bool IsSpeech);

public record SidecarRecipe(
    [property: JsonPropertyName("is_recipe")] bool IsRecipe,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("servings")] int? Servings,
    [property: JsonPropertyName("prep_minutes")] int? PrepMinutes,
    [property: JsonPropertyName("cook_minutes")] int? CookMinutes,
    [property: JsonPropertyName("ingredients")] List<SidecarIngredient> Ingredients,
    [property: JsonPropertyName("steps")] List<SidecarStep> Steps,
    [property: JsonPropertyName("equipment")] List<string> Equipment,
    [property: JsonPropertyName("food_confidence")] double FoodConfidence);

public record SidecarIngredient(
    [property: JsonPropertyName("group")] string? Group,
    [property: JsonPropertyName("quantity")] double? Quantity,
    [property: JsonPropertyName("unit")] string? Unit,
    [property: JsonPropertyName("item")] string Item,
    [property: JsonPropertyName("prep_note")] string? PrepNote,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("source_ts")] double? SourceTs);

public record SidecarStep(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("ts_start")] double? TsStart,
    [property: JsonPropertyName("ts_end")] double? TsEnd);

public class SidecarClient(HttpClient http, ILogger<SidecarClient> logger) : ISidecarClient
{
    public async Task<SidecarResult> TranscribeAsync(
        string url,
        string? caption,
        CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response;

        try
        {
            response = await http.PostAsJsonAsync(
                "/transcribe",
                new { url, caption, structure = true },
                cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogError(ex, "Extraction sidecar unreachable at {BaseAddress}", http.BaseAddress);
            throw new SidecarException("The extraction service is unavailable.");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ex, "Extraction sidecar timed out for {Url}", url);
            throw new SidecarException("The extraction service timed out.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity)
        {
            // The sidecar could not fetch or read the media — a deleted video, a private
            // account, a platform change. Expected often enough to be a normal outcome.
            var problem = await response.Content.ReadFromJsonAsync<SidecarProblem>(cancellationToken);
            throw new SidecarException(problem?.Detail ?? "The video could not be fetched.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError("Extraction sidecar returned {Status}: {Body}",
                (int)response.StatusCode, body[..Math.Min(body.Length, 500)]);
            throw new SidecarException("The extraction service failed.");
        }

        return await response.Content.ReadFromJsonAsync<SidecarResult>(cancellationToken)
               ?? throw new SidecarException("The extraction service returned an empty response.");
    }

    private record SidecarProblem([property: JsonPropertyName("detail")] string? Detail);
}

/// <summary>Builds the URL shape each platform's downloader actually accepts.</summary>
public static class MediaUrl
{
    /// <summary>
    /// yt-dlp needs TikTok's canonical creator URL: <c>tiktok.com/@handle/video/{id}</c>.
    /// The share link in the export and the id-only form that oEmbed accepts both 404
    /// there, so the creator handle from stage 1 is required before a TikTok post can be
    /// extracted at all.
    /// </summary>
    public static string? For(SavedPost post) => post.Platform switch
    {
        SourcePlatform.TikTok when !string.IsNullOrWhiteSpace(post.CreatorHandle)
            => $"https://www.tiktok.com/@{post.CreatorHandle.TrimStart('@')}/video/{post.PlatformItemId}",
        SourcePlatform.TikTok => null,
        _ => post.Url
    };
}
