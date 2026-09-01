namespace Recipe.Api.Models.Auth;

/// <summary>
/// A long-lived credential the app exchanges for short-lived access tokens.
/// </summary>
/// <remarks>
/// Only a hash is stored. A refresh token is a bearer credential for months, so a database
/// leak must not hand anyone a working session — the same reason passwords are not stored
/// in the clear.
///
/// Rotated on every use: a stolen token is usable at most once before the real client's
/// next refresh invalidates it and makes the theft visible.
/// </remarks>
public class RefreshToken
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }
    public User? User { get; set; }

    /// <summary>SHA-256 of the token. The token itself is never persisted.</summary>
    public required string TokenHash { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when rotated or signed out. A revoked token is kept as evidence, not deleted.</summary>
    public DateTime? RevokedAt { get; set; }

    public bool IsActive(DateTime now) => RevokedAt is null && ExpiresAt > now;
}
