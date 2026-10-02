using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Recipe.Api.Services.Auth;

namespace Recipe.Tests;

/// <summary>
/// The Apple token validator, against tokens it actually has to verify.
/// </summary>
/// <remarks>
/// The rest of the auth suite stubs this class out, because Apple's real tokens cannot be
/// minted offline. That left the one piece of security-critical parsing in the system with
/// no coverage at all, and it shipped two bugs: a malformed token surfaced as a 500 rather
/// than a rejected sign-in, and a *valid* token was rejected because the handler renames
/// <c>sub</c> to a WS-Federation URI on the way in.
///
/// Apple's half of the protocol is reproducible without Apple: a local RSA key stands in for
/// their signing key, and the JWKS endpoint is served from the same key. Everything the
/// validator checks — signature, issuer, audience, expiry, claims — is then real.
/// </remarks>
public class AppleTokenValidatorTests
{
    private const string Issuer = "https://appleid.apple.com";
    private const string ClientId = "app.souschef";
    private const string KeyId = "test-signing-key";

    // One key for the class: the validator caches the key set in a static field, so a
    // per-test key would only be honoured for whichever test ran first.
    private static readonly RSA Key = RSA.Create(2048);

    private static string Jwks()
    {
        var parameters = Key.ExportParameters(false);

        return $$"""
            {"keys":[{"kty":"RSA","kid":"{{KeyId}}","use":"sig","alg":"RS256",
              "n":"{{Base64UrlEncoder.Encode(parameters.Modulus!)}}",
              "e":"{{Base64UrlEncoder.Encode(parameters.Exponent!)}}"}]}
            """;
    }

    /// <summary>Mints a token the way Apple would.</summary>
    private static string Token(
        string subject = "001234.abcdef.1234",
        string? email = "someone@privaterelay.appleid.com",
        string audience = ClientId,
        string issuer = Issuer,
        DateTime? expires = null,
        RSA? signedWith = null)
    {
        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, subject) };

        if (email is not null)
        {
            claims.Add(new Claim(JwtRegisteredClaimNames.Email, email));
        }

        var expiry = expires ?? DateTime.UtcNow.AddMinutes(10);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            // Relative to the expiry, so an expired token is still internally coherent —
            // a notBefore after the expiry is refused when the token is built, which
            // would test nothing.
            notBefore: expiry.AddMinutes(-10),
            expires: expiry,
            signingCredentials: new SigningCredentials(
                new RsaSecurityKey(signedWith ?? Key) { KeyId = KeyId },
                SecurityAlgorithms.RsaSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class KeysHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(Jwks(), Encoding.UTF8, "application/json")
            });
    }

    private static AppleTokenValidator Validator(string? clientId = ClientId)
    {
        var http = new HttpClient(new KeysHandler()) { BaseAddress = new Uri(Issuer) };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Apple:ClientId"] = clientId
            })
            .Build();

        return new AppleTokenValidator(http, configuration, NullLogger<AppleTokenValidator>.Instance);
    }

    [Fact]
    public async Task A_properly_signed_token_yields_the_subject_and_email()
    {
        // The regression that mattered: validation succeeded, then the subject came back
        // empty because the handler had renamed the claim.
        var identity = await Validator().ValidateAsync(Token());

        Assert.Equal("001234.abcdef.1234", identity.Subject);
        Assert.Equal("someone@privaterelay.appleid.com", identity.Email);
    }

    [Fact]
    public async Task A_token_without_an_email_is_still_a_valid_sign_in()
    {
        // Apple omits the email when the user has signed in before, or hid it.
        var identity = await Validator().ValidateAsync(Token(email: null));

        Assert.Equal("001234.abcdef.1234", identity.Subject);
        Assert.Null(identity.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.notreal.signature")]
    [InlineData("a.b.c.d.e")]
    public async Task Malformed_input_is_a_rejected_sign_in_not_a_server_error(string token)
    {
        // These fail before any security check runs, throwing a plain ArgumentException.
        await Assert.ThrowsAsync<AppleTokenException>(() => Validator().ValidateAsync(token));
    }

    [Fact]
    public async Task A_token_signed_by_anyone_else_is_refused()
    {
        // The entire point of the class. Anyone can write a token body claiming any
        // subject; only Apple can sign one.
        using var attacker = RSA.Create(2048);

        await Assert.ThrowsAsync<AppleTokenException>(
            () => Validator().ValidateAsync(Token(signedWith: attacker)));
    }

    [Fact]
    public async Task A_token_minted_for_a_different_app_is_refused()
    {
        // A valid Apple token from some other developer's app would otherwise pass every
        // check, and sign its bearer in as whoever they liked here.
        await Assert.ThrowsAsync<AppleTokenException>(
            () => Validator().ValidateAsync(Token(audience: "com.someone.else")));
    }

    [Fact]
    public async Task A_token_from_a_different_issuer_is_refused()
    {
        await Assert.ThrowsAsync<AppleTokenException>(
            () => Validator().ValidateAsync(Token(issuer: "https://evil.example.com")));
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        // Beyond the two minutes of clock skew the validator allows.
        await Assert.ThrowsAsync<AppleTokenException>(
            () => Validator().ValidateAsync(Token(expires: DateTime.UtcNow.AddMinutes(-10))));
    }

    [Fact]
    public async Task An_unconfigured_server_refuses_rather_than_skipping_the_audience_check()
    {
        // Without the expected audience there is nothing to check the token against, and
        // any app's token would do. Refusing is the only safe response.
        var error = await Assert.ThrowsAsync<AppleTokenException>(
            () => Validator(clientId: null).ValidateAsync(Token()));

        Assert.Contains("not configured", error.Message);
    }
}
