namespace RemoteFacadeHost;

/// <summary>
/// Owns the graph every call reaches, and swaps it without ever letting a call
/// already in flight touch a disposed one.
///
/// Reset exists because the container outlives any individual test: a test that
/// deliberately leaves a lock held would otherwise poison the next one, and the
/// resulting failure would point at the wrong test. Reset REBUILDS -- it
/// replaces the whole graph, provider and singletons included -- which means it
/// also DISPOSES the provider an /invoke may still be running inside.
///
/// That is the whole difficulty. Measured against the unguarded version: two
/// calls in flight across one DELETE /instance both came back
/// {"ok":false,"error":"Cannot access a disposed object. Object name:
/// 'IServiceProvider'."} -- an error the caller never asked for, attributed to
/// nothing it did, arriving on a call it had already made.
///
/// WHY NOT ReaderWriterLockSlim. The obvious shape -- a read lock around the
/// call, a write lock around the swap -- is wrong, and wrong in a way a passing
/// suite does not reveal. ReaderWriterLockSlim is THREAD-AFFINE: EnterReadLock
/// records the calling thread as a holder, and only that thread may exit it. An
/// /invoke enters before the plugin's first await and exits after its last, on
/// whichever pool thread resumed the continuation -- a different thread. This
/// was built and run, not reasoned about:
///
///   System.Threading.SynchronizationLockException: The read lock is being
///   released without being held.
///      at System.Threading.ReaderWriterLockSlim.ExitReadLock()
///      at RemoteFacadeHost.InstanceHolder.UseAsync[T](Func`2 work)
///
/// The call returned HTTP 500 with an empty body -- no {ok:false} envelope at
/// all -- and, worse, the read count stayed orphaned on the thread that entered:
/// the next DELETE /instance logged "Executing endpoint" and never logged
/// "Executed", blocked in EnterWriteLock for as long as the container was left
/// running, and every later call blocked behind that waiting writer. Note that
/// the two SYNCHRONOUS calls before it reset in 2.4ms and 0.4ms: a suite whose
/// resets happen to sit between synchronous calls passes cleanly over a host
/// that deadlocks the first time a real async plugin method is in flight.
///
/// WHAT THIS DOES INSTEAD. Nothing is held across an await. A call takes a
/// LEASE on the graph -- a reference plus a counted claim on it -- under a
/// short, wholly synchronous lock, and drops the claim when it finishes. Reset
/// retires the current graph, publishes the replacement, and hands the
/// retired one's disposal to whoever puts down the last claim on it, which is
/// Reset itself when nothing is in flight. So:
///
///   - No lock spans user code, so no primitive's thread affinity can matter,
///     and no plugin method can hold the gate.
///   - Reset never blocks on a call. DELETE /instance stays answerable even
///     when a plugin method is wedged -- which matters, because recovering
///     from exactly that is why Reset exists.
///   - A call finishes against the graph it started on; a call arriving after
///     the swap gets the new one immediately.
///   - Disposal happens exactly once per graph, after its last call, outside
///     the lock (plugin Dispose() code must not run under it).
///   - A plugin Dispose() that THROWS is reported, not propagated, when it
///     runs on the deferred path -- see Release(). It would otherwise unwind
///     past an innocent caller's already-completed result.
/// </summary>
public sealed class InstanceHolder(Func<HostedGraph> factory)
{
    /// <summary>
    /// One graph plus the bookkeeping that decides when it may be disposed.
    ///
    /// <c>Callers</c> counts calls currently inside <c>Graph</c>.
    /// <c>Retired</c> means a Reset has stopped publishing it. Once retired,
    /// no new caller can obtain it -- they get whatever <c>_current</c> is now
    /// -- so <c>Callers</c> only ever falls, and "retired with no callers"
    /// happens exactly once. That is what makes disposal exactly-once without
    /// relying on the plugin's Dispose() being idempotent, which nothing
    /// guarantees of a third-party type.
    /// </summary>
    private sealed class Lease(HostedGraph graph)
    {
        public HostedGraph Graph { get; } = graph;
        public int Callers;
        public bool Retired;
    }

