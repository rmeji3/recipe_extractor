using System.ComponentModel.DataAnnotations;

namespace Recipe.Api.Dtos.Auth;

/// <summary>What the Expo client sends after Sign in with Apple succeeds on device.</summary>
public record AppleSignInRequest
{
    /// <summary>
    /// The JWT Apple returns as <c>identityToken</c>. Verified server-side against Apple's
    /// published signing keys — a client could put anything in the body otherwise.
    /// </summary>
    [Required, StringLength(4096, MinimumLength = 20)]
    public required string IdentityToken { get; init; }

    /// <summary>
    /// Apple hands the name to the app on the first authorization only, and never again.
    /// Send it when present; it cannot be recovered later.
    /// </summary>
    [StringLength(128)]
    public string? DisplayName { get; init; }
}

public record RefreshRequest
{
    [Required, StringLength(256, MinimumLength = 20)]
    public required string RefreshToken { get; init; }
}

/// <param name="AccessToken">Bearer token for the API. Short-lived and not revocable.</param>
/// <param name="ExpiresIn">Seconds until the access token expires.</param>
/// <param name="RefreshToken">
/// Exchange for a new pair. Rotated on every use, so store the newest and discard the old.
/// </param>
/// <param name="User">Who was signed in.</param>
public record AuthTokensDto(string AccessToken, int ExpiresIn, string RefreshToken, UserDto User);

public record UserDto(Guid Id, string? Email, string? DisplayName, DateTime CreatedAt);
