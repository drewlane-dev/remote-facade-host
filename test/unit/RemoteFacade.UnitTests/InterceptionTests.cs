using RemoteFacadeHost;
using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The proxy and the listener, driven against each other in-process.
///
/// Both halves are exercised for real -- a real HTTP round trip over loopback,
/// the real JSON on the wire -- because the interesting behaviour IS the
/// rendezvous, and a fake on either side would test the fake. What this leaves
/// to the container suite is only whether Docker delivers it.
/// </summary>
public class InterceptionUnitTests
{
    public interface IWork
    {
        string Do(string input);
        int Count();
    }

    private sealed class RealWork : IWork
    {
        public int Calls;
        public string Do(string input) { Calls++; return $"did:{input}"; }
        public int Count() => Calls;
        public Func<string, string>? OnDo { get; init; }
    }

    private sealed class ThrowingWork : IWork
    {
        public string Do(string input) => throw new InvalidOperationException("inner blew up");
        public int Count() => 0;
    }

    private static int NextPort() => 9400 + Random.Shared.Next(1, 400);

    private static (InterceptHost Host, IWork Proxy, T Inner) Wire<T>(T inner, int port) where T : class, IWork
    {
        var host = InterceptHost.Start(port);
        var proxy = (IWork)InterceptProxy.Wrap(typeof(IWork), inner, $"http://127.0.0.1:{port}");
        return (host, proxy, inner);
    }

    [Fact]
    public async Task The_call_reaches_the_real_implementation_and_its_value_comes_back()
    {
        // Interception must be transparent by default: the graph behaves as if
        // nothing were interposed.
        var (host, proxy, inner) = Wire(new RealWork(), NextPort());
        await using var _ = host;

        Assert.Equal("did:x", proxy.Do("x"));
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task Every_call_is_reported_with_its_method_and_an_increasing_number()
    {
        var (host, proxy, _) = Wire(new RealWork(), NextPort());
        await using var _h = host;

        proxy.Do("a");
        proxy.Do("b");
        proxy.Count();

        Assert.Equal(3, host.Seen.Count);
        Assert.Equal([nameof(IWork.Do), nameof(IWork.Do), nameof(IWork.Count)],
            host.Seen.Select(c => c.Method));
        Assert.Equal([1, 2, 3], host.Seen.Select(c => c.Call));
        Assert.All(host.Seen, c => Assert.Equal(typeof(IWork).FullName, c.Service));
    }

    [Fact]
    public async Task A_handler_that_blocks_HOLDS_the_call()
    {
        // The whole feature. If this does not hold, nothing downstream --
        // freezing a container mid-job, killing between steps -- is possible.
        var (host, proxy, inner) = Wire(new RealWork(), NextPort());
        await using var _h = host;

        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        host.On<IWork>(nameof(IWork.Do), async _ => { entered.TrySetResult(); await release.Task; });

        var call = Task.Run(() => proxy.Do("x"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Held BEFORE the real implementation ran: that is what makes the
        // suspended state inspectable rather than merely delayed.
        Assert.False(call.IsCompleted);
        Assert.Equal(0, inner.Calls);

        release.SetResult();
        Assert.Equal("did:x", await call);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task A_handler_can_fail_the_call_instead_of_letting_it_run()
    {
        var (host, proxy, inner) = Wire(new RealWork(), NextPort());
        await using var _h = host;

        host.On<IWork>(nameof(IWork.Do), _ => Task.FromResult<string?>("injected fault"));

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.Do("x"));

        Assert.Contains("injected fault", ex.Message);
        // The real implementation must NOT have run: a fault that also performed
        // the side effect would be worse than useless.
        Assert.Equal(0, inner.Calls);
    }

    [Fact]
    public async Task Only_the_named_method_is_intercepted()
    {
        var (host, proxy, _) = Wire(new RealWork(), NextPort());
        await using var _h = host;

        var hits = 0;
        host.On<IWork>(nameof(IWork.Count), _ => { hits++; return Task.CompletedTask; });

        proxy.Do("a");
        Assert.Equal(0, hits);

        proxy.Count();
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task An_exception_from_the_real_implementation_arrives_UNWRAPPED()
    {
        // method.Invoke wraps in TargetInvocationException. Letting that escape
        // would change what the code under test catches, so interposing would
        // alter behaviour -- the one thing it must never do.
        var (host, proxy, _) = Wire(new ThrowingWork(), NextPort());
        await using var _h = host;

        var ex = Assert.Throws<InvalidOperationException>(() => proxy.Do("x"));
        Assert.Equal("inner blew up", ex.Message);
    }

    [Fact]
    public void A_listener_that_is_not_there_does_not_break_the_call()
    {
        // The test process is the thing that died. Failing the plugin's call
        // would disguise that as an application fault.
        //
        // A port that is bound and then released, so the address is valid and
        // reachable-in-form but refuses immediately. A malformed URL would fail
        // in the Uri constructor instead and prove nothing about the call path.
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var dead = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var inner = new RealWork();
        var proxy = (IWork)InterceptProxy.Wrap(typeof(IWork), inner, $"http://127.0.0.1:{dead}");

        Assert.Equal("did:x", proxy.Do("x"));
        Assert.Equal(1, inner.Calls);
    }
}
