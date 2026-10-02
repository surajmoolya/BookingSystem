using SeatReservation.Application.Abstractions;
using SeatReservation.Application.Exceptions;
using SeatReservation.Application.Reservations;
using SeatReservation.Application.Shows;

namespace SeatReservation.UnitTests.Fakes;

public sealed record SeatRow(int Ordinal, SeatStatus Status, string? UserId = null, Guid? ReservationId = null);

/// <summary>
/// An in-memory stand-in for the database and the unit of work over it. It keeps seat, reservation and show state,
/// mirrors the semantics of the real SQL where the service relies on them (confirm only touches available rows,
/// a duplicate (user, key) insert throws, release is keyed by reservation id), and appends every call to an ordered
/// <see cref="Calls"/> log so tests can assert the correctness-critical call order (D-083).
/// The read fakes and <see cref="FakeTransactionRunner"/> share one instance per test.
/// </summary>
public sealed class InMemoryUnitOfWork : IUnitOfWork
{
    private Dictionary<(Guid ShowId, string Label), SeatRow> _seats = new();
    private Dictionary<Guid, Reservation> _reservations = new();
    private Dictionary<Guid, ShowInfo> _shows = new();

    public InMemoryUnitOfWork()
    {
        UserLock = new UserLockFake(this);
        Reservations = new ReservationRepositoryFake(this);
        Seats = new SeatRepositoryFake(this);
        Shows = new ShowRepositoryFake(this);
    }

    /// <summary>Ordered log of repository and transaction calls, e.g. <c>Seats.LockForUpdate:A1,A2</c>.</summary>
    public List<string> Calls { get; } = [];

    public IUserLock UserLock { get; }

    public IReservationRepository Reservations { get; }

    public ISeatRepository Seats { get; }

    public IShowRepository Shows { get; }

    /// <summary>When set, the next <c>Reservations.InsertAsync</c> throws it once (e.g. a lost idempotency race).</summary>
    public Exception? NextInsertFailure { get; set; }

    /// <summary>
    /// When set, the next <c>Reservations.InsertAsync</c> behaves as if another transaction committed this reservation
    /// first: the winner (and its seats) lands as committed state that survives our rollback, and the insert throws
    /// <see cref="DuplicateIdempotencyKeyException"/> the way <c>uq_reservations_user_key</c> would.
    /// </summary>
    public Reservation? ConcurrentWinner { get; set; }

    /// <summary>When set, <c>Seats.ConfirmAsync</c> reports this row count instead of the real one (simulates lost locks).</summary>
    public int? ConfirmResultOverride { get; set; }

    public TimeSpan? LastLockTimeout { get; private set; }

    public IReadOnlyDictionary<(Guid ShowId, string Label), SeatRow> SeatRows => _seats;

    public IReadOnlyDictionary<Guid, Reservation> ReservationRows => _reservations;

    public IReadOnlyDictionary<Guid, ShowInfo> ShowRows => _shows;

