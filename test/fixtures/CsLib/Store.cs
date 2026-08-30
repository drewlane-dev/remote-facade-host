using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsLib;

// Value() exercises the "anything else, deserialised directly" shape.
// CountAsync()/Ping()/FailAsync() exercise the same four return shapes as
// Store's own members do -- Task<T>, void, and Task (the last doubling as the
// exception-fidelity case) -- through a DEPENDENCY rather than the service
// being called, so a substitution's return shapes are covered too.
public interface IStamp
{
    string Value();
    Task<int> CountAsync();
    void Ping();
    Task FailAsync();

    // The dependency-side twin of IStore.VtValueAsync/VtVoidAsync. ValueTask
    // is a struct, so it is the shape most easily mishandled by any dispatch
    // that reasons about awaitables by reference -- a default ValueTask reads
    // as a completed one carrying null, with no error anywhere.
    ValueTask<string> VtValueAsync();
    ValueTask VtPingAsync();
}

public sealed class RealStamp : IStamp
{
    public string Value() => "real";
    public Task<int> CountAsync() => Task.FromResult(1);
    public void Ping() { }
    public Task FailAsync() => Task.CompletedTask;
    public ValueTask<string> VtValueAsync() => new("real-vt");
    public ValueTask VtPingAsync() => default;
}

// A fake compiled INTO the plugin. Mocks from the test process cannot be
// injected into a remote instance, so a fake has to be a type the plugin
// itself carries; a startup that registers it is how you substitute behaviour
// (see FakeStampStartup).
public sealed class FakeStamp : IStamp
{
    public string Value() => "fake";
    public Task<int> CountAsync() => Task.FromResult(0);
    public void Ping() { }
    public Task FailAsync() => Task.CompletedTask;
    public ValueTask<string> VtValueAsync() => new("fake-vt");
    public ValueTask VtPingAsync() => default;
}

// A real IStamp implementation that ALSO exposes a public method the
// interface does not declare. Exists so "only the interface is served" can be
// tested NON-vacuously: probing for a method that exists nowhere would pass
// even with the guard deleted, whereas Secret() is genuinely present on the
// served object and genuinely outside IStamp.
public sealed class SecretStamp : IStamp
{
    public string Value() => "secret-stamp";
    public Task<int> CountAsync() => Task.FromResult(7);
    public void Ping() { }
    public Task FailAsync() => Task.CompletedTask;
    public ValueTask<string> VtValueAsync() => new("secret-vt");
    public ValueTask VtPingAsync() => default;
    public string Secret() => "LEAKED";
}

// A CONCRETE dependency, the shape a real library usually has — e.g.
// GitManager(GitConfigManager). Not sealed, method virtual, so it can be
// substituted; a sealed class with non-virtual members could not be.
public class Inner { public virtual string Describe() => "inner-real"; }

public sealed class FakeInner : Inner { public override string Describe() => "inner-fake"; }

public sealed class Outer(Inner inner)
{
    public string Describe() => inner.Describe();
}

// A concrete dependency the REGISTRAR wires with a FACTORY, not a bare
// type-to-itself mapping -- the shape a real app uses when a concrete
// dependency needs a value only the app has (config, secrets, ...).
// Auto-registering Configured as NeedsConfigured's nested dependency must
// leave this alone rather than clobbering it with a plain default-constructed
// instance.
public class Configured { public virtual string Tag => "default"; }

public sealed class ConfiguredFromFactory : Configured { public override string Tag => "factory"; }

public sealed class NeedsConfigured(Configured configured)
{
    public string Describe() => configured.Tag;
}

// The registration method a real application already has. LIB_REGISTRAR points
// the host at one of these so the graph wires itself with the app's own
// lifetimes, instead of every interface being enumerated in an env var.
public static class Registration
{
    public static IServiceCollection AddCsLib(this IServiceCollection services)
    {
        services.AddSingleton<IStamp, RealStamp>();
        services.AddSingleton<Configured>(_ => new ConfiguredFromFactory());

        // Store itself, so this method is a complete composition root rather
        // than a fragment. v2 could name the class in LIB_TYPE and let the
        // host construct it alongside whatever this registered; v3 has no such
        // side channel, so the startup must register everything it serves.
        var root = Environment.GetEnvironmentVariable("STORE_ROOT") ?? "/tmp";
        services.Configure<StoreOptions>(o => o.RootPath = root);
        services.AddSingleton<Store>();
        services.AddSingleton<IStore>(sp => sp.GetRequiredService<Store>());
        return services;
    }
}

// Exercises all four return shapes the client must handle, plus failure paths
// that /invoke must convert to a structured {ok:false} error instead of an
// unhandled 500: an async method that throws (FailAsync), and, from the host
// test harness, a malformed argument passed to an existing method.
public interface IStore
{
    Task WriteAsync(string name, string content);
    Task<string> ReadAsync(string name);
    int Count();
    void Touch(string name);
    Task FailAsync();
    void Lock();
    int LockCount();
    string Stamp();

