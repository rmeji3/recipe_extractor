using Asp.Versioning;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Recipe.Api.Controllers;

/// <summary>
/// Liveness probe. Also the reference example for the house controller shape:
/// both route attributes, an explicit API version, thin body.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/[controller]")]
[Route("api/v{version:apiVersion}/[controller]")]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    /// <summary>Returns 200 while the process is serving requests.</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });
}
