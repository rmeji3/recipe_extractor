using System.ComponentModel.DataAnnotations;

namespace Recipe.Api.Dtos.Import;

/// <param name="Approve">Posts that really are recipes. Extraction is queued for each.</param>
/// <param name="Reject">Posts that are not. They stay visible under "skipped".</param>
public record ReviewDecisionRequest
{
    [MaxLength(200)]
    public List<Guid> Approve { get; init; } = [];

    [MaxLength(200)]
    public List<Guid> Reject { get; init; } = [];
}

public record ReviewResultDto(int Approved, int Rejected, int RemainingToReview);
