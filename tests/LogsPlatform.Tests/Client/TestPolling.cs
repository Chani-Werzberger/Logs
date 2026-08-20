namespace LogsPlatform.Tests.Client;

internal static class TestPolling
{
    public static async Task<int> WaitForCountAsync(Func<Task<int>> countQuery, int expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var count = await countQuery();
        while (count < expected && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            count = await countQuery();
        }
        return count;
    }
}
