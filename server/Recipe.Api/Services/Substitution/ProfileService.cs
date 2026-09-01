using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Models.Substitution;

namespace Recipe.Api.Services.Substitution;

public interface IProfileService
{
    Task<UserProfileDto> GetAsync(string userId, CancellationToken cancellationToken = default);

    Task<UserProfileDto> UpdateAsync(
        string userId, UpdateProfileRequest request, CancellationToken cancellationToken = default);
}

public class ProfileService(AppDbContext db, TimeProvider timeProvider) : IProfileService
{
    public async Task<UserProfileDto> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var profile = await db.UserProfiles
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        // Defaults rather than a 404: "no preferences yet" is a valid state, and the app
        // should not have to special-case a user who has never opened the settings screen.
        return profile is null
            ? new UserProfileDto(DietaryPattern.None, [], [], null)
            : new UserProfileDto(profile.Diet, profile.Avoid, profile.Goals, profile.Notes);
    }

    public async Task<UserProfileDto> UpdateAsync(
        string userId,
        UpdateProfileRequest request,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var profile = await db.UserProfiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);

        if (profile is null)
        {
            profile = new UserProfile { Id = Guid.NewGuid(), UserId = userId, CreatedAt = now };
            db.UserProfiles.Add(profile);
        }

        profile.Diet = request.Diet;
        profile.Avoid = [.. Clean(request.Avoid)];
        profile.Goals = [.. Clean(request.Goals)];
        profile.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        profile.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        return new UserProfileDto(profile.Diet, profile.Avoid, profile.Goals, profile.Notes);
    }

    private static IEnumerable<string> Clean(IEnumerable<string> values) =>
        values.Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct();
}
