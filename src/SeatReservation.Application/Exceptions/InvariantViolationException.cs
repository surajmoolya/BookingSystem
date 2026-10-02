namespace SeatReservation.Application.Exceptions;

/// <summary>A condition that cannot happen by construction did happen. Surfaces as 500 and an <c>invariant.violation</c> log.</summary>
public sealed class InvariantViolationException(string message) : Exception(message);
