using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;

namespace Recipe.Api.Services.Auth;

/// <summary>Verifies the identity token Sign in with Apple hands the app.</summary>
public interface IAppleTokenValidator
{
    /// <summary>
    /// Returns the Apple subject and, when Apple included it, the email.
    /// Throws <see cref="AppleTokenException"/> when the token is not valid for this app.
    /// </summary>
    Task<AppleIdentity> ValidateAsync(string identityToken, CancellationToken cancellationToken = default);
}

/// <param name="Subject">Apple's stable per-app user id.</param>
/// <param name="Email">Present only when Apple chose to include it. May be a relay address.</param>
public record AppleIdentity(string Subject, string? Email);

public class AppleTokenException(string message) : Exception(message);

/// <summary>
/// Validates against Apple's published signing keys.
/// </summary>
/// <remarks>
/// The whole point is to verify the signature. A client can put anything in a token body,
/// so trusting the <c>sub</c> claim without checking Apple actually signed it — and that
/// the token was issued for *this* app — would let anyone sign in as anyone.
/// </remarks>
public class AppleTokenValidator(
    HttpClient http,
    IConfiguration configuration,
    ILogger<AppleTokenValidator> logger) : IAppleTokenValidator
{
    private const string Issuer = "https://appleid.apple.com";
    private const string KeysPath = "/auth/keys";

    private static readonly JwtSecurityTokenHandler Handler = new();

    // Apple rotates its signing keys, so this cannot be fetched once and kept forever;
    // equally it must not be fetched per request. An hour is Apple's own guidance.
    private static readonly SemaphoreSlim KeyLock = new(1, 1);
    private static JsonWebKeySet? _keys;
    private static DateTimeOffset _keysFetchedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan KeyLifetime = TimeSpan.FromHours(1);

    public async Task<AppleIdentity> ValidateAsync(
        string identityToken,
        CancellationToken cancellationToken = default)
    {
        var audience = configuration["Auth:Apple:ClientId"];

        if (string.IsNullOrWhiteSpace(audience))
        {
            // Refusing is the only safe response: without the expected audience a token
            // minted for a different app would pass every other check.
            throw new AppleTokenException("Sign in with Apple is not configured on this server.");
        }

        var keys = await GetKeysAsync(cancellationToken);

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        try
        {
            var principal = Handler.ValidateToken(identityToken, parameters, out _);

            var subject = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new AppleTokenException("Apple's token carried no subject.");
            }

            return new AppleIdentity(subject, principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value);
        }
        catch (SecurityTokenException ex)
        {
            // Deliberately vague to the caller: which check failed is useful to an attacker
            // and useless to a legitimate client. The detail goes to the log.
            logger.LogWarning(ex, "Rejected an Apple identity token");
            throw new AppleTokenException("That sign-in could not be verified.");
        }
    }

    private async Task<JsonWebKeySet> GetKeysAsync(CancellationToken cancellationToken)
    {
        if (_keys is not null && DateTimeOffset.UtcNow - _keysFetchedAt < KeyLifetime)
        {
            return _keys;
        }

        await KeyLock.WaitAsync(cancellationToken);

        try
        {
            if (_keys is not null && DateTimeOffset.UtcNow - _keysFetchedAt < KeyLifetime)
            {
                return _keys;
            }

            var json = await http.GetStringAsync(KeysPath, cancellationToken);
            _keys = new JsonWebKeySet(json);
            _keysFetchedAt = DateTimeOffset.UtcNow;

            return _keys;
        }
        catch (HttpRequestException ex)
        {
            // Serve stale keys rather than locking every user out over a blip. Apple's keys
            // are long-lived, so a slightly old set is far better than a failed sign-in.
            if (_keys is not null)
            {
                logger.LogWarning(ex, "Could not refresh Apple signing keys; using the cached set");
                return _keys;
            }

            logger.LogError(ex, "Could not fetch Apple signing keys");
            throw new AppleTokenException("Apple's sign-in service is unreachable.");
        }
        finally
        {
            KeyLock.Release();
        }
    }
}