    // Pass-throughs to IStamp's other members, so a client driving Store
    // over /invoke can exercise a substituted IStamp's Task<T>, void,
    // and Task (throwing) shapes -- IStamp itself is never invoked directly
    // from the test process, only through Store.
    Task<int> StampCountAsync();
    void StampPing();
    Task StampFailAsync();

    // The ValueTask pass-throughs. IStamp is never invoked directly from the
    // test process, only through Store, so these are how a raw /invoke can
    // observe what the dependency actually returned.
    ValueTask<string> StampVtValueAsync();
    ValueTask StampVtPingAsync();

    // Exists only so /invoke's boundary checks have something real to
    // reject: a ref parameter has no JSON representation to bind (it's a
    // location to write to, not a value to read), and an open generic
    // method has no type argument for reflection to close over. Both must
    // be caught before Invoker attempts argument binding or method.Invoke,
    // with a message naming the method and the reason -- not left to
    // surface as an incidental reflection or JSON error.
    void RefArg(ref int x);
    T Echo<T>(T value);

    // A return type System.Text.Json refuses outright -- System.Type is
    // explicitly unsupported (it throws NotSupportedException, not a
    // graceful "{}"), which makes it a deterministic, no-setup way to
    // exercise Invoker's return-serialization guard. Never actually reached
    // by a passing call; only by the boundary test that expects it to fail.
    Type BadReturn();

    // The argument-side twin of BadReturn: System.Type also refuses to be
    // DESERIALIZED, not just serialized (NotSupportedException either way).
    // Exercises the same attribution Invoker owes a bad argument as it owes
    // a bad return value -- whichever exception type System.Text.Json
    // actually throws, not just the ones a narrower catch happened to name.
    void TypeArg(Type t);

    // A return type System.Text.Json CAN attempt to serialize but fails on
    // for a reason that has nothing to do with the type itself -- a
    // MemoryStream's ReadTimeout getter throws InvalidOperationException
    // ("Timeouts are not supported on this stream."), a message that reads
    // like a network fault. Exercises that an attribution catch scoped to
    // only NotSupportedException/JsonException would miss this one.
    Stream StreamReturn();

    // ValueTask / ValueTask<T>: awaitable, but NOT Tasks -- both are
    // STRUCTS, so a host that only tests `result is Task` misses them and
    // hands the ValueTask itself to System.Text.Json as DATA. Both do their
    // real work AFTER a delay on purpose: a host that never awaits them
    // returns BEFORE the file is written and BEFORE the value exists, so the
    // failure shows up in the VALUE that arrives (or the missing file), not
    // merely in the {ok:true} envelope -- which is exactly what the broken
    // version produced.
    ValueTask VtVoidAsync(string name);
    ValueTask<string> VtValueAsync();

    // Reports the UTC tick window over which the call ACTUALLY ran, as
    // "startTicks:endTicks". Exists so a caller driving two instances can
    // assert their execution windows OVERLAP -- server-side truth about
    // concurrency, from the two containers themselves. If the client proxy
    // blocks, the second call cannot even start until the first has returned,
    // so the windows are disjoint BY CONSTRUCTION and no wall-clock
    // tolerance is involved in telling the two cases apart.
    Task<string> SleepWindowAsync(int ms);

    // The declared return type is the BASE of a System.Text.Json
    // polymorphic hierarchy ([JsonPolymorphic]/[JsonDerivedType] below);
    // the real instance handed back is the DERIVED type. Whether the wire
    // preserves that distinction depends on what TYPE Invoker declares when
    // it serializes -- see PolyBase/PolyDerived and the comment in
    // Invoker.cs beside SerializeToElement.
    PolyBase PolyReturn();
}

// Deliberately not abstract: an abstract base would make the "$type" bug's
// failure mode a thrown exception client-side (can't instantiate an
// abstract type), which is dramatic but awkward to assert past in a test
// program that keeps running afterward. A concrete base makes the failure
// mode a SILENT downgrade instead -- PolyReturn() still "succeeds" and
// returns a PolyBase, just the wrong one, with Extra quietly gone -- which
// is both what the finding actually described as the worse case, and easy
// to assert against without exception handling.
[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(PolyDerived), "derived")]
public class PolyBase
{
    public int BaseValue { get; set; } = 1;
}

public sealed class PolyDerived : PolyBase
{
    public string Extra { get; set; } = "extra-value";
}

public sealed class StoreOptions { public string RootPath { get; set; } = "/tmp"; }

// The dominant .NET constructor shape: IOptions<T> plus ILogger<T>.
public sealed class Store(IOptions<StoreOptions> options, ILogger<Store> logger, IStamp stamp) : IStore
{
    // Ensures RootPath exists rather than requiring callers to pre-create it —
    // notably so a private per-test subdirectory under /tmp (rather than /tmp
    // itself, which the .NET runtime also writes diagnostic pipe/socket files
    // into) can be handed in without a separate provisioning step.
    private readonly string _root = EnsureDirectory(options.Value.RootPath);

    // In-memory state that a genuinely new instance does NOT carry over,
    // unlike a file on disk. Exists to prove DELETE /instance really
    // constructs a new object rather than merely returning 204 — see
    // InstanceHolder.Reset().
    private int _lockCount;

    public string Stamp() => stamp.Value();

