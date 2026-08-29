using Recipe.Api.Models.Import;

namespace Recipe.Api.Dtos.Import;

/// <summary>What a raw export file yielded before anything was stored.</summary>
/// <param name="Platform">Detected from the file's shape, not from its name.</param>
/// <param name="Posts">Normalised posts, ready for the import service.</param>
/// <param name="SkippedCount">
/// Records the parser could not use — no permalink, an unrecognised URL shape, or a
/// malformed entry. Worth surfacing: a high count means the export layout has drifted.
/// </param>
public record ExportParseResult(
    SourcePlatform Platform,
    List<ImportPostDto> Posts,
    int SkippedCount);
