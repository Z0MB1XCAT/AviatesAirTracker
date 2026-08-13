using AviatesAirTracker.Platform;
using Xunit;

namespace AviatesAirTracker.Tests.Platform;

public class WindowSubclassTests
{
    [Fact]
    public void ForwardsMessagesToTheCallback()
    {
        var received = new List<(nint Hwnd, int Msg)>();
        var subclass = new WindowSubclass((hwnd, msg, _, _) => received.Add((hwnd, msg)));

        subclass.InvokeForTest(42, 0x0402, 0, 0);

        Assert.Single(received);
        Assert.Equal(42, received[0].Hwnd);
        Assert.Equal(0x0402, received[0].Msg);
    }

    [Fact]
    public void StopsForwardingAfterDetach()
    {
        var received = new List<int>();
        var subclass = new WindowSubclass((_, msg, _, _) => received.Add(msg));

        subclass.InvokeForTest(1, 0x0402, 0, 0);
        subclass.Detach();
        subclass.InvokeForTest(1, 0x0402, 0, 0);

        Assert.Single(received);
    }

    [Fact]
    public void DetachIsIdempotent()
    {
        var subclass = new WindowSubclass((_, _, _, _) => { });

        subclass.Detach();
        subclass.Detach();   // must not throw
    }

    [Fact]
    public void ThrowingCallback_DoesNotPropagate_AndInvokesOnError()
    {
        var thrown = new InvalidOperationException("boom");
        Exception? observed = null;
        var subclass = new WindowSubclass(
            (_, _, _, _) => throw thrown,
            onError: ex => observed = ex);

        subclass.InvokeForTest(1, 0x0402, 0, 0);

        Assert.Same(thrown, observed);
    }

    [Fact]
    public void ThrowingCallback_WithNoErrorHandler_DoesNotPropagate()
    {
        var subclass = new WindowSubclass((_, _, _, _) => throw new InvalidOperationException("boom"));

        subclass.InvokeForTest(1, 0x0402, 0, 0);   // must not throw
    }
}
