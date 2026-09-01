using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Data.App;
using Recipe.Api.Dtos.Auth;
using Recipe.Api.Models.Auth;

namespace Recipe.Api.Services.Auth;

public interface IAuthService
{
    /// <summary>Exchanges an Apple identity token for this app's own tokens.</summary>
    Task<AuthTokensDto> SignInWithAppleAsync(AppleSignInRequest request, CancellationToken cancellationToken = default);

    /// <summary>Exchanges a refresh token for a new pair, revoking the old one.</summary>
    Task<AuthTokensDto> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Revokes one refresh token. Access tokens remain valid until they expire.</summary>
    Task SignOutAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<UserDto> GetAsync(string userId, CancellationToken cancellationToken = default);
}

public class AuthService(
    AppDbContext db,
    IAppleTokenValidator apple,
    IConfiguration configuration,
    TimeProvider timeProvider,
    ILogger<AuthService> logger) : IAuthService
{
    /// <summary>
    /// Short, because an access token cannot be revoked — it is trusted until it expires.
    /// The refresh token is the thing that can be taken away.
    /// </summary>
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(1);

    /// <summary>Long enough that a person who cooks weekly is never signed out.</summary>
    private static readonly TimeSpan RefreshLifetime = TimeSpan.FromDays(60);

    public async Task<AuthTokensDto> SignInWithAppleAsync(
        AppleSignInRequest request,
        CancellationToken cancellationToken = default)
    {
        AppleIdentity identity;

        try
        {
            identity = await apple.ValidateAsync(request.IdentityToken, cancellationToken);
        }
        catch (AppleTokenException ex)
        {
            throw new DomainValidationException(ex.Message);
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;

        var user = await db.Users
            .FirstOrDefaultAsync(u => u.AppleSubject == identity.Subject, cancellationToken);

        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                AppleSubject = identity.Subject,
                Email = identity.Email,
                DisplayName = Clean(request.DisplayName),
                CreatedAt = now,
                LastSeenAt = now
            };
            db.Users.Add(user);
            logger.LogInformation("Created user {UserId}", user.Id);
        }
        else
        {
            // Apple sends the name and email on the first authorization only. If they were
            // missed then — a reinstall, a failed first request — this is the one chance to
            // pick them up, so fill gaps but never overwrite what is already known.
            user.Email ??= identity.Email;
            user.DisplayName ??= Clean(request.DisplayName);
            user.LastSeenAt = now;
        }

        var tokens = await IssueAsync(user, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return tokens;
    }

    public async Task<AuthTokensDto> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var hash = Hash(refreshToken);

        var stored = await db.RefreshTokens
            .Include(t => t.User)
            .FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null || !stored.IsActive(now) || stored.User is null)
        {
            // Same message for unknown, expired, and already-used, so the response cannot
            // be used to probe which tokens exist.
            throw new DomainValidationException("That session is no longer valid. Sign in again.");
        }

        // Rotate: this token is spent the moment it is used.
        stored.RevokedAt = now;
        stored.User.LastSeenAt = now;

        var tokens = await IssueAsync(stored.User, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        return tokens;
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = Hash(refreshToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is not null && stored.RevokedAt is null)
        {
            stored.RevokedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Silent either way. Signing out something already gone is not an error worth
        // reporting, and reporting it would leak which tokens are real.
    }

    public async Task<UserDto> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(userId, out var id))
        {
            throw new KeyNotFoundException("No such user.");
        }

        return await db.Users
            .Where(u => u.Id == id)
            .Select(u => new UserDto(u.Id, u.Email, u.DisplayName, u.CreatedAt))
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("No such user.");
    }

    private async Task<AuthTokensDto> IssueAsync(User user, DateTime now, CancellationToken cancellationToken)
    {
        var refresh = CreateRefreshToken();

        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            TokenHash = Hash(refresh),
            CreatedAt = now,
            ExpiresAt = now.Add(RefreshLifetime)
        });

        await PruneAsync(user.Id, now, cancellationToken);

        return new AuthTokensDto(
            AccessToken: CreateAccessToken(user, now),
            ExpiresIn: (int)AccessLifetime.TotalSeconds,
            RefreshToken: refresh,
            User: new UserDto(user.Id, user.Email, user.DisplayName, user.CreatedAt));
    }

    /// <summary>Drops tokens that expired a while ago, so the table cannot grow forever.</summary>
    private async Task PruneAsync(Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var cutoff = now.AddDays(-30);

        var stale = await db.RefreshTokens
            .Where(t => t.UserId == userId && t.ExpiresAt < cutoff)
            .ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            db.RefreshTokens.RemoveRange(stale);
        }
    }

    private string CreateAccessToken(User user, DateTime now)
    {
        var key = configuration["Auth:Jwt:Key"];

        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
        {
            // Loud and immediate. A short or missing signing key means anyone can mint
            // tokens, and that must never be something the server shrugs off at runtime.
            throw new InvalidOperationException(
                "Auth:Jwt:Key is missing or shorter than 32 characters.");
        }

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: configuration["Auth:Jwt:Issuer"] ?? "recipe-api",
            audience: configuration["Auth:Jwt:Audience"] ?? "recipe-app",
            claims:
            [
                // Every service reads the user from NameIdentifier; keep that the user id.
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            notBefore: now,
            expires: now.Add(AccessLifetime),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string CreateRefreshToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=');

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string? Clean(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, 128)];
    }
}
