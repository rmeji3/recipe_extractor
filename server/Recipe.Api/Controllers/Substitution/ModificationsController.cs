using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Recipes;
using Recipe.Api.Dtos.Substitution;
using Recipe.Api.Services.Substitution;

namespace Recipe.Api.Controllers.Substitution;

/// <summary>
/// Adapting a recipe — vegetarian, healthier, higher protein — using substitutions that
/// have been vetted rather than invented.
/// </summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/recipes")]
[Route("api/v{version:apiVersion}/recipes")]
public class ModificationsController(IModificationService modifications) : ControllerBase
{
    /// <summary>Proposes a rewrite. Changes nothing until accepted.</summary>
    /// <remarks>
    /// Every proposed swap comes from a curated table of ingredient functions and tested
    /// replacements, filtered by the goal and by the user's profile, and ranked so that
    /// things they already cook with come first. Anything the model suggests that is not
    /// backed by that table is discarded and reported in <c>warnings</c>.
    ///
    /// A recipe with nothing substitutable comes back with no changes and says so, rather
    /// than inventing something.
    /// </remarks>
    /// <response code="200">A proposal. Empty <c>changes</c> means nothing applied.</response>
    /// <response code="400">No goal given, or the recipe has no ingredients yet.</response>
    /// <response code="404">No such recipe for this user.</response>
    [HttpPost("{id:guid}/modify")]
    [ProducesResponseType(typeof(ModificationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Modify(
        Guid id,
        [FromBody] ModifyRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await modifications.ProposeAsync(userId, id, request.Goal, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (DomainValidationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Accepts a proposal, creating the adapted recipe.</summary>
    /// <remarks>
    /// The result is a **new** recipe alongside the original, which is left untouched —
    /// it came from a real video, and that is what the substitution was derived from.
    /// </remarks>
    /// <response code="200">The adapted recipe.</response>
    /// <response code="404">No such proposal for this user.</response>
    [HttpPost("modifications/{modificationId:guid}/accept")]
    [ProducesResponseType(typeof(RecipeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Accept(Guid modificationId, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await modifications.AcceptAsync(userId, modificationId, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
