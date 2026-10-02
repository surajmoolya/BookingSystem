namespace Burst;

public static class StartGate
{
    /// <summary>
    /// Starts <paramref name="count"/> calls that all park at one gate, then opens it, so the requests are truly concurrent
    /// rather than a fast loop (lld §12). Results come back in index order.
    /// </summary>
    public static async Task<T[]> RunAsync<T>(int count, Func<int, Task<T>> action)
    {
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
}
