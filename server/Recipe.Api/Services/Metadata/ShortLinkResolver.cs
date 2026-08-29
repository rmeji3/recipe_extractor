using Recipe.Api.Common;

namespace Recipe.Api.Services.Metadata;

/// <summary>
/// Follows a share-sheet short link to the post it points at.
/// </summary>
/// <remarks>
/// The iOS share sheet hands out <c>vm.tiktok.com/…</c> and <c>tiktok.com/t/…</c>, neither
/// of which contains the video id. Since the share sheet is the app's primary way in, this
/// runs on the hot path for most single-video requests.
/// </remarks>
public interface IShortLinkResolver
{
    /// <summary>Returns the URL unchanged when it is already parseable.</summary>
    Task<string> ResolveAsync(string url, CancellationToken cancellationToken = default);
}

public class ShortLinkResolver(HttpClient http, ILogger<ShortLinkResolver> logger) : IShortLinkResolver
{
    public async Task<string> ResolveAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!PostUrl.IsShortLink(url))
        {
            return url;
        }

        try
        {
            // HEAD is enough to learn the destination and avoids pulling a page body.
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await http.SendAsync(request, cancellationToken);

            // The handler follows redirects, so the final URI is on the request message.
            var resolved = response.RequestMessage?.RequestUri?.ToString();

            if (!string.IsNullOrWhiteSpace(resolved) && PostUrl.TryParse(resolved, out _))
            {
                return resolved;
            }

            logger.LogWarning("Short link {Url} resolved to something unparseable: {Resolved}",
                url, resolved);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not follow short link {Url}", url);
        }

        // Hand back the original and let the caller report that it could not be read.
        return url;
    }
}
