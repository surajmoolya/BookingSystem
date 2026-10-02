using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Shows;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>Test-only controller (registered through <c>ApiFactory</c>) that throws each kind of exception on demand.</summary>
[ApiController]
[Route("_probe")]
public sealed class ErrorProbeController : ControllerBase
{
    public const string SecretDetail = "secret-internal-detail";

    [HttpGet("not-ready")]
    public IActionResult NotReady() => throw new NotReadyException();

    [HttpGet("dependency")]
    public IActionResult Dependency() => throw new DependencyUnavailableException($"db at {SecretDetail} is down", new TimeoutException(SecretDetail));

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException(SecretDetail);

    [HttpGet("invariant")]
    public IActionResult Invariant() => throw new InvariantViolationException(SecretDetail);

    [HttpGet("too-large")]
    public IActionResult TooLarge() => throw new BadHttpRequestException(SecretDetail, StatusCodes.Status413PayloadTooLarge);

    [HttpGet("malformed")]
    public IActionResult Malformed() => throw new BadHttpRequestException(SecretDetail, StatusCodes.Status400BadRequest);

    [HttpGet("ok")]
    public IActionResult Success() => new JsonResult(new { PricePaise = 25000, SeatStatus = SeatStatus.Available, Nested = new { PerUserLimit = 4 } });

    [HttpPost("bind")]
    public IActionResult Bind(ProbeRequest request) => Ok(new { request.Name, SeatCount = request.Seats.Count, request.PricePaise });

    public sealed class ProbeRequest
    {
        [Required]
        public string? Name { get; set; }

        [Required, MinLength(1)]
        public List<string> Seats { get; set; } = [];

        [Range(0, 1_000_000)]
        public int PricePaise { get; set; }
    }
}
