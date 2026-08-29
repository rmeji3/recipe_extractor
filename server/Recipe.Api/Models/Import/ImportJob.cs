namespace Recipe.Api.Models.Import;

/// <summary>One submitted batch of saved posts, and what came of it.</summary>
public class ImportJob
{
    public Guid Id { get; set; }

    public required string UserId { get; set; }

    public SourcePlatform Platform { get; set; }

    /// <summary>Posts in the submitted payload, before any deduplication.</summary>
    public int SubmittedCount { get; set; }

    /// <summary>Posts stored as new rows.</summary>
    public int ImportedCount { get; set; }

    /// <summary>Posts dropped because this user already had that platform item.</summary>
    public int DuplicateCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public ICollection<SavedPost> Posts { get; set; } = [];
}
