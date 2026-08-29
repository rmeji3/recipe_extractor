namespace Recipe.Api.Common;

/// <summary>
/// A single page of results plus the metadata a client needs to page through the rest.
/// Build one with <see cref="CreateAsync"/> so paging is applied in the database,
/// never in memory.
/// </summary>
public sealed record PaginatedResult<T>(
    IReadOnlyList<T> Items,
    int PageNumber,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public const int MaxPageSize = 100;

    /// <summary>
    /// Counts and pages <paramref name="query"/> in the database. Call this on a query
    /// that has already been projected to a DTO, not on an entity query.
    /// </summary>
    public static async Task<PaginatedResult<T>> CreateAsync(
        IQueryable<T> query,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        pageNumber = pageNumber < 1 ? 1 : pageNumber;
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PaginatedResult<T>(items, pageNumber, pageSize, totalCount);
    }
}
