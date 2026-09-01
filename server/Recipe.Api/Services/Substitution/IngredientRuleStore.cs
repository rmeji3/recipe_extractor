using System.Text.Json;
using System.Text.Json.Serialization;
using Recipe.Api.Models.Substitution;

namespace Recipe.Api.Services.Substitution;

/// <summary>The curated substitution table.</summary>
public interface IIngredientRuleStore
{
    Task<IReadOnlyList<IngredientRule>> GetAllAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Loads the rules from a JSON file shipped with the app.
/// </summary>
/// <remarks>
/// A file rather than a table, for now. The rules are curated by hand and change when
/// someone edits them, not when a user does anything — so they version with the code and
/// review like code, which is the point. Ids are derived from the canonical name so they
/// stay stable across restarts and can be stored on a modification.
/// </remarks>
public class FileIngredientRuleStore(IWebHostEnvironment environment, ILogger<FileIngredientRuleStore> logger)
    : IIngredientRuleStore
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private IReadOnlyList<IngredientRule>? _cached;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<IngredientRule>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var path = Path.Combine(environment.ContentRootPath, "Data", "Seed", "IngredientRules.json");

            if (!File.Exists(path))
            {
                logger.LogError("Ingredient rules not found at {Path}; substitution is disabled", path);
                return _cached = [];
            }

            await using var stream = File.OpenRead(path);
            var raw = await JsonSerializer.DeserializeAsync<List<RuleFile>>(stream, Options, cancellationToken);

            _cached = [.. (raw ?? []).Select(r => new IngredientRule
            {
                // Deterministic from the name, so a rule id recorded on a modification
                // still resolves after a restart or a redeploy.
                Id = DeterministicId(r.Canonical),
                Canonical = r.Canonical,
                Aliases = r.Aliases ?? [],
                Function = r.Function,
                Substitutions = r.Substitutions ?? []
            })];

            logger.LogInformation("Loaded {Count} ingredient substitution rules", _cached.Count);

            return _cached;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static Guid DeterministicId(string canonical)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes(canonical.ToLowerInvariant()));
        return new Guid(bytes);
    }

    private record RuleFile(
        string Canonical,
        List<string>? Aliases,
        IngredientFunction Function,
        List<Models.Substitution.Substitution>? Substitutions);
}
