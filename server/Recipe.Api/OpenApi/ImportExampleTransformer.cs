using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Recipe.Api.Dtos.Import;

namespace Recipe.Api.OpenApi;

/// <summary>
/// Attaches a worked example to the import request schema so the Swagger UI "Try it out"
/// box opens with a body that actually posts, instead of an empty textarea that fails
/// validation on the required fields.
/// </summary>
public sealed class ImportExampleTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(
        Microsoft.OpenApi.OpenApiSchema schema,
        OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(CreateImportRequest))
        {
            schema.Example = BuildRequestExample();
        }
        else if (context.JsonTypeInfo.Type == typeof(ImportPostDto))
        {
            schema.Example = BuildPostExample();
        }

        return Task.CompletedTask;
    }

    private static JsonNode BuildRequestExample() => new JsonObject
    {
        ["platform"] = "Instagram",
        ["posts"] = new JsonArray(BuildPostExample())
    };

    /// <summary>
    /// Shaped like a real record from an Instagram export: a shortcode rather than a
    /// numeric id, and the same caption twice, which is how carousel slides arrive. The
    /// response should report one stored caption, not two.
    /// </summary>
    private static JsonNode BuildPostExample() => new JsonObject
    {
        ["platformItemId"] = "DbTA2dDk_33",
        ["url"] = "https://www.instagram.com/p/DbTA2dDk_33/",
        ["kind"] = "Post",
        ["captions"] = new JsonArray(
            "Garlic butter pasta\n\n2 tbsp butter\n3 cloves garlic\n200g spaghetti\n\nMelt the butter, add the garlic, toss through the drained pasta.",
            "Garlic butter pasta\n\n2 tbsp butter\n3 cloves garlic\n200g spaghetti\n\nMelt the butter, add the garlic, toss through the drained pasta."),
        ["creatorHandle"] = "examplefoodcreator",
        ["creatorName"] = "Example Food Creator",
        ["hashtags"] = new JsonArray("recipe", "pasta", "easydinner"),
        ["savedAt"] = "2026-08-28T22:22:56Z"
    };
}