    public Task<int> StampCountAsync() => stamp.CountAsync();
    public void StampPing() => stamp.Ping();
    public Task StampFailAsync() => stamp.FailAsync();

    public ValueTask<string> StampVtValueAsync() => stamp.VtValueAsync();
    public ValueTask StampVtPingAsync() => stamp.VtPingAsync();

    public Task WriteAsync(string name, string content)
    {
        logger.LogInformation("writing {Name}", name);
        return File.WriteAllTextAsync(Path.Combine(_root, name), content);
    }

    public Task<string> ReadAsync(string name) => File.ReadAllTextAsync(Path.Combine(_root, name));

    public int Count() => Directory.GetFiles(_root).Length;

    public void Touch(string name) => File.WriteAllText(Path.Combine(_root, name), "touched");

    // Deliberately faults AFTER yielding, so the exception surfaces on the
    // returned Task (observed at `await task` in Invoker) rather than
    // synchronously from method.Invoke — the case that used to escape /invoke
    // as an unhandled 500 instead of a structured {ok:false} error.
    public async Task FailAsync()
    {
        await Task.Yield();
        throw new InvalidOperationException("deliberate failure for the async-exception test");
    }

    public void Lock() => _lockCount++;

    public int LockCount() => _lockCount;

    // Never actually reached over /invoke -- Invoker's boundary checks must
    // reject both calls before either body runs.
    public void RefArg(ref int x) => x++;
    public T Echo<T>(T value) => value;

    // Runs to completion and returns a real value -- it's the SERIALIZING
    // of that value, back in Invoker, that must fail and be reported.
    public Type BadReturn() => typeof(Store);

    // Never actually reached -- deserializing the JSON argument into
    // System.Type is what must fail and be attributed.
    public void TypeArg(Type t) { }

    // Runs to completion and returns a real, readable MemoryStream -- it's
    // System.Text.Json's OWN attempt to serialize it that fails.
    public Stream StreamReturn() => new MemoryStream([1, 2, 3]);

    // The work happens after the delay, so "was it awaited?" is observable:
    // an un-awaited ValueTask lets the host answer before the file exists,
    // and the ReadAsync that follows fails instead of returning "vt-written".
    public async ValueTask VtVoidAsync(string name)
    {
        await Task.Delay(50);
        await File.WriteAllTextAsync(Path.Combine(_root, name), "vt-written");
    }

    public async ValueTask<string> VtValueAsync()
    {
        await Task.Delay(50);
        return "vt-value";
    }

    public async Task<string> SleepWindowAsync(int ms)
    {
        var start = DateTime.UtcNow.Ticks;
        await Task.Delay(ms);
        return $"{start}:{DateTime.UtcNow.Ticks}";
    }

    // Runs to completion and returns a real, populated PolyDerived -- it's
    // whether Invoker's serialization preserves that concrete type on the
    // wire that this exists to prove.
    public PolyBase PolyReturn() => new PolyDerived();

