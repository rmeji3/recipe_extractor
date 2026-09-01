using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Dtos.Pantry;
using Recipe.Api.Services.Pantry;

namespace Recipe.Api.Controllers.Pantry;

/// <summary>
/// The ingredients this person cooks with.
/// </summary>
/// <remarks>
/// Familiarity, not inventory. It answers "would they know what to do with this", which is
/// what makes a substitution worth suggesting. Real stock tracking — amounts, deductions,
/// expiry — is a later and much harder feature, because it goes silently wrong the moment
/// one use is missed.
/// </remarks>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
public class PantryController(IPantryService pantry) : ControllerBase
{
    /// <summary>Everything currently in the pantry.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<PantryItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null ? Unauthorized() : Ok(await pantry.ListAsync(userId, cancellationToken));
    }

    /// <summary>
    /// Records ingredients this person cooks with.
    /// </summary>
    /// <remarks>
    /// Mostly fills itself: cooking a recipe adds its ingredients automatically. This is
    /// for staples that never show up in a saved recipe — the salt and oil everyone has.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType(typeof(List<PantryItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Add(
        [FromBody] AddPantryItemsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await pantry.AddAsync(
                userId, request.Items.Select(i => i.Item), addedByUser: true, cancellationToken));
    }

    /// <summary>Removes one item.</summary>
    /// <response code="404">Not in this user's pantry.</response>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            await pantry.RemoveAsync(userId, id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>
    /// Recipes ranked by how little unfamiliar shopping they need.
    /// </summary>
    /// <remarks>
    /// Not "what can I cook tonight" — that needs stock tracking this does not have. This
    /// is "nothing in here is new to me", which is a genuinely useful way to pick a
    /// weeknight dinner. Matching is deliberately loose: a recipe wanting "boneless chicken
    /// thighs" counts as familiar to someone who cooks "chicken".
    /// </remarks>
    [HttpGet("familiar")]
    [ProducesResponseType(typeof(List<CookabilityDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Familiar(
        CancellationToken cancellationToken,
        [FromQuery] int limit = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return userId is null
            ? Unauthorized()
            : Ok(await pantry.FamiliarAsync(userId, limit, cancellationToken));
    }
}
