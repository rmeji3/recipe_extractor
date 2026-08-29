namespace Recipe.Api.Models.Recipes;

/// <summary>
/// Where a saved post sits in the extraction cascade. Values are pinned and are a wire
/// contract — see server/CLAUDE.md.
/// </summary>
public enum ExtractionStatus
{
    /// <summary>Not attempted yet.</summary>
    Pending = 0,

    /// <summary>A usable recipe came out of the caption and/or the narration.</summary>
    Extracted = 1,

    /// <summary>
    /// The video narrates too little to work with — the method is on-screen text over
    /// music. Not a failure: it is the signal to route this to the vision model.
    /// </summary>
    NeedsVision = 2,

    /// <summary>The media could not be fetched, or the sidecar errored.</summary>
    Failed = 3,

    /// <summary>Processed, but the content turned out not to be a recipe at all.</summary>
    NotARecipe = 4
}
