# remote-class-host — design

**Date:** 2026-08-23
**Status:** approved, not yet implemented
**Image:** `ghcr.io/drewlane-dev/remote-class-host`
**Package:** `RemoteClass.Client`

## Purpose

Run any .NET class library inside a container — with whatever environment the
test needs, such as a cifs mount — and invoke its members from a test process
with full type safety.

The point is to test the real code in the real environment. Integration tests
that construct a class in the test process cannot reproduce a deployment where
several instances run on separate hosts against shared storage. Two containers
running the same library **are** two instances, and driving them from one test
method makes distributed behaviour ordinarily testable.

One image serves every library. Nothing library-specific is compiled into it.

## Proven before designing

A throwaway spike established all of this against real containers. None of it
is assumed.

1. **A generic host can construct an arbitrary type by name.** Given a mounted
   `dotnet publish` folder plus the type name, it satisfied a constructor taking
   `IOptions<ShareOptions>` and `ILogger<T>` — the dominant .NET shape.
2. **`DispatchProxy` needs no code generation.** It intercepts async methods,
   synchronous methods, `void` methods and properties, returning real values.
   The test project already references the library for its interface, so the
   compiler enforces the contract and nothing regenerates.
3. **Two instances really are two clients.** Instance A wrote a file through its
   own cifs mount; instance B read it back through a separate mount and separate
   SMB session; the bytes were confirmed on the server, not in either container.
4. **VB works unchanged.** A C# test process drove a VB interface and VB
   implementation through the same image, with no host changes. The host reflects
   over IL metadata and cannot tell which compiler produced an assembly.

## Non-goals

- **Not a production RPC framework.** It is a test affordance. No auth, no
  retries, no streaming, no versioned contract negotiation.
- **No `ref`/`out` parameters, open generics, or `Stream` arguments.**
  `DispatchProxy` surfaces them but they cannot cross the wire meaningfully.
- **No general DI container.** Constructor injection is limited to the shapes
  below, with a plugin escape hatch for anything else.
- **No assumption of SMB.** Mounting is optional. A SQL-client library needs no
  mount, and the image must serve it just as happily.

## Architecture

### Plugin loading

The library and its dependencies are mounted as a `dotnet publish` output
directory. The host loads the assembly into the **default**
`AssemblyLoadContext`, resolving dependencies from that directory via
`AssemblyResolve`.

**This is the single most important decision in the design, and the spike is
why.** Loading into a separate `AssemblyLoadContext` isolates type identities:
the host's `typeof(IOptions<>)` is then a *different type* from the plugin's,
every dependency shape has to be resolved by string through the plugin's own
assemblies, and the failures are cryptic runtime nulls. Sharing the default
context makes type identities unify and the construction code collapses to
something readable. The cost is that host and plugin must agree on the versions
of any shared package; that trade is worth taking.

### Construction

A constructor parameter is satisfied if it is:

- `IOptions<T>` — `T` is bound from the `LIB_OPTIONS` JSON.
- `ILogger<T>` — a null logger.

Anything else fails fast at startup, naming the parameter type.

**Escape hatch.** A library needing real wiring (a live `SqlConnection`, say)
can publish a type implementing `IRemoteClassModule`, a single-method interface
returning the constructed object. The host prefers it when present. This keeps
the common case zero-effort without capping what the image can serve.

### Instance lifetime

**One instance per container, constructed at startup, living until the container
stops.** Every `/invoke` call reaches that same object.

This is the property that makes the image useful rather than an implementation
detail. A method that acquires a resource and stores it in a field — a lock, an
open handle, a connection — can be released by a later call, because the later
call reaches the same instance. A per-call instance would make `lock()` /
`unlock()` impossible to express at all.

Three consequences follow, and all three are easy to be surprised by:

- **Calls are concurrent.** ASP.NET serves requests in parallel against the one
  instance, so two overlapping `/invoke` calls run on the same object. A test
  that fires parallel calls is exercising the library's thread-safety whether it
  intended to or not. This is faithful to a deployed instance, which also serves
  concurrent requests.
