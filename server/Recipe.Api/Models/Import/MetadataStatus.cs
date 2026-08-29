namespace Recipe.Api.Models.Import;

/// <summary>
/// Stage 1 state for a saved post. Values are pinned and are a wire contract — see
/// server/CLAUDE.md.
/// </summary>
public enum MetadataStatus
{
    /// <summary>Not fetched yet. Instagram posts skip straight past this — the export carries their captions.</summary>
    Pending = 0,

    /// <summary>Caption and creator are populated.</summary>
    Fetched = 1,

    /// <summary>
    /// The platform no longer serves this video: deleted, private, or region-locked.
    /// A normal outcome on an old backlog — 38% of a 2019-onward sample — and a terminal
    /// one. Do not retry these.
    /// </summary>
    Unavailable = 2,

    /// <summary>A transient failure. Safe to retry.</summary>
    Failed = 3,

    /// <summary>Nothing to fetch: the export already carried everything.</summary>
    NotNeeded = 4
}
