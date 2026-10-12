using Xunit;

namespace MacExplorer.Tests;

internal static class TestTime
{
    public static async Task WaitUntilAsync(Func<bool> completed)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!completed()) await Task.Delay(10, deadline.Token);
    }
}
