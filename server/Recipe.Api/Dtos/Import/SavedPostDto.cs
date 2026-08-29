using Recipe.Api.Models.Import;

namespace Recipe.Api.Dtos.Import;

public record SavedPostDto(
    Guid Id,
    SourcePlatform Platform,
    string PlatformItemId,
    string Url,
    SavedPostKind Kind,
    string? Caption,
    string? CreatorHandle,
    string? CreatorName,
    List<string> Hashtags,
    DateTime? SavedAt,
    DateTime CreatedAt,
    MetadataStatus MetadataStatus,
    string? ThumbnailUrl,
    ClassificationStatus ClassificationStatus,
    /// <summary>Drives the ranked queue — highest confidence is what the user sees first.</summary>
    double FoodConfidence,
    string? ClassifiedBy);
