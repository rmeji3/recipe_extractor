namespace Recipe.Api.Models.Import;

/// <summary>
/// Shape of the saved item, derived from the URL path segment at parse time. A ranking
/// hint for classification, never a hard filter — carousels carry real recipes.
/// Values are pinned; see server/CLAUDE.md.
/// </summary>
public enum SavedPostKind
{
    Unknown = 0,
    /// <summary>Instagram <c>/p/</c> — a single image or a carousel.</summary>
    Post = 1,
    /// <summary>Instagram <c>/reel/</c>.</summary>
    Reel = 2,
    /// <summary>TikTok video.</summary>
    Video = 3,

    /// <summary>
    /// TikTok photo post — an image slideshow. A real recipe format, not an edge case:
    /// the layout suits a typed ingredient list, so these often carry exact measurements
    /// the video posts do not.
    /// </summary>
    Photo = 4
}