    private static string EnsureDirectory(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

/// <summary>A root type whose constructor takes a plain string — impossible to
/// construct via ActivatorUtilities without a registration.</summary>
public class StringRooted(string label)
{
    public string Label() => label;
}

/// <summary>An interface used as LIB_TYPE, resolved from the provider.</summary>
public interface IRootFacade { string Who(); }

public sealed class RootFacade : IRootFacade
{
    public string Who() => "root-facade";
}

/// <summary>A Scoped registration -- a remote call has no scope for it to
/// live in, so resolving it must be rejected by name rather than silently
/// served from the root provider.</summary>
public interface IScopedThing { string Say(); }

public sealed class ScopedThing : IScopedThing
{
    public string Say() => "scoped";
}

/// <summary>An interface implemented EXPLICITLY. An explicit implementation
/// compiles to a private, specially-named method ("CsLib.IExplicitThing.Go")
/// on the concrete class -- Type.GetMethods() on the CONCRETE type does not
/// return it at all (it is not public), and even with non-public binding
/// flags its Name would not equal the plain method name a caller sends. Only
/// GetMethods() on the INTERFACE type itself finds "Go", and invoking that
/// MethodInfo against the concrete instance dispatches correctly through the
/// interface. Exists to pin, non-vacuously, which type /invoke's
/// service-routing branch must hand to Invoker.</summary>
public interface IExplicitThing { string Go(); }

public sealed class ExplicitThing : IExplicitThing
{
    string IExplicitThing.Go() => "explicit";
}

/// <summary>Registered BOTH AddScoped and AddSingleton for the SAME
/// interface, without Replace -- DI resolves the LAST registration, so this
/// interface actually serves as the SINGLETON despite an earlier Scoped
/// registration for it. Exists to prove scoped-detection follows the
/// descriptor that WINS resolution, not "any descriptor that matches": a
/// naive scan would see the Scoped registration and reject this service,
/// refusing a call the container would have served correctly.</summary>
public interface IScopedThenSingleton { string Say(); }

public sealed class ScopedThenSingletonLoser : IScopedThenSingleton
{
    public string Say() => "should-never-serve";
}

public sealed class ScopedThenSingletonWinner : IScopedThenSingleton
{
    public string Say() => "singleton-wins";
}

/// <summary>A singleton whose count proves a REBUILD happened, not merely a
/// re-resolve: rebuilding the provider means a brand-new Counter, so Next()
/// starts again at 1. A file on disk, or the type name /health reports, would
/// read identically whether the provider was rebuilt or kept.
///
/// HoldThenNextAsync/InFlight exist so a reset can be driven against calls
/// that are PROVABLY still in flight rather than probably still in flight:
/// the call parks INSIDE the service until a file appears, and InFlight
/// reports, from the host's own memory, how many calls are parked there right
/// now. Timing two curl containers' start-up and hoping is not evidence, and
/// the defect this guards against is a race -- a test that only usually
/// overlaps only usually tests anything.
///
/// The park loop awaits, repeatedly and for real. That is the point: a gate
/// held across an await is exited by whichever pool thread resumes the
/// continuation, not the one that entered it, which is why InstanceHolder
/// cannot use a thread-affine primitive.
///
/// Deliberately NOT IDisposable. An earlier version made Counter the sentinel
/// for "the retired graph was disposed exactly once" -- which it could never
/// observe: Counter is PROVIDER-OWNED, and ServiceProvider.Dispose() is
/// idempotent (measured: three calls, one disposal), so a double
/// HostedGraph.Dispose() reached Counter.Dispose() exactly once either way.
/// The assertion pinned deferral only, under a name that promised more. The
/// sentinel is now CsLib.DisposableRoot, constructed by ActivatorUtilities and
/// therefore disposed by HostedGraph EXPLICITLY, where a second disposal does
/// append a second line.</summary>
public interface ICounter
{
    int Next();
    Task<int> HoldThenNextAsync(string releasePath);
    int InFlight();
}

public sealed class Counter(IServiceProvider services) : ICounter
{
    private int _n;
    private int _inFlight;

    // Interlocked, not ++: two HoldThenNextAsync calls are released
    // concurrently by construction, and a lost update would make the test
    // flake rather than fail.
    public int Next() => Interlocked.Increment(ref _n);

    public int InFlight() => Volatile.Read(ref _inFlight);

    public async Task<int> HoldThenNextAsync(string releasePath)
    {
        Interlocked.Increment(ref _inFlight);
        try
        {
            // A deadline, so a mistake in the test fails the suite rather than
            // hanging it forever.
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (!File.Exists(releasePath) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            // Touches the PROVIDER after the park, not merely this object's
            // own field. That is the failure this case exists to catch: a
            // reset that disposes the graph a call is inside surfaces at the
            // caller as an ObjectDisposedException from a call that never
            // asked for a reset. Counter's own _n survives its Dispose()
            // perfectly well, so without this the two parked calls would
            // still answer 1 and 2 with the provider pulled out from under
            // them -- the assertion would pass against the very bug it names.
            _ = services.GetService(typeof(IRootFacade));

            return Interlocked.Increment(ref _n);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

/// <summary>Registers both shapes the new resolution path must support.</summary>
public static class GraphStartup
{
    public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton(services, _ => new StringRooted("from-factory"));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IRootFacade, RootFacade>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddScoped<IScopedThing, ScopedThing>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IExplicitThing, ExplicitThing>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddScoped<IScopedThenSingleton, ScopedThenSingletonLoser>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IScopedThenSingleton, ScopedThenSingletonWinner>(services);
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<ICounter, Counter>(services);
        // Inherited-interface-member coverage for the service-routed path.
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IDerivedFacade, DerivedFacade>(services);
        // Registered, never constructed until something asks for it -- the
        // host must start normally and fail only at the call that resolves it.
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IThrowingCtor, ThrowingCtorService>(services);
    }
}

// --- review round 1 fixtures ---

public sealed class DisposableOptions { public string SentinelPath { get; set; } = "/tmp/disposed-sentinel"; }

/// <summary>An IDisposable root. Dispose() APPENDS a line rather than
/// overwriting, so a test can tell "disposed once" from "disposed more than
/// once" by counting lines in the sentinel file -- an overwrite would look
/// identical either way and could not catch a double-dispose regression.</summary>
public sealed class DisposableRoot(IOptions<DisposableOptions> options) : IDisposable
{
    private readonly string _sentinelPath = options.Value.SentinelPath;

    public string Ping() => "alive";

    public void Dispose() => File.AppendAllText(_sentinelPath, "disposed\n");
}

/// <summary>Registers DisposableRoot itself, so it is resolved FROM the
/// container rather than built by ActivatorUtilities -- the shape that must
/// NOT be disposed a second time by HostedGraph, since the container already
/// tracks and disposes it.</summary>
public static class DisposableRootStartup
{
    public static void Configure(IServiceCollection services)
    {
        // Env var rather than LIB_OPTIONS, which v3 removed. Each test needs
        // its OWN sentinel file: they count lines to tell "disposed once" from
        // "disposed twice", and a shared path would make every run after the
        // first read the previous run's writes.
        var sentinel = Environment.GetEnvironmentVariable("SENTINEL_PATH");
        if (!string.IsNullOrWhiteSpace(sentinel))
        {
            services.Configure<DisposableOptions>(o => o.SentinelPath = sentinel);
        }

        services.AddSingleton<DisposableRoot>();

        // v2 paired LIB_TYPE=DisposableRoot with LIB_REGISTRAR=GraphStartup.
        // One registrar is all there is now, so composing is how two sets of
        // registrations combine -- which is ordinary C#, and the point.
        GraphStartup.Configure(services);
    }
}

/// <summary>The throwing twin, registered the same way.</summary>
public static class ThrowingDisposableRootStartup
{
    public static void Configure(IServiceCollection services)
    {
        var sentinel = Environment.GetEnvironmentVariable("SENTINEL_PATH");
        if (!string.IsNullOrWhiteSpace(sentinel))
        {
            services.Configure<DisposableOptions>(o => o.SentinelPath = sentinel);
        }

        services.AddSingleton<ThrowingDisposableRoot>();
        GraphStartup.Configure(services);
    }
}

/// <summary>Registers DisposableRoot as a ready-made INSTANCE -- the
/// ordinary `services.AddSingleton&lt;T&gt;(existingInstance)` LIB_REGISTRAR
/// pattern -- rather than a type or factory mapping. The provider RETURNS
/// this on GetService, but did not CREATE it, so .NET's own container never
/// tracks it for disposal, unlike DisposableRootStartup above. This is the
/// shape that exposed "the provider returned it" != "the provider will
/// dispose it": HostedGraph must dispose this one itself.</summary>
public static class DisposableRootInstanceStartup
{
    public static void Configure(IServiceCollection services)
        => services.AddSingleton(new DisposableRoot(
            Options.Create(new DisposableOptions { SentinelPath = "/tmp/disposed-instance" })));
}

/// <summary>An IDisposable root whose Dispose() APPENDS a line and then
/// THROWS.
///
/// Exists for the one path where a plugin's Dispose() has no right person to
/// report to: the DEFERRED path, where the retired graph is disposed by
/// whichever in-flight call happened to finish last. That caller's own work
/// has already SUCCEEDED by then, so a propagating throw would unwind past its
/// result and turn it into an empty HTTP 500 -- punishing a bystander for a
/// defect it had nothing to do with, and picking a different arbitrary request
/// each run, since which caller is last depends on scheduling.
///
/// It appends BEFORE throwing so a test can tell "Dispose ran and threw" from
/// "Dispose never ran" -- without that line, a holder that silently skipped
/// disposal entirely would pass the same assertions.
///
/// Reuses DisposableOptions rather than adding a second options type: what
/// distinguishes this fixture is the throw, not its configuration.</summary>
public sealed class ThrowingDisposableRoot(IOptions<DisposableOptions> options) : IDisposable
{
    private readonly string _sentinelPath = options.Value.SentinelPath;

    public string Ping() => "alive";

    public void Dispose()
    {
        File.AppendAllText(_sentinelPath, "disposed\n");
        throw new InvalidOperationException("plugin Dispose() deliberately threw");
    }
}

/// <summary>A concrete type with zero PUBLIC constructors for a reason other
/// than being an interface -- the case the guard removed from Activation.cs
/// (LIB_TYPE with no public constructor is no longer a hard error there)
/// must still fail, just later, by naming the type.</summary>
public sealed class PrivateCtorRoot
{
    private PrivateCtorRoot() { }
    public string Ping() => "unreachable";
}

/// <summary>A root with more than one public constructor, one marked
/// [ActivatorUtilitiesConstructor]. Registered via the container rather than
/// left for ActivatorUtilities, so which constructor actually runs pins the
/// container's OWN selection -- it is not obliged to honour the attribute,
/// which is an ActivatorUtilities-only convention.</summary>
public sealed class MultiCtorRoot
{
    public string Which { get; }

    [ActivatorUtilitiesConstructor]
    public MultiCtorRoot(IOptions<StoreOptions> options)
    {
        Which = "attributed-one";
    }

    public MultiCtorRoot(IOptions<StoreOptions> options, ILogger<MultiCtorRoot> logger)
    {
        Which = "two";
    }

    public string WhichCtor() => Which;
}

public static class MultiCtorStartup
{
    public static void Configure(IServiceCollection services) => services.AddSingleton<MultiCtorRoot>();
}

// --- review round 3 fixtures ---

/// <summary>A trivially-constructible root, used to prove a KEYED
/// registration for the SAME type does not crash construction of the
/// UNKEYED root. provider.GetService (unkeyed) does not resolve a keyed
/// registration, so the root still falls through to ActivatorUtilities --
/// the point is that reaching that fallback must not throw first.</summary>
public sealed class KeyedProbe
{
    public string Ping() => "keyed-probe-alive";
}

/// <summary>Registers BOTH an unkeyed AND a keyed singleton for KeyedProbe's
/// own type, unkeyed FIRST. provider.GetService(typeof(KeyedProbe))
/// (unkeyed) resolves to the unkeyed registration regardless of order -- so
/// construction succeeds today. But a scan like
/// `services.LastOrDefault(d => d.ServiceType == rootType)` does not
/// distinguish keyed from unkeyed and simply returns the LAST descriptor
/// with a matching ServiceType, which is the KEYED one here. Reading
/// ServiceDescriptor.ImplementationInstance on THAT descriptor throws
/// InvalidOperationException("This service descriptor is keyed..."), even
/// though the instance actually served came from the unkeyed path and never
/// touched that property. Order matters: this is why the keyed registration
/// is added SECOND.</summary>
public static class KeyedServiceStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<KeyedProbe>();
        services.AddKeyedSingleton<KeyedProbe>("probe-key");
    }
}

/// <summary>An OPEN GENERIC root. Dispose() appends a line to a sentinel
/// file, same convention as DisposableRoot -- so a double-dispose shows as
/// two lines, not one.</summary>
public sealed class GenericDisposableRoot<T> : IDisposable
{
    public string Ping() => "generic-alive";
    public void Dispose() => File.AppendAllText("/tmp/disposed-generic", "disposed\n");
}

/// <summary>The type argument GenericDisposableRoot&lt;T&gt; is closed over
/// for the open-generic test. Its own identity does not matter; it exists
/// so LIB_TYPE can name a genuinely closed generic type.</summary>
public sealed class OpenGenericArg { }

/// <summary>Registers GenericDisposableRoot&lt;&gt; as an OPEN generic
/// mapping -- the shape that has no descriptor whose ServiceType equals the
/// CLOSED root type, which is what made the descriptor-scan approach
/// compute ownership wrong (false) and double-dispose it.</summary>
public static class OpenGenericStartup
{
    public static void Configure(IServiceCollection services)
        => services.AddSingleton(typeof(GenericDisposableRoot<>));
}

// --- final fix-wave fixtures ---

/// <summary>A service whose CONSTRUCTOR throws something OTHER than
/// InvalidOperationException. DI does not wrap a constructor's exception, so
/// this propagates unwrapped out of provider.GetService -- past a catch that
/// names only InvalidOperationException, past UseAsync, and out to Kestrel as
/// an empty HTTP 500 with the message reaching only the container log.
///
/// ArgumentException specifically: InvalidOperationException would be caught
/// by the pre-existing branch and prove nothing about the new one. A
/// composition root's constructor is the most likely place for a wiring
/// mistake to throw, which is the whole reason wiring moved into C#.</summary>
public interface IThrowingCtor { string Boom(); }

public sealed class ThrowingCtorService : IThrowingCtor
{
    public ThrowingCtorService()
        => throw new ArgumentException("wiring is wrong: no connection string");

    public string Boom() => "never";
}

/// <summary>A base interface whose member has to stay reachable through a
/// DERIVED one.
///
/// Type.GetMethods() returns inherited members for a CLASS but NOT for an
/// INTERFACE -- it returns only what that interface itself declares. v1.0
/// always dispatched against the concrete class, so nothing in this fixture
/// could observe the difference; v1.1 dispatches against the interface on
/// BOTH new paths (a "service"-routed call and LIB_TYPE naming an
/// interface), so FromBase() was unreachable while FromDerived() worked.
///
/// FromDerived() is the paired positive control: it is DECLARED on
/// IDerivedFacade, so it is found either way. Without it, an assertion on
/// FromBase() alone could not tell "inherited members are reachable" from
/// "this fixture is reachable at all".</summary>
public interface IBaseFacade { string FromBase(); }

public interface IDerivedFacade : IBaseFacade { string FromDerived(); }

public sealed class DerivedFacade : IDerivedFacade
{
    public string FromBase() => "base-method";
    public string FromDerived() => "derived-method";
}

/// <summary>A plain IDisposable singleton the PROVIDER owns and creates, so
/// it is disposed only if provider.Dispose()/DisposeAsync() actually runs.
/// Appends rather than overwrites, so "disposed once" is distinguishable
/// from "disposed twice". It is the instrument for two separate defects:
/// a root whose Dispose() throws used to skip provider disposal entirely,
/// and a sync provider disposal used to abort part-way through the tracked
/// list.</summary>
public sealed class OwnedResource : IDisposable
{
    public string Ping() => "owned";

    public void Dispose() => File.AppendAllText("/tmp/owned-disposed", "disposed\n");
}

/// <summary>A singleton implementing ONLY IAsyncDisposable -- ordinary in
/// modern .NET, and absent from this fixture until now.
/// ServiceProvider.Dispose() (synchronous) THROWS for one of these ("only
/// implements IAsyncDisposable. Use DisposeAsync to dispose the
/// container."), which made every DELETE /instance return 500 and abort
/// disposal once such a service had been resolved. Impossible in v1.0,
/// which never disposed the provider at all.</summary>
public sealed class AsyncOnlyResource : IAsyncDisposable
{
    public string Ping() => "async-only";

    public ValueTask DisposeAsync()
    {
        File.AppendAllText("/tmp/async-disposed", "disposed\n");
        return default;
    }
}

/// <summary>An IAsyncDisposable-ONLY root, named as LIB_TYPE and registered
/// by nothing, so ActivatorUtilities builds it and nothing but HostedGraph
/// can dispose it. Before the fix HostedGraph only looked for IDisposable,
/// so a root of this shape was disposed by nobody at all.</summary>
public sealed class AsyncOnlyRoot : IAsyncDisposable
{
    public string Ping() => "alive";

    public ValueTask DisposeAsync()
    {
        File.AppendAllText("/tmp/async-root-disposed", "disposed\n");
        return default;
    }
}

/// <summary>Pairs a throwing-Dispose root with a provider-owned IDisposable
/// singleton -- the combination that shows a throwing root taking every
/// singleton in the graph down with it. GraphStartup could not show it:
/// nothing it registers is IDisposable.</summary>
public static class ThrowingRootOwnedStartup
{
    public static void Configure(IServiceCollection services) => services.AddSingleton<OwnedResource>();
}

/// <summary>Registers both disposal shapes. The TEST resolves OwnedResource
/// FIRST and AsyncOnlyResource SECOND on purpose: the container disposes in
/// reverse creation order, so the async-only one is reached first and, under
/// synchronous disposal, throws before OwnedResource is ever reached. Both
/// sentinels absent is then the signature of "disposal aborted part-way",
/// which one sentinel alone could not distinguish from "disposal ran".</summary>
public static class AsyncOnlyStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<OwnedResource>();
        services.AddSingleton<AsyncOnlyResource>();
    }
}

/// <summary>Registers IStore -> Store, and Store's own IStamp dependency,
/// from a REGISTRAR rather than from the interface-to-implementation map that
/// v3 accepted as LIB_SERVICES.
///
/// Exists to de-confound the unbindable-LIB_OPTIONS guard. The first version
/// of that test used CsLib.IRootFacade, whose implementation takes no
/// IOptions&lt;T&gt; at all -- so it could not distinguish "the host refused
/// because no implementation was NAMED" (the intended reason) from "the host
/// refused because nothing ASKED for options" (a bug the guard had, and one
/// that fired on correct configurations too). CsLib.Store genuinely takes
/// IOptions&lt;StoreOptions&gt;, so this fixture exercises the guard for the
/// one reason it is allowed to fire.</summary>
public static class OptionsFacadeStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IStamp, RealStamp>();

