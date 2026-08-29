using Recipe.Api.Common;
using Recipe.Api.Dtos.Import;

namespace Recipe.Api.Services.Import;

public interface IImportService
{
    /// <summary>Stores a batch of saved posts, skipping ones this user already has.</summary>
    Task<ImportSummaryDto> CreateAsync(string userId, CreateImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="KeyNotFoundException"/> when the job is not this user's.</summary>
    Task<ImportSummaryDto> GetAsync(string userId, Guid importId, CancellationToken cancellationToken = default);

    Task<PaginatedResult<ImportSummaryDto>> ListAsync(string userId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);

    /// <summary>Posts stored by one import. Throws <see cref="KeyNotFoundException"/> if it is not this user's.</summary>
    Task<PaginatedResult<SavedPostDto>> ListPostsAsync(string userId, Guid importId, int pageNumber, int pageSize, CancellationToken cancellationToken = default);
}
