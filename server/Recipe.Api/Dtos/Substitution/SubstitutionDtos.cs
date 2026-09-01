using System.ComponentModel.DataAnnotations;
using Recipe.Api.Models.Substitution;

namespace Recipe.Api.Dtos.Substitution;

public record ModifyRequest
{
    /// <summary>
    /// What to change, in the user's words: "make it vegetarian", "healthier",
    /// "higher protein". Matched against the tags on the curated substitution table.
    /// </summary>
    [Required, StringLength(200, MinimumLength = 2)]
    public required string Goal { get; init; }
}

/// <param name="Id">Null when nothing could be proposed — there is no proposal to accept.</param>
/// <param name="Changes">Each traceable to a curated rule. Never anything the model invented.</param>
/// <param name="Warnings">
/// Suggestions that were discarded because nothing backed them. Surfaced rather than
/// hidden: silently dropping them would make the feature look arbitrary.
/// </param>
public record ModificationDto(
    Guid? Id,
    Guid RecipeId,
    string Goal,
    List<AppliedChange> Changes,
    string? Summary,
    List<string> Warnings);

public record UserProfileDto(
    DietaryPattern Diet,
    List<string> Avoid,
    List<string> Goals,
    string? Notes);

public record UpdateProfileRequest
{
    public DietaryPattern Diet { get; init; }

    /// <summary>Allergies and hard dislikes. Enforced as a filter, never a hint.</summary>
    [MaxLength(50)]
    public List<string> Avoid { get; init; } = [];

    /// <summary>Standing aims — "lower-calorie", "higher-protein", "higher-fibre".</summary>
    [MaxLength(20)]
    public List<string> Goals { get; init; } = [];

    [StringLength(1000)]
    public string? Notes { get; init; }
}