        // Registered BOTH ways, resolving to ONE instance. v2 could name the
        // concrete class as LIB_TYPE and reach every public method on it,
        // including those IStore does not declare. Routing by service name
        // dispatches against the type NAMED, so without a concrete
        // registration those methods would become unreachable -- and the
        // second registration must delegate rather than construct, or the two
        // names would hand out two different objects and the file-locking
        // cases would stop contending with each other.
        services.AddSingleton<Store>();
        services.AddSingleton<IStore>(sp => sp.GetRequiredService<Store>());
    }
}

/// <summary>
/// The startup the converted suite drives. It exists because v3 removed
/// LIB_TYPE and LIB_OPTIONS, so both jobs those did -- naming the type to serve
/// and configuring its options -- now belong here, in ordinary C#.
///
/// Configuration arrives as an environment variable read by this method. That
/// is the migration path for anything that used to be LIB_OPTIONS: the startup
/// is plugin code, so it can read env vars, files, or anything else, and it can
/// do so with the real types rather than through a JSON binder the host owns.
/// </summary>
public static class StoreStartup
{
    public static void Configure(IServiceCollection services)
    {
        var root = Environment.GetEnvironmentVariable("STORE_ROOT") ?? "/tmp";

        services.Configure<StoreOptions>(o => o.RootPath = root);

        // The default. A startup wanting FakeStamp or SecretStamp instead
        // calls this method and then Replace -- see FakeStampStartup.
        services.AddSingleton<IStamp, RealStamp>();

        // Registered BOTH ways, resolving to ONE instance. v2 could name the
        // concrete class as LIB_TYPE and reach every public method on it,
        // including those IStore does not declare. Routing by service name
        // dispatches against the type NAMED, so without a concrete
        // registration those methods would become unreachable -- and the
        // second registration must delegate rather than construct, or the two
        // names would hand out two different objects and the file-locking
        // cases would stop contending with each other.
        services.AddSingleton<Store>();
        services.AddSingleton<IStore>(sp => sp.GetRequiredService<Store>());
    }
}

