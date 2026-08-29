using System.Text.Json;

namespace Recipe.Api.Middleware;

/// <summary>
/// Last-resort handler: turns an unhandled exception into a ProblemDetails-shaped JSON
/// body instead of an empty 500. Services throw domain exceptions and controllers
/// translate them — anything reaching here is a bug, so it is logged at Error.
/// </summary>
public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);

            if (context.Response.HasStarted)
            {
                // Too late to rewrite the response; let the server tear down the connection.
                throw;
            }

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "application/problem+json";

            // Never leak exception detail to a client. The trace id is the join key to the log.
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
                title = "An unexpected error occurred.",
                status = StatusCodes.Status500InternalServerError,
                traceId = context.TraceIdentifier
            }));
        }
    }
}
