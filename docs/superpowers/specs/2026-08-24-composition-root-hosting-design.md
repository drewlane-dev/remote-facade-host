# Composition-root hosting — design

**Date:** 2026-08-24
**Status:** proposed
**Target version:** v1.1.0 (additive; v1.0 consumers unaffected)
**Supersedes nothing.** Extends `2026-08-23-remote-class-host-design.md`.

## The problem

v1.0 hosts exactly one class, named by `LIB_TYPE`, constructed by
`ActivatorUtilities`. Everything else about the object graph is expressed in
environment variables: `LIB_OPTIONS` for configuration, `LIB_SERVICES` for
substituting an implementation, `LIB_REGISTRAR` for anything those two cannot
say.

Three things follow from that, and all three were hit in practice:

1. **Ordinary constructors do not work.** A parameter that is a `string`, `int`,
   `bool`, `Guid` or enum cannot be supplied at all. `Widget(string rootPath)`
   fails at startup with
   `Unable to resolve service for type 'System.String'`. Measured against the
   published v1.0.1 image.

2. **A registrar cannot construct the root type.** `Activation.Create` calls
   `ActivatorUtilities.CreateInstance(provider, type)` unconditionally, which
   builds the type directly and never asks the container whether it already
   knows how. So
   `services.AddSingleton(sp => new Widget("/from/factory"))` is registered,
   present in the provider, and then bypassed — the same string failure occurs.
   Also measured.

3. **Configuration lives in JSON strings rather than C#.** The one place that
   can express an arbitrary object graph — the registrar — is reachable only for
   dependencies, never for the thing being served.

The consequence is that expressing a real application's wiring means fighting
three partial mechanisms, none of which is as capable as the language.

## The change

The container stops being *one instance of a class* and becomes *a host for a
composition root*.

The consumer writes two things:

```csharp
// 1. Startup — all wiring, in real C#.
public static class RemoteStartup
{
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IGitConfig>(new GitConfig("/mnt/share", depth: 50));
        services.AddSingleton(sp => new GitManager("/mnt/share",
                                                  sp.GetRequiredService<IGitConfig>()));
        services.AddSingleton<ITestFacade, TestFacade>();
    }
}

// 2. Facade — the surface the test drives. Simple values only.
public interface ITestFacade
{
    Task<string> CommitAllAsync(string message);
    Task<int>    PendingChangesAsync();
}
```

The container is told only where the composition root is:

```
LIB_DIR=/plugin
LIB_ASSEMBLY=TestSupport.dll
LIB_REGISTRAR=TestSupport.RemoteStartup.Configure
```

and the client names the facade in C#, where the compiler checks it:

```csharp
await using var remote = RemoteHost.At("http://rch-a:8080");
var git = await remote.GetAsync<ITestFacade>();
await git.CommitAllAsync("a message");
```

`LIB_TYPE` is no longer required. `LIB_OPTIONS`, `LIB_SERVICES` and
`LIB_ARGS`-shaped needs are all expressible in the startup instead — a factory
registration handles every constructor shape the language allows, including
strings, primitives, optional parameters and `params`.

## Why not `LIB_ARGS`

A `LIB_ARGS` map of constructor-parameter-name to JSON value was designed and
rejected. It would have been a worse re-implementation of a constructor call:
it cannot express a computed value, a shared instance across two parameters, or
anything conditional, and it introduces a second syntax for something C# already
states precisely. Fixing root-type resolution makes it unnecessary.

## Configuration contract

| Variable | v1.0 | v1.1 |
|---|---|---|
| `LIB_DIR` | required | unchanged |
| `LIB_ASSEMBLY` | required | unchanged |
| `LIB_TYPE` | **required** | **optional** |
| `LIB_REGISTRAR` | optional | optional, and the primary path |
| `LIB_OPTIONS` | optional | unchanged |
| `LIB_SERVICES` | optional | unchanged |
| `LIB_CALLBACKS` | optional | unchanged |

Two modes, decided by whether `LIB_TYPE` is set:

- **Single-instance mode** (`LIB_TYPE` set) — exactly v1.0 behaviour. The named
  type is constructed at startup and every `/invoke` without a `service` field
  targets it.
- **Composition-root mode** (`LIB_TYPE` unset) — `LIB_REGISTRAR` is required.
  The provider is built from the startup, and each `/invoke` names the service
  it wants.

