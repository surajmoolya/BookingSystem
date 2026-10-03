using Microsoft.AspNetCore.Mvc;
using SeatReservation.Application.Reservations;
using Serilog;

namespace SeatReservation.Api.Observability;

/// <summary>
/// Adds the business outcome to the request's single completion log line (lld §10, D-088) through Serilog's
/// <see cref="IDiagnosticContext"/>: <c>Outcome</c>, <c>Reason</c> (declines only), <c>ReservationId</c>, <c>UserId</c>,
/// <c>ShowId</c>. A controller-layer concern, so the logic layer stays free of Serilog. Ids belong in logs, never in metrics.
/// </summary>
public static class RequestLogContext
{
    public static void Reserve(ControllerBase controller, ReservationOutcome outcome, string userId, Guid showId)
    {
        var diagnostics = For(controller);
        if (diagnostics is null)
        {
            return;
        }

        diagnostics.Set("UserId", userId);
        diagnostics.Set("ShowId", showId);
        switch (outcome)
        {
            case ReservationOutcome.Created created:
                diagnostics.Set("Outcome", "created");
                diagnostics.Set("ReservationId", created.Reservation.Id);
                break;

            case ReservationOutcome.Replayed replayed:
                diagnostics.Set("Outcome", "replayed");
                diagnostics.Set("ReservationId", replayed.Reservation.Id);
                break;

            default:
                var reason = PrometheusReservationMetrics.Label(outcome.DeclineReason!.Value);
                diagnostics.Set("Outcome", reason);
                diagnostics.Set("Reason", reason);
                break;
        }
    }

    public static void Cancel(ControllerBase controller, CancelOutcome outcome, Guid reservationId, string userId) =>
        Set(controller, userId, reservationId, outcome switch
        {
            CancelOutcome.Cancelled => "cancelled",
            CancelOutcome.AlreadyCancelled => "already_cancelled",
            CancelOutcome.NotOwner => "not_owner",
            _ => "reservation_not_found",
        });

    public static void GetReservation(ControllerBase controller, GetReservationOutcome outcome, Guid reservationId, string userId) =>
        Set(controller, userId, reservationId, outcome switch
        {
            GetReservationOutcome.Found => "found",
            GetReservationOutcome.NotOwner => "not_owner",
            _ => "reservation_not_found",
        });

    private static void Set(ControllerBase controller, string userId, Guid reservationId, string outcome)
    {
        var diagnostics = For(controller);
        diagnostics?.Set("Outcome", outcome);
        diagnostics?.Set("UserId", userId);
        diagnostics?.Set("ReservationId", reservationId);
    }

    // Null when Serilog request logging isn't wired (e.g. a host built without it); logging is then simply skipped.
    private static IDiagnosticContext? For(ControllerBase controller) =>
        controller.HttpContext.RequestServices.GetService<IDiagnosticContext>();
}
