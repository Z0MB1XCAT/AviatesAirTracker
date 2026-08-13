using AviatesAirTracker.Platform;
using Xunit;

namespace AviatesAirTracker.Tests.Platform;

public class ImmediateUiDispatcherTests
{
    [Fact]
    public void Invoke_RunsSynchronouslyOnTheCallingThread()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var callingThread = Environment.CurrentManagedThreadId;
        var observedThread = 0;

        dispatcher.Invoke(() => observedThread = Environment.CurrentManagedThreadId);

        Assert.Equal(callingThread, observedThread);
    }

    [Fact]
    public void Invoke_PropagatesExceptions()
    {
        var dispatcher = new ImmediateUiDispatcher();

        Assert.Throws<InvalidOperationException>(
            () => dispatcher.Invoke(() => throw new InvalidOperationException("boom")));
    }

    [Fact]
    public void BeginInvoke_RunsTheAction()
    {
        var dispatcher = new ImmediateUiDispatcher();
        var ran = false;

        dispatcher.BeginInvoke(() => ran = true);

        Assert.True(ran);
    }
}