- **State outlives a test.** The container outlives any single test, so a lock
  left held by one test is still held for the next, and the resulting failure
  points at the wrong test. Callers must release what they take, or reset
  between tests.
- **The lifetime may not match production.** A store registered scoped-per-request
  in a real application gets a fresh instance per request. Here it is effectively
  a singleton. Code that relies on per-request construction has different
  semantics under this host than in production, and a passing test should be read
  with that in mind.

`DELETE /instance` disposes the current instance (if it implements `IDisposable`)
and constructs a fresh one from the same configuration, so a fixture can reset
between tests without restarting the container. It exists specifically for the
case above: a test that deliberately leaves a resource held.

### API

```
GET    /health                      -> 200 {"type":"..."}
GET    /types                       -> the types the loaded assembly contains
POST   /invoke  {"method":"WriteAsync","args":[...]}
                                    -> {"ok":true,"result":...}
                                    |  {"ok":false,"error":"..."}
DELETE /instance                    -> 204, disposes and reconstructs
```

`/types` exists because of a specific spike failure. VB **prepends
`RootNamespace` to declared namespaces**, unlike C# where a file's namespace is
absolute, so a VB library's fully-qualified name is frequently not what a C#
developer would write. Misconfiguring `LIB_TYPE` otherwise produces "type not
found" at startup with nothing pointing at the cause. Listing the assembly's
actual types turns a dead end into an obvious fix.

### Optional mount

If `SMB_SERVER` and `SMB_SHARE` are set, the host mounts the share before
serving, and a failed mount is fatal. If they are unset it serves immediately.
Mounting requires `CAP_SYS_ADMIN` and `DAC_READ_SEARCH`; a library that needs no
mount needs no elevated capabilities.

### Substituting a dependency

Two mechanisms, chosen per dependency. They compose: one instance can take a
fake for one collaborator and a live mock for another.

**`LIB_SERVICES` — a fake compiled into the plugin.**

```
LIB_SERVICES={"MyApp.IGitConfigManager":"MyApp.Testing.FakeGitConfigManager"}
```

Zero infrastructure, no network hop, behaviour fixed at compile time. The right
choice when the substitute is simple and the same for every test.

**`LIB_CALLBACKS` — a real mock living in the test process.**

```
LIB_CALLBACKS={"MyApp.IGitConfigManager":"http://testrunner:9090"}
```

The host registers a `DispatchProxy` for that interface which forwards every
call back to the test process over HTTP, where an ordinary Moq mock serves it.
`Setup`, `Returns`, argument matchers and `Verify` all work, against an instance
running in a container.

This is the existing mechanism reversed: the forwarding proxy is the client's
proxy pointing outward, and the test-side dispatcher is the host's `Invoker`
pointing inward. Both directions already work, which is why this is tractable.

Naming the same interface in both is a **startup error**, not a precedence rule.
Ambiguous configuration should fail loudly rather than resolve silently to
whichever the implementation happened to check first.

### What callbacks cost

The line count is modest; these four are where the real difficulty is, and each
is a decision rather than an implementation detail.

- **Reachability.** The container must resolve and reach the test process. The
  test runner is already on the Docker network, so it needs a network alias and
  a known port. Missing that fails at first call with a connection error rather
  than at startup, so the client's listener reports the address it is reachable
  at when it starts.
- **Lifetime.** A container can call back *after* the test method returns, when
  the mock's setup is gone and the listener may be disposed. A late call gets an
  explicit error naming the situation — not a silent success and not an
  ambiguous connection refusal. Callers dispose the listener with the fixture,
  not the test.
- **Concurrency.** Several instances may call one mock at once. Moq is not
  thread-safe for setup, and `Verify(Times.Once)` counts across every caller. The
  supported pattern is one mock per instance; sharing one across instances is
  possible but the counting is the caller's problem, and the README says so.