Setting neither `LIB_TYPE` nor `LIB_REGISTRAR` is a fatal startup error naming
both and saying that one is required.

## Root-type resolution

`Activation.Create` resolves the root in this order:

1. If the provider can resolve the type — including via a factory registration,
   and including when the type is an **interface** — use that.
2. Otherwise fall back to `ActivatorUtilities.CreateInstance`, exactly as today.

This is a three-line change with two consequences:

- A factory registration for the root type is honoured, so any constructor shape
  works.
- `LIB_TYPE` may name an **interface**, because a provider can resolve one and
  `ActivatorUtilities` never could. The client and the container then name the
  same type, rather than the container naming a concrete class while the client
  names an interface.

When nothing registers the root type — every v1.0 configuration — step 2 runs
and behaviour is bit-for-bit unchanged.

## Wire protocol

`POST /invoke` gains one optional field:

```jsonc
{
  "service": "TestSupport.ITestFacade",   // optional; new in v1.1
  "method":  "CommitAllAsync",
  "args":    ["a message"]
}
```

- **absent, `LIB_TYPE` set** — target the `LIB_TYPE` instance, exactly as v1.0.
  A v1.0 client talking to a v1.1 host is unaffected.
- **absent, `LIB_TYPE` unset** — an `ok:false` envelope saying the host is in
  composition-root mode and the call must name a service. There is no default
  instance to fall back to, and guessing one would be worse than saying so.
- **present** — resolve that service from the provider and invoke on it,
  regardless of mode. A host with `LIB_TYPE` set may still serve other
  registered services by name.

A `service` that resolves to nothing is an `ok:false` envelope naming the
requested service **and listing the service types the startup actually
registered**. A missing registration is the most likely mistake in this mode and
the error has to make the fix obvious.

`GET /services` is added, returning the registered service type names. It is
what makes the error above possible and is useful on its own for diagnosing a
startup that did not register what its author thought.

## Service lifetimes

Services are resolved from the **root provider**.

- `Singleton` — works, and is the expected registration for a facade.
- `Transient` — works; a new instance per `/invoke`.
- `Scoped` — **rejected at resolve time** with an error naming the service and
  explaining that a remote call has no scope to live in.

Rejecting `Scoped` rather than silently resolving it from the root is
deliberate. Resolving a scoped service from the root container yields
singleton-like lifetime, which is the kind of quietly-wrong behaviour that costs
hours to diagnose. Failing loudly at the first call costs seconds.

## Reset semantics

`DELETE /instance` today discards the single instance so the next call rebuilds
it. In composition-root mode it **rebuilds the provider**: dispose the old one,
re-run the startup, and drop all cached service instances.

This gives a per-test reset that costs a provider rebuild rather than a new
container — useful if a suite outgrows per-test containers, and honest about
what it does and does not clear. It does **not** clear state outside the
process: files already written to a mounted share stay written.

## Client API

Added to `RemoteClass.Client`. This surface freezes at v1.1, so it is kept
small.

```csharp
public sealed class RemoteHost : IAsyncDisposable
{
    public static RemoteHost At(string baseUrl);

    /// Resolves the service in the container and returns a typed proxy.
    /// The round trip is deliberate: a missing registration fails HERE,
    /// naming the interface and listing what is registered, rather than
    /// surfacing later as a confusing failure at the first method call.
    ///
    /// The returned proxy holds the service NAME, not a handle to a
    /// particular instance: every call sends `service` and the host
    /// resolves it afresh. That is what makes ResetAsync work without
    /// invalidating proxies, and what makes a Transient registration
    /// yield a new instance per call.
    public Task<T> GetAsync<T>() where T : class;

    /// Rebuilds the container's provider. All previously returned proxies
    /// remain valid and will bind to the newly built services.
    public Task ResetAsync();
}
```

`RemoteClass.For<T>(url)` is unchanged and remains the single-instance entry
point. `RemoteHost` is the composition-root entry point. Both produce the same
kind of `DispatchProxy`, and the constraint on `T` is the same: it must be an
interface.

A helper builds the container's environment from the startup type, so a fixture
cannot typo a name that the compiler could have checked:

