using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recipe.Api.Services.Classification;

/// <summary>Batched food/not-food judgement, via the Python sidecar.</summary>
public interface IClassifierClient
{
    Task<IReadOnlyList<ClassifierVerdict>> ClassifyAsync(
        IReadOnlyList<ClassifierItem> items, CancellationToken cancellationToken = default);
}

public record ClassifierItem(string? Caption, string? CreatorHandle);

public record ClassifierVerdict(bool IsFood, double Confidence);

public class ClassifierClient(HttpClient http, ILogger<ClassifierClient> logger) : IClassifierClient
{
    public async Task<IReadOnlyList<ClassifierVerdict>> ClassifyAsync(
        IReadOnlyList<ClassifierItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var payload = new
        {
            items = items.Select(i => new { caption = i.Caption, creator_handle = i.CreatorHandle })
        };

        try
        {
            var response = await http.PostAsJsonAsync("/classify", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Classifier returned {Status}", (int)response.StatusCode);
                return [];
            }

            var body = await response.Content.ReadFromJsonAsync<ClassifyResponse>(cancellationToken);

            return [.. (body?.Verdicts ?? []).Select(v => new ClassifierVerdict(v.IsFood, v.Confidence))];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Returning nothing leaves the batch Pending, so it is retried rather than
            // being written off as not-food.
            logger.LogError(ex, "Classifier unavailable at {BaseAddress}", http.BaseAddress);
            return [];
        }
    }

    private record ClassifyResponse(
        [property: JsonPropertyName("verdicts")] List<RawVerdict> Verdicts);

    private record RawVerdict(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("is_food")] bool IsFood,
        [property: JsonPropertyName("confidence")] double Confidence);
}
