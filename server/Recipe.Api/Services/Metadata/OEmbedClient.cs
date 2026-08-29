using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recipe.Api.Services.Metadata;

/// <summary>
/// Stage 1 for the TikTok path: light metadata from TikTok's public oEmbed endpoint.
/// </summary>
/// <remarks>
/// The export gives nothing but a date and a link, so classification and extraction both
/// need this to run first. oEmbed is unauthenticated and intended for public embedding —
/// a far better posture than scraping — and it returns the caption untruncated.
/// </remarks>
public interface IOEmbedClient
{
    /// <summary>Returns null when the platform no longer serves the video.</summary>
    Task<OEmbedResult?> FetchAsync(string platformItemId, CancellationToken cancellationToken = default);
}

public record OEmbedResult(
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("author_unique_id")] string? AuthorUniqueId,
    [property: JsonPropertyName("author_name")] string? AuthorName,
    [property: JsonPropertyName("thumbnail_url")] string? ThumbnailUrl);

/// <summary>A transient failure. The caller should leave the post retryable.</summary>
public class OEmbedException(string message) : Exception(message);

public class OEmbedClient(HttpClient http, ILogger<OEmbedClient> logger) : IOEmbedClient
{
    public async Task<OEmbedResult?> FetchAsync(
        string platformItemId,
        CancellationToken cancellationToken = default)
    {
        // The share URL in the export (tiktokv.com/share/video/{id}) is rejected here, and
        // the creator handle is not required: the numeric id alone resolves. That matters,
        // because the handle is precisely what this call exists to discover.
        var target = $"https://www.tiktok.com/video/{platformItemId}";
        var requestUri = $"/oembed?url={Uri.EscapeDataString(target)}";

        HttpResponseMessage response;

        try
        {
            response = await http.GetAsync(requestUri, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OEmbedException($"could not reach TikTok: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OEmbedException("TikTok timed out");
        }

        // 400 is what a deleted, private, or region-locked video returns. Terminal, and
        // common enough on an old backlog that it must not read as an error.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound
            or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
        {
            return null;
        }

        if (response.StatusCode is HttpStatusCode.TooManyRequests
            or HttpStatusCode.ServiceUnavailable)
        {
            // Getting blocked partway through is the failure mode that kills this feature.
            // Surface it loudly rather than marking hundreds of live posts unavailable.
            logger.LogWarning("TikTok oEmbed rate-limited: {Status}", (int)response.StatusCode);
            throw new OEmbedException("TikTok is rate-limiting requests; back off and retry later.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new OEmbedException($"TikTok returned {(int)response.StatusCode}");
        }

        return await response.Content.ReadFromJsonAsync<OEmbedResult>(cancellationToken);
    }
}