/// <summary>
/// Real wiring, one thing faked -- the whole of what LIB_SERVICES used to do,
/// in ordinary C#.
///
/// This is the shape the host's removal message points operators at. It
/// composes the REAL composition root (Registration.AddCsLib, an extension
/// method, which is also why this doubles as the case proving a registrar can
/// be one) and then replaces exactly one registration.
///
/// Note what the JSON map could not have said and this does: the lifetime is
/// chosen here. The map always registered Singleton, so substituting a
/// Transient dependency silently changed its semantics as well as its
/// implementation.
/// </summary>
public static class FakeStampStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddCsLib();
        services.Replace(ServiceDescriptor.Singleton<IStamp, FakeStamp>());
    }
}

/// <summary>
/// Deliberately incomplete: registers Store but not the IStamp its constructor
/// needs.
///
/// v2 caught this at startup, because LIB_TYPE made the host construct the
/// root eagerly. v3 resolves per call, so the same mistake surfaces at the
/// first call that needs it -- later, but naming the same missing type, and
/// without the host having to construct anything before it knows what will be
/// asked for.
/// </summary>
public static class IncompleteStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.Configure<StoreOptions>(o => o.RootPath = "/tmp");
        services.AddSingleton<Store>();
        services.AddSingleton<IStore>(sp => sp.GetRequiredService<Store>());
    }
}

