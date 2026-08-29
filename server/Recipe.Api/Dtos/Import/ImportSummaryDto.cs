using Recipe.Api.Models.Import;

namespace Recipe.Api.Dtos.Import;

/// <summary>
/// Result of an import. <c>ImportedCount</c> is the number to surface to the user —
/// "214 recipes found in 786 saves" is the moment the import earns its keep.
/// </summary>
public record ImportSummaryDto(
    Guid Id,
    SourcePlatform Platform,
    int SubmittedCount,
    int ImportedCount,
    int DuplicateCount,
    DateTime CreatedAt)
{
    /// <summary>
    /// Records in an uploaded export file the parser could not use. Null when the batch
    /// was posted as JSON rather than parsed from a file.
    /// </summary>
    public int? SkippedCount { get; init; }
}
