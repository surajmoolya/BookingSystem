namespace SeatReservation.Application.Shows;

public abstract record GetShowOutcome
{
    private GetShowOutcome()
    {
    }

    public sealed record Found(ShowSnapshot Snapshot) : GetShowOutcome;

    public sealed record ShowNotFound : GetShowOutcome;
}
