namespace Gcs.IntegrationTests.Infrastructure;

/// <summary>Polls until an asynchronous condition holds. Live links change state in the background, not per request.</summary>
internal static class Eventually
{
    public static async Task<T> GetAsync<T>(Func<Task<T>> probe, Func<T, bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTime.UtcNow + timeout;
        var last = await probe();
        while (!condition(last))
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out after {timeout.TotalSeconds} s waiting until {because}. Last value: {last}");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
            last = await probe();
        }

        return last;
    }
}
