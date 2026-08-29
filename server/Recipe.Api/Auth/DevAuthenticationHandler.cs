using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Recipe.Api.Auth;

/// <summary>
/// Authenticates every request as a fixed local user so the Swagger UI and the test suite
/// can exercise authorized endpoints without a token.
/// </summary>
/// <remarks>
/// **Never registered outside Development and Testing** — see the environment guard in
/// Program.cs. Registering this in production would make every endpoint public.
/// Send an <c>X-Dev-User</c> header to act as a different user id, which is how the
/// tests check that one user cannot read another's imports.
/// </remarks>
public class DevAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DevAuth";
    public const string UserHeader = "X-Dev-User";
    public const string DefaultUserId = "dev-user";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var userId = Request.Headers.TryGetValue(UserHeader, out var header) && !string.IsNullOrWhiteSpace(header)
            ? header.ToString()
            : DefaultUserId;

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, userId)],
            SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
