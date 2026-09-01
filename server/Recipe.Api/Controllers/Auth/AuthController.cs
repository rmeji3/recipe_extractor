using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Auth;
using Recipe.Api.Services.Auth;

namespace Recipe.Api.Controllers.Auth;

/// <summary>Sign in, stay signed in, sign out.</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
public class AuthController(IAuthService authService) : ControllerBase
{
    /// <summary>Exchanges an Apple identity token for this app's tokens.</summary>
    /// <remarks>
    /// The client runs Sign in with Apple on device and posts the resulting
    /// <c>identityToken</c>. It is verified against Apple's published keys and against this
    /// app's bundle id before any account is created.
    ///
    /// Apple returns the user's name on the **first** authorization only — send it here or
    /// it is lost for good.
    /// </remarks>
    /// <response code="200">Signed in. Store both tokens.</response>
    /// <response code="400">The token could not be verified.</response>
    [HttpPost("apple")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthTokensDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Apple(
        [FromBody] AppleSignInRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await authService.SignInWithAppleAsync(request, cancellationToken));
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Exchanges a refresh token for a new pair.</summary>
    /// <remarks>
    /// The old refresh token is revoked in the same call, so always store the new one. A
    /// 400 here means the session is gone and the user has to sign in again.
    /// </remarks>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthTokensDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Refresh(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return Ok(await authService.RefreshAsync(request.RefreshToken, cancellationToken));
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Revokes a refresh token.</summary>
    /// <remarks>
    /// The access token stays valid until it expires — up to an hour. Sign-out is not
    /// instant revocation, and the client should discard its tokens locally too.
    /// </remarks>
    [HttpPost("signout")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> SignOutSession(
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        await authService.SignOutAsync(request.RefreshToken, cancellationToken);
        return NoContent();
    }

    /// <summary>Returns the signed-in user.</summary>
    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Me(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await authService.GetAsync(userId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
