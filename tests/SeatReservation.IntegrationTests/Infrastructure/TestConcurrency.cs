namespace SeatReservation.IntegrationTests.Infrastructure;

public static class TestConcurrency
{
    /// <summary>
    /// Runs <paramref name="action"/> <paramref name="count"/> times (argument = index 0..count-1) so that every call is
    /// queued and parked at a start gate before any of them begins, then releases them all at once. Results come back in
    /// index order. This is what makes a "200 users, one seat" test an actual race rather than a loop.
    /// </summary>
    public static async Task<T[]> ConcurrentAsync<T>(int count, Func<int, Task<T>> action)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = 0;
        var allParked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            if (Interlocked.Increment(ref parked) == count)
            {
                allParked.SetResult();
            }

            await gate.Task;
            return await action(i);
        })).ToArray();

        await allParked.Task;
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    public static Task ConcurrentAsync(int count, Func<int, Task> action) =>
        ConcurrentAsync<bool>(count, async i =>
        {
            await action(i);
            return true;
        });
}