/// <summary>
/// Options whose defaults are deliberately absurd. A test asserting the values
/// arrived has to be able to tell "bound from the environment" from "fell back
/// to the defaults", and defaults like "/tmp" or 3 would be indistinguishable
/// from a plausible configured value.
/// </summary>
public sealed class EchoOptions
{
    public string RootPath { get; set; } = "NEVER-SET";
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;
    public int Retries { get; set; } = -1;
    public bool Enabled { get; set; }
    public List<string> Tags { get; set; } = [];
}

public interface IOptionsEcho
{
    string Describe();
}

/// <summary>Reports what the container actually bound, so a test can compare
/// it against what the fixture wrote.</summary>
public sealed class OptionsEcho(IOptions<EchoOptions> options) : IOptionsEcho
{
    public string Describe()
    {
        var o = options.Value;
        return $"{o.RootPath}|{o.Timeout}|{o.Retries}|{o.Enabled}|{string.Join(",", o.Tags)}";
    }
}

/// <summary>The startup a consumer would write: BindOptions, then registrations.</summary>
public static class EchoStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.BindOptions<EchoOptions>();
        services.AddSingleton<IOptionsEcho, OptionsEcho>();
    }
}

/// <summary>The same graph, bound the STOCK way -- no BindOptions, no reference
/// to this package needed. Proves the documented two-line pattern really is
/// equivalent rather than merely claimed to be.</summary>
public static class EchoStockStartup
{
    public static void Configure(IServiceCollection services)
    {
        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

        services.Configure<EchoOptions>(config.GetSection(nameof(EchoOptions)));
        services.AddSingleton<IOptionsEcho, OptionsEcho>();
    }
}

