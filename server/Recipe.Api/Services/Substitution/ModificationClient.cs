using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Recipe.Api.Services.Substitution;

/// <summary>Asks the model to choose among substitutions that are already known-good.</summary>
public interface IModificationClient
{
    Task<ModificationSelection> SelectAsync(
        ModificationPrompt prompt, CancellationToken cancellationToken = default);
}

/// <param name="Candidates">
/// The only swaps the model may choose from. Everything outside this list is discarded
/// downstream, so the prompt is a selection task rather than a generation one.
/// </param>
public record ModificationPrompt(
    string Title,
    string Goal,
    string? UserNotes,
    IReadOnlyList<SubstitutionCandidates.Candidate> Candidates);

public record ModificationSelection(List<ProposedChange> Changes, string Summary);

public record ProposedChange(string From, string To);

public class ModificationClient(HttpClient http, ILogger<ModificationClient> logger) : IModificationClient
{
    public async Task<ModificationSelection> SelectAsync(
        ModificationPrompt prompt,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            title = prompt.Title,
            goal = prompt.Goal,
            user_notes = prompt.UserNotes,
            candidates = prompt.Candidates.Select(c => new
            {
                ingredient = c.Ingredient.Item,
                quantity = c.Ingredient.Quantity,
                unit = c.Ingredient.Unit,
                options = c.Options.Select(o => new
                {
                    replacement = o.Replacement,
                    effect = o.Effect,
                    note = o.Note,
                    // Flagged so the model can prefer what this person can use tonight.
                    already_cooks_with = o.InCorpus,
                    in_pantry_now = o.InPantry,
                    tags = o.Tags
                })
            })
        };

        try
        {
            var response = await http.PostAsJsonAsync("/modify", payload, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Modification service returned {Status}", (int)response.StatusCode);
                return new ModificationSelection([], "The substitution service could not be reached.");
            }

            var body = await response.Content.ReadFromJsonAsync<RawSelection>(cancellationToken);

            return new ModificationSelection(
                [.. (body?.Changes ?? []).Select(c => new ProposedChange(c.From, c.To))],
                body?.Summary ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // No changes rather than a failure: the recipe is untouched and the user can
            // retry. Nothing has been altered.
            logger.LogError(ex, "Modification service unavailable at {BaseAddress}", http.BaseAddress);
            return new ModificationSelection([], "The substitution service is unavailable.");
        }
    }

    private record RawSelection(
        [property: JsonPropertyName("changes")] List<RawChange> Changes,
        [property: JsonPropertyName("summary")] string? Summary);

    private record RawChange(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string To);
}
