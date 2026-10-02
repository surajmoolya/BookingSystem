namespace SeatReservation.Application.Shows;

public abstract record CreateShowOutcome
{
    private CreateShowOutcome()
    {
    }

    /// <summary>The show and its seats were committed; <see cref="Snapshot"/> has every seat available.</summary>
    public sealed record Created(ShowSnapshot Snapshot) : CreateShowOutcome;

    /// <summary>The command failed validation; nothing was written.</summary>
    public sealed record Invalid(IReadOnlyDictionary<string, string[]> Errors) : CreateShowOutcome;
}
