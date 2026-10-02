using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Recipe.Api.Common;

/// <summary>
/// Request limits, partitioned per user.
/// </summary>
/// <remarks>
/// The thing being protected is not the server — it is the bill. Accepting a request is
/// cheap; what it queues is not. A single client looping on <c>from-url</c> can enqueue
/// hundreds of fetches, transcriptions, and model calls in a minute, and the queue worker
/// will dutifully pay for every one.
///
/// So the tight limit sits on the endpoints that commission paid work, and everything else
/// gets a loose ceiling that only catches a runaway client.
/// </remarks>
public static class RateLimiting
{
    /// <summary>Endpoints that commission a model call or a media fetch.</summary>
    public const string Extraction = "extraction";

    /// <summary>Everything else.</summary>
    public const string Default = "default";

    public static IServiceCollection AddAppRateLimiting(
        this IServiceCollection services, IConfiguration configuration, IWebHostEnvironment environment)
    {
        // Tests drive hundreds of requests through one fixture in seconds. Limiting them
        // would produce flakes that look like product bugs.
        var enabled = !environment.IsEnvironment("Testing")
                      && configuration.GetValue("RateLimits:Enabled", true);

        var extractionPerHour = configuration.GetValue("RateLimits:ExtractionPerHour", 40);
        var requestsPerMinute = configuration.GetValue("RateLimits:RequestsPerMinute", 300);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, token) =>
            {
                // Tell the client when to come back. Without this a well-behaved app has to
                // guess, and guessing badly is how a retry loop becomes a hammer.
                //
                // The limiter only supplies this metadata for some window types, so there
                // is a fallback: a sliding window frees a segment at a time, so the worst
                // wait is one segment. Erring long is right — an early retry is another
                // rejection.
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? (int)retryAfter.TotalSeconds
                    : RetryAfterFallback(context.HttpContext);

                context.HttpContext.Response.Headers.RetryAfter =
                    Math.Max(1, seconds).ToString(CultureInfo.InvariantCulture);

                context.HttpContext.Response.ContentType = "application/problem+json";

                await context.HttpContext.Response.WriteAsync(
                    """
                    {"title":"Too many requests.","status":429,
                     "detail":"You are doing that faster than we can process it. Try again shortly."}
                    """,
                    token);
            };

            options.AddPolicy(Extraction, context => enabled
                ? RateLimitPartition.GetSlidingWindowLimiter(
                    PartitionKey(context),
                    _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = extractionPerHour,
                        Window = TimeSpan.FromHours(1),
                        // Sliding rather than fixed: a fixed window lets someone spend the
                        // whole hour's budget twice across the boundary.
                        SegmentsPerWindow = ExtractionSegments,
                        QueueLimit = 0
                    })
                : RateLimitPartition.GetNoLimiter(PartitionKey(context)));

            options.AddPolicy(Default, context => enabled
                ? RateLimitPartition.GetFixedWindowLimiter(
                    PartitionKey(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = requestsPerMinute,
                        Window = TimeSpan.FromMinutes(1),
                        QueueLimit = 0
                    })
                : RateLimitPartition.GetNoLimiter(PartitionKey(context)));
        });

        return services;
    }

    /// <summary>
    /// How long to wait when the limiter did not say.
    /// </summary>
    /// <remarks>
    /// One segment of the extraction window, or one minute for the general limit. Both are
    /// upper bounds on how long until capacity frees up, which is the safe direction: a
    /// client that retries too early simply gets rejected again.
    /// </remarks>
    private static int RetryAfterFallback(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var policy = endpoint?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;

        return policy == Extraction
            ? (int)TimeSpan.FromHours(1).TotalSeconds / ExtractionSegments
            : 60;
    }

    /// <summary>
    /// Segments the extraction window is divided into. More segments means capacity frees
    /// up more smoothly; fewer means a coarser but cheaper limiter.
    /// </summary>
    private const int ExtractionSegments = 6;

    /// <summary>
    /// Who is being limited.
    /// </summary>
    /// <remarks>
    /// The signed-in user where there is one. Anonymous requests fall back to the remote
    /// address, which is weak — a phone network NATs thousands of people behind one
    /// address — but the only anonymous endpoints are sign-in and health, so the blast
    /// radius is small. Everything expensive requires authentication.
    /// </remarks>
    private static string PartitionKey(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.Connection.RemoteIpAddress?.ToString()
        ?? "anonymous";
}
