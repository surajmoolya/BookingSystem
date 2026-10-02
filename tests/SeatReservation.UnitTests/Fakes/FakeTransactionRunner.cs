using SeatReservation.Application.Abstractions;

namespace SeatReservation.UnitTests.Fakes;

/// <summary>
/// Runs the delegate against an <see cref="InMemoryUnitOfWork"/> the way the real runner does: commit keeps the
/// changes, rollback (or an exception) restores the pre-transaction state. It records what happened, and can
/// simulate transient retries by re-running the whole delegate.
/// </summary>
public sealed class FakeTransactionRunner(InMemoryUnitOfWork uow) : ITransactionRunner
{
    /// <summary>How many times <c>RunAsync</c> was called (0 means the locked path was never entered).</summary>
    public int Invocations { get; private set; }

    /// <summary>Operation names passed to <c>RunAsync</c>, in order.</summary>
    public List<string> Operations { get; } = [];

    public int Commits { get; private set; }

    public int Rollbacks { get; private set; }

    /// <summary>The final decision of the most recent run; null before any run.</summary>
    public bool? LastCommit { get; private set; }

    /// <summary>How many extra attempts to simulate per run: each earlier attempt is rolled back and its result discarded.</summary>
    public int SimulatedRetries { get; set; }

    /// <summary>Thrown from <c>RunAsync</c> before any work, e.g. a <c>DependencyUnavailableException</c>.</summary>
    public Exception? FailWith { get; set; }

    public async Task<T> RunAsync<T>(string operation, Func<IUnitOfWork, CancellationToken, Task<TxResult<T>>> work, CancellationToken ct)
    {
        Invocations++;
        Operations.Add(operation);
        if (FailWith is { } failure)
        {
            throw failure;
        }

        for (var attempt = 0; ; attempt++)
        {
            var before = uow.Snapshot();
            uow.Calls.Add($"Tx.Begin:{operation}");

            TxResult<T> result;
            try
            {
                result = await work(uow, ct);
            }
            catch
            {
                uow.Restore(before);
                uow.Calls.Add("Tx.Rollback");
                Rollbacks++;
                LastCommit = false;
                throw;
            }

            if (attempt < SimulatedRetries)
            {
                uow.Restore(before);
                uow.Calls.Add("Tx.Retry");
                continue;
            }

            if (result.Commit)
            {
                uow.Calls.Add("Tx.Commit");
                Commits++;
            }
            else
            {
                uow.Restore(before);
                uow.Calls.Add("Tx.Rollback");
                Rollbacks++;
            }

            LastCommit = result.Commit;
            return result.Value;
        }
    }
}
