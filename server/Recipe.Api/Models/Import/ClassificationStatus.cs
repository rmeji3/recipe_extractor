namespace Recipe.Api.Models.Import;

/// <summary>
/// Whether a saved post has been judged to be a recipe. Values are pinned and are a wire
/// contract — see server/CLAUDE.md.
/// </summary>
public enum ClassificationStatus
{
    /// <summary>Not judged yet.</summary>
    Pending = 0,

    /// <summary>
    /// Confidently food. Extract automatically — this is the tier the user sees first.
    /// </summary>
    Food = 1,

    /// <summary>
    /// Could go either way. Goes to a review pile with thumbnails for bulk approval rather
    /// than being imported or discarded.
    /// </summary>
    Uncertain = 2,

    /// <summary>
    /// Not food. Never imported, but kept visible under "skipped" — that list is the
    /// safety valve that makes it safe to tune for precision.
    /// </summary>
    NotFood = 3,

    /// <summary>Nothing to judge: no caption and no metadata to work from.</summary>
    Unclassifiable = 4
}
