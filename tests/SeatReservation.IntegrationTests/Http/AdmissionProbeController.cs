using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SeatReservation.Api.Admission;

namespace SeatReservation.IntegrationTests.Http;

/// <summary>
/// Test-only controller under the <c>db</c> admission policy whose action holds its permit until the test opens the
/// <see cref="AdmissionGate"/>. Hosts that call it register the gate as a singleton.
/// </summary>
[ApiController]
[Route("_probe/admission")]
[EnableRateLimiting(AdmissionControl.DbPolicy)]
public sealed class AdmissionProbeController : ControllerBase
{
    [HttpGet("hold")]
    public async Task<IActionResult> Hold([FromServices] AdmissionGate gate)
    {
        gate.Entered();
        await gate.Released;
        return Ok(new { held = true });
    }
}

/// <summary>Counts requests that got a permit and holds them until <see cref="Release"/>.</summary>
public sealed class AdmissionGate
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _entered = new(0);

    public Task Released => _release.Task;

    public void Entered() => _entered.Release();

    public Task<bool> WaitEnteredAsync() => _entered.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _release.TrySetResult();
}