```csharp
public static class RemoteHostEnvironment
{
    /// LIB_ASSEMBLY and LIB_REGISTRAR derived from TStartup.
    /// methodName defaults to "Configure".
    public static IDictionary<string, string> For<TStartup>(string? methodName = null);
}
```

This returns a dictionary rather than orchestrating a container. Container
lifecycle stays with the consumer, so `RemoteClass.Client` takes no dependency
on Testcontainers or any container library — a test-support dependency does not
belong in the package that production-shaped code references.

## The boundary, stated plainly

The README must state this, because the failure is silent:

**Arguments and return values cross by value, never by reference.**

- A data-only object crosses fine.
- An object **with methods** also crosses — the container has the type, so it
  deserializes the state and runs the real methods. But it is a **copy**.
  Mutations made in the container are not visible to the caller. Measured: a
  `Counter { Count = 5 }` passed to a method that calls `Bump()` returns
  `bumped to 6` while the caller's object still reads 5.
- An **interface** argument fails loudly:
  `Deserialization of interface or abstract types is not supported`.

This is why the facade pattern is the documented approach: a narrow interface
taking simple values keeps complex objects inside the container, where the
startup built them, instead of copying them across a boundary that cannot carry
their identity.

When a live object that calls **back** is genuinely needed, that is
`LIB_CALLBACKS` — which passes a reference (a URL) rather than data, and works
for interfaces only. Extending callbacks to method arguments is explicitly **out
of scope** for v1.1.

## Backward compatibility

Every v1.0 configuration keeps working, unchanged:

- `LIB_TYPE` set, no registrar → single-instance mode, `ActivatorUtilities`
  path, identical behaviour.
- `/invoke` without a `service` field → the `LIB_TYPE` instance.
- `RemoteClass.For<T>(url)` → unchanged.

The only behavioural change to an existing configuration is that a registrar
which registers the **root type** is now honoured instead of ignored. That is
the bug being fixed; any configuration relying on it being ignored was relying
on a defect.

## Testing

Added to `test/run.sh`, each verified by removing the behaviour and observing
the failure before restoring it:

1. A factory registration for a concrete root type with a `string` constructor
   parameter is used.
2. `LIB_TYPE` naming an **interface** resolves from the provider.
3. Composition-root mode: no `LIB_TYPE`, startup registers two facades, both
   are callable by `service` name from one container.
4. An unknown `service` returns an error naming it **and** listing registered
   services.
5. `GET /services` lists what the startup registered.
6. A `Scoped` registration is rejected with an error naming the service.
7. `DELETE /instance` rebuilds the provider: a counter held in a singleton
   resets.
8. Neither `LIB_TYPE` nor `LIB_REGISTRAR` set → non-zero exit naming both.
9. **Regression guard:** an existing v1.0 configuration — `LIB_TYPE` plus
   `LIB_OPTIONS`, no registrar — produces byte-identical `/invoke` responses to
   v1.0.1.
10. **Regression guard:** `/invoke` with no `service` field still targets the
    `LIB_TYPE` instance.

Case 9 matters most. The last release cycle shipped a Critical wire-format
regression that every one of 45 tests passed straight through, because none of
them compared response bytes against the previous version. This one does.

## Risks

- **Provider rebuild and in-flight calls.** `ResetAsync` disposes a provider
  that a concurrent `/invoke` may be using. The implementation must serialise
  reset against in-flight invocations, or a reset during a call produces an
  `ObjectDisposedException` that looks like a bug in the caller's code.
- **`IDisposable` services.** Rebuilding the provider disposes the old one,
  which disposes registered `IDisposable` singletons. That is correct, but a
  service holding an open file handle on the share will release it — which a
  test asserting on lock behaviour could be surprised by. Documented, not
  prevented.
- **Startup that throws.** A composition root can fail in ways the v1.0 path
  could not. The error must name the registrar method and preserve the inner
  exception, or a typo in wiring surfaces as an opaque container exit.
- **Two modes to document.** Single-instance and composition-root paths both
  exist. The README has to make the choice obvious rather than presenting two
  equal options — composition-root is the recommendation, single-instance is
  the quick path for one simple class.

## Non-goals

- Callbacks as method arguments.
- Passing live objects by reference in either direction.
- Scoped service support.
- Creating instances on demand from the client (`New<T>(args)`); the startup
  owns construction, deliberately.
- Any change to `RemoteClass.For<T>` semantics.
