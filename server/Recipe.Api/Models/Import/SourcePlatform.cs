namespace Recipe.Api.Models.Import;

/// <summary>
/// Where a saved post came from. Values are pinned and are a wire contract — see
/// server/CLAUDE.md. Never reorder or reuse a number.
/// </summary>
public enum SourcePlatform
{
    Instagram = 1,
    TikTok = 2
}