    public Task SetLockTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        LastLockTimeout = timeout;
        Calls.Add("SetLockTimeout");
        return Task.CompletedTask;
    }

    // ---- test setup ----

    /// <summary>Seeds a show with all seats available (ordinal = position).</summary>
    public ShowInfo AddShow(ShowInfo show, IReadOnlyList<string> labels)
    {
        _shows[show.Id] = show;
        for (var i = 0; i < labels.Count; i++)
        {
            _seats[(show.Id, labels[i])] = new SeatRow(i + 1, SeatStatus.Available);
        }

        return show;
    }

    /// <summary>Seeds a show named "show" with the given labels, price 25,000 paise and per-user limit 4.</summary>
    public ShowInfo AddShow(Guid id, params string[] labels) =>
        AddShow(new ShowInfo(id, "show", 25_000, 4, labels.Length), labels);

    public void SetSeat(Guid showId, string label, SeatStatus status, string? userId, Guid? reservationId)
    {
        var ordinal = _seats[(showId, label)].Ordinal;
        _seats[(showId, label)] = new SeatRow(ordinal, status, userId, reservationId);
    }

    /// <summary>Seeds an existing reservation and marks its seats confirmed for its user.</summary>
    public void AddConfirmedReservation(Reservation reservation)
    {
        _reservations[reservation.Id] = reservation;
        foreach (var label in reservation.Seats)
        {
            SetSeat(reservation.ShowId, label, SeatStatus.Confirmed, reservation.UserId, reservation.Id);
        }
    }

    public SeatRow Seat(Guid showId, string label) => _seats[(showId, label)];

    // ---- transactional state, used by FakeTransactionRunner ----

    internal object Snapshot() => (new Dictionary<(Guid, string), SeatRow>(_seats), new Dictionary<Guid, Reservation>(_reservations), new Dictionary<Guid, ShowInfo>(_shows));

    internal void Restore(object snapshot)
    {
        var (seats, reservations, shows) = ((Dictionary<(Guid, string), SeatRow>, Dictionary<Guid, Reservation>, Dictionary<Guid, ShowInfo>))snapshot;
        _seats = new Dictionary<(Guid ShowId, string Label), SeatRow>(seats);
        _reservations = new Dictionary<Guid, Reservation>(reservations);
        _shows = new Dictionary<Guid, ShowInfo>(shows);
        if (_committedElsewhere is { } winner)
        {
            AddConfirmedReservation(winner);
        }
    }

    // A reservation committed by a simulated concurrent transaction; re-applied on every restore.
    private Reservation? _committedElsewhere;

    // ---- repositories ----

    private sealed class UserLockFake(InMemoryUnitOfWork db) : IUserLock
    {
        public Task AcquireAsync(string userId, CancellationToken ct)
        {
            db.Calls.Add($"UserLock.Acquire:{userId}");
            return Task.CompletedTask;
        }
    }

    private sealed class ReservationRepositoryFake(InMemoryUnitOfWork db) : IReservationRepository
    {
        public Task<Reservation?> FindByKeyAsync(string userId, string idempotencyKey, CancellationToken ct)
        {
            db.Calls.Add("Reservations.FindByKey");
            return Task.FromResult(db.FindByKey(userId, idempotencyKey));
        }

        public Task<Reservation?> GetByIdForUpdateAsync(Guid reservationId, CancellationToken ct)
        {
            db.Calls.Add("Reservations.GetByIdForUpdate");
            return Task.FromResult(db._reservations.GetValueOrDefault(reservationId));
        }

        public Task InsertAsync(Reservation reservation, CancellationToken ct)
        {
            db.Calls.Add("Reservations.Insert");
            if (db.ConcurrentWinner is { } winner)
            {
                db.ConcurrentWinner = null;
                db._committedElsewhere = winner;
                db.AddConfirmedReservation(winner);
                throw new DuplicateIdempotencyKeyException();
            }

            if (db.NextInsertFailure is { } failure)
            {
                db.NextInsertFailure = null;
                throw failure;
            }

            // Mirrors uq_reservations_user_key (23505 translated by the real repository).
            if (db.FindByKey(reservation.UserId, reservation.IdempotencyKey) is not null)
            {
                throw new DuplicateIdempotencyKeyException();
            }

            db._reservations[reservation.Id] = reservation;
            return Task.CompletedTask;
        }

        public Task MarkCancelledAsync(Guid reservationId, DateTimeOffset at, CancellationToken ct)
        {
            db.Calls.Add("Reservations.MarkCancelled");
            db._reservations[reservationId] = db._reservations[reservationId].AsCancelled(at);
            return Task.CompletedTask;
        }
    }

    private sealed class SeatRepositoryFake(InMemoryUnitOfWork db) : ISeatRepository
    {
        public Task<int> CountConfirmedByUserAsync(Guid showId, string userId, CancellationToken ct)
        {
            db.Calls.Add("Seats.CountConfirmedByUser");
            var count = db._seats.Count(s => s.Key.ShowId == showId && s.Value.UserId == userId && s.Value.Status == SeatStatus.Confirmed);
            return Task.FromResult(count);
        }

        public Task<IReadOnlyList<LockedSeat>> LockForUpdateAsync(Guid showId, IReadOnlyList<string> sortedLabels, CancellationToken ct)
        {
            // Logged exactly as passed, so tests can verify the caller sorted the labels.
            db.Calls.Add($"Seats.LockForUpdate:{string.Join(',', sortedLabels)}");
            IReadOnlyList<LockedSeat> locked = sortedLabels
                .Where(l => db._seats.ContainsKey((showId, l)))
                .OrderBy(l => l, StringComparer.Ordinal)
                .Select(l => new LockedSeat(l, db._seats[(showId, l)].Status))
                .ToList();
            return Task.FromResult(locked);
        }

        public Task<int> ConfirmAsync(Guid showId, IReadOnlyList<string> labels, string userId, Guid reservationId, CancellationToken ct)
        {
            db.Calls.Add($"Seats.Confirm:{string.Join(',', labels)}");
            var updated = 0;
            foreach (var label in labels)
            {
                if (db._seats.TryGetValue((showId, label), out var row) && row.Status == SeatStatus.Available)
                {
                    db._seats[(showId, label)] = row with { Status = SeatStatus.Confirmed, UserId = userId, ReservationId = reservationId };
                    updated++;
                }
            }

            return Task.FromResult(db.ConfirmResultOverride ?? updated);
        }

        public Task<int> ReleaseByReservationAsync(Guid reservationId, CancellationToken ct)
        {
            db.Calls.Add("Seats.ReleaseByReservation");
            var keys = db._seats.Where(s => s.Value.ReservationId == reservationId).Select(s => s.Key).ToList();
            foreach (var key in keys)
            {
                db._seats[key] = db._seats[key] with { Status = SeatStatus.Available, UserId = null, ReservationId = null };
            }

            return Task.FromResult(keys.Count);
        }
    }

    private sealed class ShowRepositoryFake(InMemoryUnitOfWork db) : IShowRepository
    {
        public Task InsertShowWithSeatsAsync(ShowInfo show, IReadOnlyList<string> labels, CancellationToken ct)
        {
            db.Calls.Add("Shows.InsertShowWithSeats");
            db.AddShow(show, labels);
            return Task.CompletedTask;
        }
    }

    internal Reservation? FindByKey(string userId, string idempotencyKey) =>
        _reservations.Values.FirstOrDefault(r => r.UserId == userId && r.IdempotencyKey == idempotencyKey);
}
