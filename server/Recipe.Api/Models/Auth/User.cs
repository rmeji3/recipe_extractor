namespace Recipe.Api.Models.Auth;

/// <summary>
/// A person. Identified to the rest of the system by <see cref="Id"/> as a string, which
/// is what every other table's <c>UserId</c> holds.
/// </summary>
public class User
{
    public Guid Id { get; set; }

    /// <summary>
    /// Apple's stable subject claim for this user and this app. The real identity key:
    /// email can be a private relay address and can change, this cannot.
    /// </summary>
    public required string AppleSubject { get; set; }

    /// <summary>
    /// Apple returns this on the first authorization only, and it may be a private relay
    /// address. Never treat it as a login handle or assume it is reachable.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>Also first-authorization-only, so it is captured when offered and kept.</summary>
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime LastSeenAt { get; set; }

    public ICollection<RefreshToken> RefreshTokens { get; set; } = [];
}