- **Exceptions across two hops.** A mock that throws must arrive at the original
  call site with its own message intact, through both the callback channel and
  the invoke channel. Both hops unwrap `TargetInvocationException` and preserve
  the inner message.

### Client package

`RemoteClass.Client` ships the `DispatchProxy` and the wire types, and
**nothing else** — no Testcontainers dependency, no fixture helpers — so it stays
usable from any test framework and rarely needs a release. It is versioned with
the image because it is the other half of the same protocol.

The proxy must handle four return shapes, which the spike proved are all
distinct: `void` → null, `Task` → completed, `Task<T>` → wrapped value,
anything else → deserialised directly. An early version assumed everything was
`Task` or `Task<T>` and threw on the first synchronous method.

## Configuration

| Variable | Default | Meaning |
|---|---|---|
| `LIB_DIR` | `/plugin` | Mounted `dotnet publish` output. |
| `LIB_ASSEMBLY` | *(required)* | Assembly file name within that directory. |
| `LIB_TYPE` | *(required)* | Fully-qualified type to construct. |
| `LIB_OPTIONS` | `{}` | JSON bound to whatever `IOptions<T>` the constructor requires. |
| `LIB_PORT` | `8080` | HTTP listen port. |
| `LIB_SERVICES` | `{}` | Interface-to-implementation map, resolved from the plugin. |
| `LIB_REGISTRAR` | unset | `Namespace.Type.Method` taking `IServiceCollection`; runs before `LIB_SERVICES`. |
| `LIB_CALLBACKS` | `{}` | Interface-to-URL map; calls forward to a mock in the test process. |
| `SMB_SERVER` | unset | If set with `SMB_SHARE`, mount before serving. |
| `SMB_SHARE` | unset | Share name. |
| `SMB_USER` / `SMB_PASS` | `azure` / `Passw0rd!` | Mount credentials. |
| `SMB_MOUNT_OPTIONS` | `vers=3.1.1,uid=0,gid=0,file_mode=0777,dir_mode=0777,serverino,nosharesock,actimeo=30,mfsymlinks,seal` | Passed to `mount -t cifs`. |
| `SMB_MOUNT_POINT` | `/mnt/share` | Mount location. |

## Testing

Self-tests run real containers, and cover both languages because language
independence is a claim this design makes.

1. A C# library is constructed and an async method returns a value.
2. A synchronous method with a return value works.
3. A `void` method works.
4. A **VB** library is constructed and all three shapes work, with no host change.
5. `LIB_TYPE` naming a missing type fails at startup, and `/types` lists what the
   assembly actually contains.
6. A constructor parameter that cannot be satisfied fails fast, naming the type.
7. Two instances sharing an SMB share observe each other: A writes, B reads the
   same bytes over a separate mount, and the content is verified **server-side**
   rather than through either client's cache.

Case 7 is the one that proves the image's reason for existing. Cases 4 and 5
exist because the spike hit both.

## Release

Mirrors `azure-files-emulator`, whose pipeline is proven:

- `ci.yml` on pull request and push to `main`: build, run the self-tests.
- `release.yml` on a `v*` tag: run the self-tests, then push a multi-arch
  `linux/amd64,linux/arm64` image to GHCR **and** push the NuGet package, both
  carrying the tag's version so they cannot drift.

Tags for `v1.2.3`: `1.2.3`, `1.2`, `1`, `latest`, with `latest` guarded against
prerelease tags.

## Risks

- **Shared-context version conflicts.** If a plugin needs a different major
  version of a package the host also loads, loading will fail or misbehave. The
  alternative — isolated contexts — was measured to be substantially worse.
  Mitigation: keep the host's own dependency surface as small as possible.
- **Reflection-based construction has a ceiling.** It handles the common shape,
  not everything. `IRemoteClassModule` is the intended answer, and if most consumers end
  up needing it, the reflection path is the wrong default and should be revisited.
- **Test-only by construction.** `/invoke` executes arbitrary methods on a loaded
  assembly. It must never be exposed outside a test network, and the README must
  say so plainly.