/// <summary>
/// Proves which Microsoft.Data.SqlClient the container actually loaded.
///
/// Constructing a SqlConnection is enough and needs no database: the stub that
/// ships at the package root throws "Microsoft.Data.SqlClient is not supported
/// on this platform" from its constructor, while the real implementation under
/// runtimes/unix/lib constructs fine. So the two outcomes are unambiguous.
/// </summary>
public interface ISqlProbe
{
    string Describe();
}

public sealed class SqlProbe : ISqlProbe
{
    public string Describe()
    {
        // Never opened. The point is which assembly answered, not whether a
        // server is reachable.
        using var connection = new Microsoft.Data.SqlClient.SqlConnection();
        var assembly = connection.GetType().Assembly;
        return $"{assembly.GetName().Version}|{assembly.Location}";
    }
}

public static class SqlProbeStartup
{
    public static void Configure(IServiceCollection services) =>
        services.AddSingleton<ISqlProbe, SqlProbe>();
}

/// <summary>
/// Proves the container is not running in globalization-invariant mode.
///
/// Alpine .NET images are invariant by default, and a plugin doing anything
/// culture-aware then fails with "Globalization Invariant Mode is not
/// supported". Microsoft.Data.SqlClient throws it on the first CONNECTION --
/// long after the assembly loaded fine -- so it reads as a database fault
/// rather than an image one. Asking a culture directly needs no database and
/// fails in the same place.
/// </summary>
public interface IGlobalizationProbe
{
    string Describe();
}

public sealed class GlobalizationProbe : IGlobalizationProbe
{
    public string Describe()
    {
        // The AppContext switch, NOT just whether a culture resolves. Those
        // are different signals: setting the env var makes cultures work while
        // runtimeconfig.json can still say invariant, and the switch is what
        // Microsoft.Data.SqlClient reads before refusing to connect. An earlier
        // version of this probe asked only for a culture and passed against an
        // image SqlClient would not work on.
        AppContext.TryGetSwitch("System.Globalization.Invariant", out var invariant);

        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-GB");
        return $"invariant={invariant}|{culture.Name}|{culture.NumberFormat.CurrencySymbol}";
    }
}

public static class GlobalizationProbeStartup
{
    public static void Configure(IServiceCollection services) =>
        services.AddSingleton<IGlobalizationProbe, GlobalizationProbe>();
}

/// <summary>
/// A controller-shaped type in a PLUGIN, which must never be served.
///
/// The host serves its API from an MVC controller, and MVC discovers
/// controllers BY CONVENTION: ControllerFeatureProvider.IsController accepts
/// any public, non-abstract, non-generic class whose name ends in "Controller",
/// with no base class and no attribute required -- which is why this type can
/// exist here without CsLib referencing any part of ASP.NET Core.
///
/// Discovery is scoped to the host's registered application parts, and a plugin
/// loaded by Assembly.LoadFrom is not one. That is the property under test: a
/// third-party assembly this image loads must not be able to add, replace or
/// shadow a route on the host that loaded it. Nothing enforced this while the
/// endpoints were minimal-API lambdas, because there was no discovery at all.
/// </summary>
public sealed class HijackController
{
    public string Index() => "the plugin answered a route it should not have";
}
