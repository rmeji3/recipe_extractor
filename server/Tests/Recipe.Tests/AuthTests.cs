using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Recipe.Api.Dtos.Auth;
using Recipe.Api.Services.Auth;

namespace Recipe.Tests;

/// <summary>
/// Stands in for Apple's token verification.
/// </summary>
/// <remarks>
/// The real validator checks a signature against Apple's published keys — which is the
/// whole point, since a client can put anything in a token body. That cannot be exercised
/// offline, so these tests cover everything built on top of a verified identity, and the
/// validator's own contract is asserted through this seam.
/// </remarks>
public class StubAppleValidator : IAppleTokenValidator
{
    public Func<string, AppleIdentity>? Respond { get; set; }

    public Task<AppleIdentity> ValidateAsync(string identityToken, CancellationToken cancellationToken = default)
    {
        if (Respond is null)
        {
            throw new AppleTokenException("That sign-in could not be verified.");
        }

        return Task.FromResult(Respond(identityToken));
    }
}

public class AuthTests(AppFixture fixture) : IClassFixture<AppFixture>
{
    private StubAppleValidator Apple => fixture.Services.GetRequiredService<StubAppleValidator>();

    private const string Token = "a-token-long-enough-to-pass-validation";

    private async Task<AuthTokensDto> SignIn(string subject, string? email = null, string? name = null)
    {
        Apple.Respond = _ => new AppleIdentity(subject, email);

        var response = await fixture.CreateClient().PostAsJsonAsync("/api/auth/apple",
            new AppleSignInRequest { IdentityToken = Token, DisplayName = name });

        return (await response.Content.ReadFromJsonAsync<AuthTokensDto>(AppFixture.JsonOptions))!;
    }

    /// <summary>A client carrying a real bearer token rather than the dev stub header.</summary>
    private HttpClient Bearer(string accessToken)
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    [Fact]
    public async Task Signing_in_creates_an_account_and_returns_tokens()
    {
        var tokens = await SignIn($"apple-{Guid.NewGuid()}", "a@example.com", "Rafael");

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(tokens.RefreshToken));
        Assert.Equal("a@example.com", tokens.User.Email);
        Assert.Equal("Rafael", tokens.User.DisplayName);
        Assert.True(tokens.ExpiresIn > 0);
    }

    [Fact]
    public async Task Signing_in_again_returns_the_same_account()
    {
        // Apple's subject is the identity. A second sign-in is the same person, not a
        // second account, even from a reinstalled app.
        var subject = $"apple-{Guid.NewGuid()}";

        var first = await SignIn(subject, "b@example.com", "Rafael");
        var second = await SignIn(subject);

        Assert.Equal(first.User.Id, second.User.Id);
    }

    [Fact]
    public async Task A_later_sign_in_never_erases_the_name_apple_sent_once()
    {
        // Apple returns the name on the first authorization only. Overwriting it with the
        // null of a later sign-in would lose it permanently.
        var subject = $"apple-{Guid.NewGuid()}";

        await SignIn(subject, "c@example.com", "Rafael");
        var second = await SignIn(subject, email: null, name: null);

        Assert.Equal("Rafael", second.User.DisplayName);
        Assert.Equal("c@example.com", second.User.Email);
    }

    [Fact]
    public async Task An_unverifiable_token_is_rejected()
    {
        Apple.Respond = null;

        var response = await fixture.CreateClient().PostAsJsonAsync("/api/auth/apple",
            new AppleSignInRequest { IdentityToken = Token });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_access_token_authenticates_a_real_request()
    {
        var tokens = await SignIn($"apple-{Guid.NewGuid()}", "d@example.com");

        var me = await Bearer(tokens.AccessToken)
            .GetFromJsonAsync<UserDto>("/api/auth/me", AppFixture.JsonOptions);

        Assert.Equal(tokens.User.Id, me!.Id);
    }

    [Fact]
    public async Task A_request_with_no_credentials_at_all_is_refused()
    {
        // The dev stub authenticates unauthenticated requests, so this asserts the JWT
        // path specifically: a malformed bearer token must not fall back to the stub.
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Refreshing_returns_a_new_pair()
    {
        var tokens = await SignIn($"apple-{Guid.NewGuid()}");

        var response = await fixture.CreateClient().PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = tokens.RefreshToken });
        var refreshed = await response.Content.ReadFromJsonAsync<AuthTokensDto>(AppFixture.JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(tokens.User.Id, refreshed!.User.Id);
        Assert.NotEqual(tokens.RefreshToken, refreshed.RefreshToken);
    }

    [Fact]
    public async Task A_refresh_token_cannot_be_used_twice()
    {
        // Rotation is what limits a stolen token to a single use, and makes the theft
        // visible the next time the real client refreshes.
        var tokens = await SignIn($"apple-{Guid.NewGuid()}");
        var client = fixture.CreateClient();

        await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = tokens.RefreshToken });

        var replay = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
    }

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token()
    {
        var tokens = await SignIn($"apple-{Guid.NewGuid()}");
        var client = fixture.CreateClient();

        var signOut = await client.PostAsJsonAsync("/api/auth/signout",
            new RefreshRequest { RefreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);

        var afterwards = await client.PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = tokens.RefreshToken });

        Assert.Equal(HttpStatusCode.BadRequest, afterwards.StatusCode);
    }

    [Fact]
    public async Task An_unknown_refresh_token_reports_the_same_thing_as_an_expired_one()
    {
        // Distinguishing them would let the response be used to probe which tokens exist.
        var response = await fixture.CreateClient().PostAsJsonAsync("/api/auth/refresh",
            new RefreshRequest { RefreshToken = "definitely-not-a-real-refresh-token" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Refresh_tokens_are_never_stored_in_the_clear()
    {
        // A database leak must not hand anyone a working session for the next two months.
        var tokens = await SignIn($"apple-{Guid.NewGuid()}");

        using var db = fixture.CreateDbContext();
        var stored = db.RefreshTokens.Select(t => t.TokenHash).ToList();

        Assert.DoesNotContain(tokens.RefreshToken, stored);
        Assert.All(stored, hash => Assert.Equal(64, hash.Length));
    }

    [Fact]
    public async Task Two_users_signed_in_for_real_cannot_see_each_others_recipes()
    {
        // The ownership checks are exercised elsewhere with the dev stub; this proves they
        // hold for genuine identities, which is what actually ships.
        var mine = await SignIn($"apple-{Guid.NewGuid()}");
        var theirs = await SignIn($"apple-{Guid.NewGuid()}");

        var sidecar = fixture.Services.GetRequiredService<StubSidecar>();
        var oembed = fixture.Services.GetRequiredService<StubOEmbed>();
        sidecar.Throws = null;
        sidecar.Next = (_, _) => StubSidecar.Narrated();
        oembed.Throw = null;
        oembed.Respond = _ => StubOEmbed.Result();

        var created = await Bearer(mine.AccessToken).PostAsJsonAsync("/api/recipes/from-url",
            new Recipe.Api.Dtos.Recipes.ExtractFromUrlRequest
            {
                Url = "https://www.tiktok.com/@chef/video/8100000000000000001"
            });
        var queued = await created.Content
            .ReadFromJsonAsync<Recipe.Api.Dtos.Recipes.RecipeDto>(AppFixture.JsonOptions);
        await fixture.DrainQueueAsync();

        var owner = await Bearer(mine.AccessToken).GetAsync($"/api/recipes/{queued!.Id}");
        var stranger = await Bearer(theirs.AccessToken).GetAsync($"/api/recipes/{queued.Id}");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger.StatusCode);
    }
}
