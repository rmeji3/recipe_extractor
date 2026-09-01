using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Services.Substitution;

namespace Recipe.Api.Controllers.Substitution;

/// <summary>
/// What the app remembers about how someone eats. Feeds every substitution.
/// </summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
public class ProfileController(IProfileService profiles) : ControllerBase
{
    /// <summary>Returns the profile, with defaults when none has been saved.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await profiles.GetAsync(userId, cancellationToken));
    }

    /// <summary>
    /// Replaces the profile.
    /// </summary>
    /// <remarks>
    /// <c>avoid</c> is a hard filter, not a preference — an ingredient listed there is
    /// never offered as a substitution whatever the goal. That is the right behaviour for
    /// an allergy, and the wrong place to be clever.
    /// </remarks>
    [HttpPut]
    [ProducesResponseType(typeof(UserProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateProfileRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await profiles.UpdateAsync(userId, request, cancellationToken));
    }
}
