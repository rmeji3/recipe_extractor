using System.Security.Claims;
using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Recipe.Api.Common.Exceptions;
using Recipe.Api.Dtos.Cooking;
using Recipe.Api.Services.Cooking;

namespace Recipe.Api.Controllers.Cooking;

/// <summary>Cooking from the corpus: scaling, timers, and shopping.</summary>
[ApiController]
[Authorize]
[ApiVersion("1.0")]
[Route("api")]
[Route("api/v{version:apiVersion}")]
public class CookingController(ICookingService cooking, ICookLogService cookLog) : ControllerBase
{
    /// <summary>A recipe prepared for cooking.</summary>
    /// <remarks>
    /// Numbers the steps and parses timers out of them, so "simmer for 20 minutes" offers a
    /// timer rather than making someone set one with wet hands.
    ///
    /// Pass <c>servings</c> to scale. Quantities are multiplied; **times are not** — doubling
    /// a recipe barely changes how long it cooks, and scaling that number would be actively
    /// dangerous. Vague amounts like "a pinch" pass through untouched.
    /// </remarks>
    /// <response code="200">The recipe, ready to cook.</response>
    /// <response code="400">Servings out of range, or the recipe never said what it serves.</response>
    /// <response code="404">No such recipe for this user.</response>
    [HttpGet("recipes/{id:guid}/cook")]
    [ProducesResponseType(typeof(CookModeDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cook(
        Guid id,
        CancellationToken cancellationToken,
        [FromQuery] int? servings = null)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await cooking.GetCookModeAsync(userId, id, servings, cancellationToken));
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

    /// <summary>Records that this recipe was cooked.</summary>
    /// <remarks>
    /// The only honest signal the product has — saves measure intent, this measures use.
    ///
    /// It also teaches the pantry: the recipe's ingredients are recorded as ones this
    /// person works with, so substitution suggestions drift toward their own shelf over
    /// time with nothing extra for them to do.
    /// </remarks>
    /// <response code="200">Logged. <c>learnedIngredients</c> is what the pantry took from it.</response>
    /// <response code="404">No such recipe for this user.</response>
    [HttpPost("recipes/{id:guid}/cooked")]
    [ProducesResponseType(typeof(CookLogDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cooked(
        Guid id,
        [FromBody] LogCookRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await cookLog.LogAsync(userId, id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>Every time this recipe has been cooked, newest first.</summary>
    /// <remarks>
    /// One row per cook rather than a counter, because the second time someone makes a
    /// dish they usually change something, and their note about it is the most valuable
    /// text in the app.
    /// </remarks>
    [HttpGet("recipes/{id:guid}/history")]
    [ProducesResponseType(typeof(RecipeHistoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> History(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await cookLog.HistoryAsync(userId, id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>Builds one shopping list from several recipes.</summary>
    /// <remarks>
    /// Ingredients appearing in more than one recipe are combined — 2 tbsp plus 1 tbsp
    /// becomes 3 tbsp, and 1 kg plus 500 g becomes 1.5 kg.
    ///
    /// Amounts that cannot honestly be added come back with a null quantity and their
    /// <c>sources</c> listed separately. 100g of butter and 2 tablespoons of butter need a
    /// density table nobody has; two lines is honest, one wrong number is not.
    /// </remarks>
    /// <response code="404">None of those recipes belong to this user.</response>
    [HttpPost("grocery-list")]
    [ProducesResponseType(typeof(GroceryListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GroceryList(
        [FromBody] GroceryListRequest request,
        CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);

        if (userId is null)
        {
            return Unauthorized();
        }

        try
        {
            return Ok(await cooking.BuildGroceryListAsync(userId, request.RecipeIds, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