    // Guards _current and every Lease's fields. Only ever held across a few
    // field reads and writes -- never across an await, and never while plugin
    // code (including Dispose) runs.
    private readonly object _sync = new();

    private Lease _current = new(factory());

    /// <summary>
    /// Runs <paramref name="work"/> against a graph that cannot be disposed
    /// until it returns, however many times it awaits in between. Callers must
    /// not hold the graph, or anything resolved from it, past the call.
    /// </summary>
    public async Task<T> UseAsync<T>(Func<HostedGraph, Task<T>> work)
    {
        var lease = Acquire();
        try
        {
            return await work(lease.Graph);
        }
        finally
        {
            Release(lease);
        }
    }

    /// <summary>
    /// The synchronous twin, for a reader that never awaits (/services reads a
    /// name list). Kept separate rather than wrapped in Task.FromResult so the
    /// caller does not have to fake an async signature to read a field.
    /// </summary>
    public T Use<T>(Func<HostedGraph, T> work)
    {
        var lease = Acquire();
        try
        {
            return work(lease.Graph);
        }
        finally
        {
            Release(lease);
        }
    }

    public void Reset()
    {
        // Build the replacement BEFORE retiring the old one: if factory()
        // throws (a startup that fails, say), _current must stay pointed at the
        // still-valid old graph, not a disposed one that every later call would
        // then hit. Deliberately outside the lock too, so a slow or hanging
        // startup cannot block calls that are only trying to read _current.
        var next = factory();

        Lease retired;
        bool disposeNow;

        lock (_sync)
        {
            retired = _current;
            retired.Retired = true;

            // Nothing in flight, so this Reset is the last claim-holder and
            // disposes right here -- the ordinary case, and the one the
            // existing "DELETE /instance disposes an IDisposable root" cases
            // observe synchronously.
            disposeNow = retired.Callers == 0;

            _current = new Lease(next);
        }

        if (disposeNow) retired.Graph.Dispose();
    }

    private Lease Acquire()
    {
        lock (_sync)
        {
            var lease = _current;
            lease.Callers++;
            return lease;
        }
    }

    private void Release(Lease lease)
    {
        bool disposeNow;

        lock (_sync)
        {
            disposeNow = --lease.Callers == 0 && lease.Retired;
        }

        if (!disposeNow) return;

        // Outside the lock: HostedGraph.Dispose() runs the plugin's own
        // Dispose(), which is arbitrary user code and must not run holding a
        // gate that every other call needs.
        try
        {
            lease.Graph.Dispose();
        }
        catch (Exception ex)
        {
            // Reported, never rethrown -- and this asymmetry with Reset() is
            // the whole point.
            //
            // We are inside some /invoke's finally, and that call has ALREADY
            // COMPLETED: its result is sitting in the return value this throw
            // would unwind past, turning a successful call into an empty HTTP
            // 500 with no envelope the client can parse. The caller did
            // nothing wrong. It is being punished for happening to be the last
            // one out of a graph somebody else reset -- which caller that is
            // depends on scheduling, so the same plugin defect would destroy a
            // different, arbitrary request every run. That is indistinguishable
            // from a flaky host.
            //
            // Reset() deliberately does NOT swallow: when nothing is in flight
            // it disposes on the DELETE /instance thread and lets the throw
            // reach the operator who asked for the reset. That is the person
            // who wants to know their plugin's Dispose() is broken. Here there
            // is no such person -- only a bystander -- so the container log is
            // the honest channel.
            Console.Error.WriteLine(
                "[InstanceHolder] a retired graph's Dispose() threw on the deferred path " +
                "(the last in-flight call released it). The call itself was unaffected; " +
                $"the graph may be partly undisposed: {ex}");
        }
    }
}
