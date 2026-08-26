using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// InstanceHolder decides when a graph may be disposed while calls are in
/// flight. Every case here turns on an interleaving -- a reset landing during
/// a call, two resets racing -- which is why they are unit tests: driving
/// these through HTTP means hoping the timing lands, and a hopeful test that
/// passes proves nothing. The container suite covers the ordinary path.
/// </summary>
public class InstanceHolderTests
{
    [Fact]
    public void Reset_with_nothing_in_flight_disposes_the_old_graph_immediately()
    {
        var root = new Counted();
        var holder = new InstanceHolder(() => Graphs.Named("only", root));

        holder.Reset();

        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public async Task Reset_during_a_call_does_not_dispose_until_the_call_returns()
    {
        var first = new Counted();
        var graphs = new Queue<Counted>([first, new Counted()]);
        var holder = new InstanceHolder(() => Graphs.Named("g", graphs.Dequeue()));

        var insideCall = new TaskCompletionSource();
        var releaseCall = new TaskCompletionSource();

        var call = holder.UseAsync(async _ =>
        {
            insideCall.SetResult();
            await releaseCall.Task;
            return 1;
        });

        await insideCall.Task;
        holder.Reset();

        // The whole point: the graph a caller is standing on cannot be pulled
        // out from under it, however long that caller takes.
        Assert.Equal(0, first.Disposals);

        releaseCall.SetResult();
        await call;

        Assert.Equal(1, first.Disposals);
    }

    [Fact]
    public async Task Reset_does_not_block_on_an_in_flight_call()
    {
        var holder = new InstanceHolder(() => Graphs.Named("g", new Counted()));
        var insideCall = new TaskCompletionSource();
        var releaseCall = new TaskCompletionSource();

        var call = holder.UseAsync(async _ =>
        {
            insideCall.SetResult();
            await releaseCall.Task;
            return 1;
        });
        await insideCall.Task;

        // Reset on a worker thread, so a regression that makes it wait on the
        // call shows up as a timeout instead of hanging the whole test run.
        // This is the exact failure a ReaderWriterLockSlim implementation had:
        // DELETE /instance blocked for as long as the container stayed up.
        var reset = Task.Run(holder.Reset);
        var finished = await Task.WhenAny(reset, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.True(ReferenceEquals(finished, reset),
            "Reset() blocked while a call was in flight; it must never wait on user code.");

        releaseCall.SetResult();
        await call;
    }

    [Fact]
    public async Task A_retired_graph_is_disposed_exactly_once_however_many_callers_were_on_it()
    {
        var root = new Counted();
        var graphs = new Queue<Counted>([root, new Counted()]);
        var holder = new InstanceHolder(() => Graphs.Named("g", graphs.Dequeue()));

        var release = new TaskCompletionSource();
        var arrived = new CountdownEvent(4);

        var calls = Enumerable.Range(0, 4).Select(_ => holder.UseAsync(async _ =>
        {
            arrived.Signal();
            await release.Task;
            return 1;
        })).ToArray();

        arrived.Wait(TimeSpan.FromSeconds(5));
        holder.Reset();
        Assert.Equal(0, root.Disposals);

        release.SetResult();
        await Task.WhenAll(calls);

        // Four callers leave, but only the LAST one may dispose. Nothing here
        // relies on the plugin's Dispose() being idempotent, because nothing
        // guarantees that of a third-party type.
        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public void A_factory_that_throws_leaves_the_previous_graph_serving()
    {
        var root = new Counted();
        var fail = false;
        var holder = new InstanceHolder(() =>
            fail ? throw new InvalidOperationException("startup failed") : Graphs.Named("original", root));

        fail = true;
        Assert.Throws<InvalidOperationException>(holder.Reset);

        // The old graph must be neither disposed nor unpublished: a failed
        // rebuild that retired the current one would leave every later call
        // hitting a disposed provider.
        Assert.Equal(0, root.Disposals);
        Assert.Equal("original", holder.Use(g => g.ServiceNames[0]));
    }

    [Fact]
    public void A_root_whose_Dispose_throws_propagates_on_the_immediate_reset_path()
    {
        var root = new Counted { ThrowOnDispose = true };
        var holder = new InstanceHolder(() => Graphs.Named("g", root));

        // Reported to the operator who asked for the reset, rather than
        // swallowed -- DELETE /instance answers 500 and says why.
        Assert.Throws<InvalidOperationException>(holder.Reset);
        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public async Task A_root_whose_Dispose_throws_on_the_DEFERRED_path_does_not_fault_the_call()
    {
        var root = new Counted { ThrowOnDispose = true };
        var graphs = new Queue<Counted>([root, new Counted()]);
        var holder = new InstanceHolder(() => Graphs.Named("g", graphs.Dequeue()));

        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var call = holder.UseAsync(async _ => { inside.SetResult(); await release.Task; return 42; });

        await inside.Task;
        holder.Reset();
        release.SetResult();

        // The disposal now happens on the LAST CALLER's thread. That caller
        // asked for a method result and had nothing to do with the reset, so a
        // throwing plugin Dispose() must not become its exception.
        Assert.Equal(42, await call);
        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public async Task A_call_started_before_a_reset_keeps_the_graph_it_started_on()
    {
        var names = new Queue<string>(["first", "second"]);
        var holder = new InstanceHolder(() => Graphs.Named(names.Dequeue(), new Counted()));

        var inside = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        string? seen = null;

        var call = holder.UseAsync(async g =>
        {
            inside.SetResult();
            await release.Task;
            seen = g.ServiceNames[0];   // read AFTER the reset has happened
            return 1;
        });

        await inside.Task;
        holder.Reset();
        release.SetResult();
        await call;

        Assert.Equal("first", seen);
        Assert.Equal("second", holder.Use(g => g.ServiceNames[0]));
    }
}
