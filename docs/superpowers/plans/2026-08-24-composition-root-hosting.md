# Composition-root hosting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the container host a composition root — the consumer writes a C# startup plus a facade interface, and the client names the facade in C# — instead of hosting one class configured entirely through environment variables.

**Architecture:** `Activation` stops returning a single constructed object and returns a `HostedGraph` that owns the `ServiceProvider`. Root resolution asks the provider first and falls back to `ActivatorUtilities`, which both honours a factory registration and lets `LIB_TYPE` name an interface. `/invoke` gains an optional `service` field so one container can serve every service its startup registered.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, `Microsoft.Extensions.DependencyInjection`, `System.Reflection.DispatchProxy`, Docker, `sh` for the test suite.

**Spec:** `docs/superpowers/specs/2026-08-24-composition-root-hosting-design.md`

## Global Constraints

- **Backward compatibility is the top constraint.** Every v1.0 configuration must behave identically. `LIB_TYPE` set with no registrar must produce byte-identical `/invoke` responses to v1.0.1. Task 1 builds the guard that proves this; it must stay green for every later task.
- **`LIB_TYPE` becomes optional; `LIB_ASSEMBLY` and `LIB_DIR` stay required.** Setting neither `LIB_TYPE` nor `LIB_REGISTRAR` is a fatal startup error naming both.
- **`Scoped` services are rejected at resolve time**, with an error naming the service. Never silently resolve one from the root provider.
- **Errors must name enough to act on**: the service or type involved, and for a missing service, the list of services that ARE registered.
- **No test may pass for a reason other than the behaviour it names.** For every test added, delete the behaviour under test, observe the failure, restore it, and report both observations. Assert on whole response envelopes, not substrings — a broken payload can still contain the substring you grepped for.
- **The client package takes no dependency on Testcontainers or any container library.**
- **`RemoteClass.For<T>(url)` semantics do not change.** It must continue NOT to send a `service` field.
- Every step's `docker run` uses the locally built image tag, never a published tag, except where a task explicitly compares against `ghcr.io/drewlane-dev/remote-class-host:1.0.1`.
- **Every task appends cases to `test/run.sh`. Insert them BEFORE the wire-format baseline block that Task 1 adds at the end**, so the baseline stays the last thing the suite reports and the final summary stays last. Appending after it corrupts the suite's output ordering.
- **`test/run.sh`'s `api` helper takes a PATH, not a method name**: `api <alias> <path> [curl args...]` (`test/run.sh:50`). Every `/invoke` call goes through it as `api <alias> /invoke -X POST -H 'Content-Type: application/json' -d '<json>'`.

---

### Task 1: Byte-comparison regression guard against v1.0.1

Built first, deliberately. The last release cycle shipped a Critical wire-format regression that all 45 tests passed through, because nothing compared response bytes to the previous version. This guard exists before any behaviour changes, so every later task is measured against it.

**Files:**
- Create: `test/baseline.sh`
- Modify: `test/run.sh` (add a call to it)

**Interfaces:**
- Consumes: nothing.
- Produces: `test/baseline.sh`, run as `sh test/baseline.sh <image-tag>`; exits non-zero on any byte difference.

- [ ] **Step 1: Write the guard**

Create `test/baseline.sh`:

```sh
#!/bin/sh
# Compares /invoke responses from the image under test against the PUBLISHED
# v1.0.1 image, byte for byte, for a v1.0-shaped configuration.
#
# This exists because a wire-format change is invisible to ordinary tests: a
# regression that drops a field still returns ok:true and still passes any
# assertion that greps for a value. Only a byte comparison against the previous
# release catches it.
set -eu

IMAGE="${1:-remote-class-host:dev}"
BASELINE="ghcr.io/drewlane-dev/remote-class-host:1.0.1"
HERE="$(cd "$(dirname "$0")" && pwd)"
NET="rch-baseline-$$"
PASS=0
FAIL=0

ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; }

cleanup() {
  docker rm -f "base-${NET}" "test-${NET}" >/dev/null 2>&1 || true
  docker network rm "${NET}" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "${NET}" >/dev/null

start() { # start <name> <image>
  # LIB_SERVICES is REQUIRED here: CsLib.Store's constructor takes IStamp, so
  # without a mapping the container fails to construct and never becomes
  # healthy. This mirrors exactly how test/run.sh starts its own `cs` host.
  docker run -d --name "$1" --network "${NET}" --network-alias "$1" \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store \
    -e LIB_OPTIONS='{"RootPath":"/tmp/baseline"}' \
    -e LIB_SERVICES='{"CsLib.IStamp":"CsLib.RealStamp"}' \
    -e DOTNET_EnableDiagnostics=0 \
    "$2" >/dev/null
}

call() { # call <alias> <json> -> raw response body
  docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
    -X POST "http://$1:8080/invoke" -H 'Content-Type: application/json' -d "$2"
}

wait_healthy() {
  i=0
  while [ "$i" -lt 60 ]; do
    if docker run --rm --network "${NET}" curlimages/curl:8.10.1 \
         -s -m 2 -o /dev/null "http://$1:8080/health" 2>/dev/null; then
      return 0
    fi
    i=$((i + 1)); sleep 1
  done
  return 1
}

echo "== wire-format baseline: ${IMAGE} vs ${BASELINE} =="
docker pull -q "${BASELINE}" >/dev/null 2>&1 || true
start "base-${NET}" "${BASELINE}"
start "test-${NET}" "${IMAGE}"
wait_healthy "base-${NET}" || { echo "baseline image did not become healthy"; exit 1; }
wait_healthy "test-${NET}" || { echo "image under test did not become healthy"; exit 1; }

# Each case is a full /invoke body. Compare the WHOLE response envelope: a
# substring check would pass against a payload that had lost a field.
# Method names verified against test/fixtures/CsLib/Store.cs. Two of these --
# VtValueAsync and PolyReturn -- are the exact shapes that regressed in the last
# release cycle, so they are the ones most worth pinning byte-for-byte.
for body in \
  '{"method":"WriteAsync","args":["a.txt","hello"]}' \
  '{"method":"ReadAsync","args":["a.txt"]}' \
  '{"method":"Count","args":[]}' \
  '{"method":"VtValueAsync","args":[]}' \
  '{"method":"Stamp","args":[]}' \
  '{"method":"RefArg","args":[1]}' \
  '{"method":"Echo","args":[1]}' \
  '{"method":"PolyReturn","args":[]}' \
  '{"method":"DefinitelyMissing","args":[]}' \
  ; do
  a=$(call "base-${NET}" "$body")
  b=$(call "test-${NET}" "$body")
  if [ "$a" = "$b" ]; then
    ok "identical response for $body"
  else
    bad "response DIFFERS for $body"
    echo "      v1.0.1: $a"
    echo "      under test: $b"
  fi
done

echo ""
echo "baseline passed: ${PASS}  failed: ${FAIL}"
[ "$FAIL" -eq 0 ]
```

- [ ] **Step 2: Run it and verify it passes against an unchanged build**

```bash
cd /Users/drew/repos/remote-class-host
dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
docker build -t remote-class-host:dev .
sh test/baseline.sh remote-class-host:dev
```

Expected: every case reports `ok`, `failed: 0`, exit 0. The current build IS v1.0.1 plus the nuget change, so identical responses are the correct result.

- [ ] **Step 3: Prove the guard can fail**

Temporarily break the wire format — in `src/RemoteClassHost/Invoker.cs`, find where the success envelope is built and rename `ok` to `okay`. Rebuild and re-run:

```bash
docker build -t remote-class-host:dev .
sh test/baseline.sh remote-class-host:dev
```

Expected: FAIL on every case, showing both payloads, exit non-zero. Record the actual output in your report. Then revert the change, rebuild, and confirm it passes again.

A guard that has never been observed failing is not a guard.

- [ ] **Step 4: Wire it into the suite**

At the very end of `test/run.sh`, immediately before the final summary that prints `passed:`/`failed:`, add:

```sh
echo "== wire-format baseline vs the previous release =="
if sh "${HERE}/baseline.sh" "${IMAGE}"; then
  ok "responses are byte-identical to v1.0.1 for a v1.0 configuration"
else
  bad "responses DIFFER from v1.0.1 for a v1.0 configuration"
fi
```

- [ ] **Step 5: Run the full suite**

```bash
./test/run.sh remote-class-host:dev
```

Expected: all existing cases pass, plus the new baseline case. Report the total.

- [ ] **Step 6: Commit**

```bash
git add test/baseline.sh test/run.sh
git commit -m "Guard the wire format against the previous release"
```

---

### Task 2: HostedGraph, and resolve the root from the provider first

The core fix. Two measured defects close here: a factory registration for the root type is ignored, and `LIB_TYPE` cannot name an interface.

**Files:**
- Create: `src/RemoteClassHost/HostedGraph.cs`
- Modify: `src/RemoteClassHost/Activation.cs`
- Modify: `src/RemoteClassHost/InstanceHolder.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `test/fixtures/CsLib/Store.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `Activation.Create(Type, string, string, string?, string)` as it exists today.
- Produces:
  - `sealed class HostedGraph : IDisposable` with `object? Root { get; }`, `IReadOnlyList<string> ServiceNames { get; }`, `object Resolve(string serviceName)`, `Dispose()`.
  - `static HostedGraph Activation.Build(Type? rootType, string optionsJson, string servicesJson, string? registrar, string callbacksJson)`.
  - `Activation.Create` is REMOVED; `Build` replaces it. Nothing outside `Program.cs` and `InstanceHolder.cs` calls it.

- [ ] **Step 1: Add the failing test fixtures**

In `test/fixtures/CsLib/Store.cs`, append:

```csharp
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

/// <summary>Registers both shapes the new resolution path must support.</summary>
public static class GraphStartup
{
    public static void Configure(Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton(services, _ => new StringRooted("from-factory"));
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<IRootFacade, RootFacade>(services);
    }
}
```

- [ ] **Step 2: Add the failing test cases**

In `test/run.sh`, after the existing `LIB_REGISTRAR` section, add:

```sh
echo "== the provider builds the root when it knows how =="
start_host factoryroot "${HERE}/publish/cslib" CsLib.dll CsLib.StringRooted '{}' '{}' \
  CsLib.GraphStartup.Configure
wait_healthy factoryroot && ok "a factory-registered root type constructs" \
                         || bad "a factory-registered root type constructs"
body=$(api factoryroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Label","args":[]}')
[ "$body" = '{"ok":true,"result":"from-factory"}' ] \
  && ok "the factory's value reached the instance" \
  || bad "the factory's value reached the instance"

echo "== LIB_TYPE may name an interface =="
start_host ifaceroot "${HERE}/publish/cslib" CsLib.dll CsLib.IRootFacade '{}' '{}' \
  CsLib.GraphStartup.Configure
wait_healthy ifaceroot && ok "an interface as LIB_TYPE resolves" \
                      || bad "an interface as LIB_TYPE resolves"
body=$(api ifaceroot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}')
[ "$body" = '{"ok":true,"result":"root-facade"}' ] \
  && ok "the registered implementation served the call" \
  || bad "the registered implementation served the call"
```

The `api` helper's real signature is `api <alias> <path> [curl args...]`
(`test/run.sh:50`) — it takes a PATH, not a method name. The calls above use it
correctly; do not "fix" them to a shorter form.

- [ ] **Step 3: Run and watch both fail**

```bash
dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
docker build -t remote-class-host:dev . && ./test/run.sh remote-class-host:dev
```

Expected: `factoryroot` fails to become healthy (the container exits with `Unable to resolve service for type 'System.String'`), and `ifaceroot` fails because `ActivatorUtilities` cannot construct an interface. Record the exact container logs for both.

- [ ] **Step 4: Write HostedGraph**

Create `src/RemoteClassHost/HostedGraph.cs`:

```csharp
using Microsoft.Extensions.DependencyInjection;

namespace RemoteClassHost;

/// <summary>
/// Owns the provider built from the plugin's composition root, plus the root
/// instance when one is configured.
///
/// This exists because the provider has to OUTLIVE construction. v1.0 built a
/// provider, constructed one object from it, and dropped it — which is why a
/// registrar could register a service that nothing could ever reach.
/// </summary>
public sealed class HostedGraph(ServiceProvider provider, object? root, IReadOnlyList<string> serviceNames)
    : IDisposable
{
    /// <summary>The LIB_TYPE instance, or null in composition-root mode.</summary>
    public object? Root { get; } = root;

    /// <summary>Full names of every service type the startup registered.</summary>
    public IReadOnlyList<string> ServiceNames { get; } = serviceNames;

    /// <summary>
    /// Resolves a service by full type name.
    ///
    /// A miss lists what IS registered: a name that does not match is the most
    /// likely mistake in composition-root mode, and "not found" alone leaves
    /// the caller guessing between a typo, a missing registration, and the
    /// wrong assembly.
    /// </summary>
    public object Resolve(string serviceName)
    {
        var type = PluginLoader.Assembly?.GetType(serviceName)
                   ?? Type.GetType(serviceName);

        if (type is null)
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is not a type in the plugin assembly. " +
                $"Registered services: {string.Join(", ", ServiceNames)}");
        }

        var resolved = provider.GetService(type);

        if (resolved is null)
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is not registered. " +
                $"Registered services: {string.Join(", ", ServiceNames)}");
        }

        return resolved;
    }

    public void Dispose() => provider.Dispose();
}
```

- [ ] **Step 5: Change Activation to build a graph**

In `src/RemoteClassHost/Activation.cs`, rename `Create` to `Build`, change the
signature so the root type is optional, and replace the construction block.

The signature becomes:

```csharp
public static HostedGraph Build(
    Type? rootType, string optionsJson, string servicesJson, string? registrar, string callbacksJson)
```

Everything from `var services = new ServiceCollection();` down to the end of the
auto-registration `while` loop stays EXACTLY as it is, with one change: the
auto-registration walk currently seeds `pending` with `type`. Guard it so a null
root does not enqueue anything:

```csharp
var pending = new Queue<Type>();
if (rootType is not null) pending.Enqueue(rootType);
```

Then replace the `var provider = services.BuildServiceProvider();` block and the
`try`/`catch` that follows it with:

```csharp
        // Captured BEFORE building: a ServiceCollection is the list of
        // descriptors, and it is the only place the registered service types
        // can be enumerated. A built provider does not expose them.
        var serviceNames = services
            .Select(d => d.ServiceType.FullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct()
            .ToList();

        var provider = services.BuildServiceProvider();

        if (rootType is null)
        {
            // Composition-root mode: nothing to construct up front. Every call
            // names the service it wants.
            return new HostedGraph(provider, null, serviceNames);
        }

        object rootInstance;
        try
        {
            // Ask the CONTAINER first. This is what makes a factory
            // registration -- services.AddSingleton(sp => new Thing("x")) --
            // actually get used, and what allows LIB_TYPE to name an
            // interface. ActivatorUtilities can do neither: it constructs the
            // type directly and ignores any registration for it.
            rootInstance = provider.GetService(rootType)
                           ?? ActivatorUtilities.CreateInstance(provider, rootType);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"cannot construct {rootType.FullName}: {ex.Message}. " +
                "Register it or its missing dependency in your LIB_REGISTRAR " +
                "startup, or name an implementation in LIB_SERVICES as " +
                "{\"Full.IService\":\"Full.Implementation\"}.", ex);
        }

        return new HostedGraph(provider, rootInstance, serviceNames);
    }
```

- [ ] **Step 6: Update InstanceHolder to hold a graph**

Replace the body of `src/RemoteClassHost/InstanceHolder.cs`:

```csharp
namespace RemoteClassHost;

/// <summary>
/// Owns the graph every call reaches.
///
/// Reset exists because the container outlives any individual test: a test that
/// deliberately leaves a lock held would otherwise poison the next one, and the
/// resulting failure would point at the wrong test.
/// </summary>
public sealed class InstanceHolder(Func<HostedGraph> factory)
{
    private HostedGraph _current = factory();

    public HostedGraph Current => _current;

    public void Reset()
    {
        // Build the replacement BEFORE disposing the old one: if factory()
        // throws (a startup that fails, say), _current must stay pointed at the
        // still-valid old graph, not a disposed one that every later call would
        // then hit.
        var next = factory();
        _current.Dispose();
        _current = next;
    }
}
```

- [ ] **Step 7: Update Program.cs to the new call**

In `src/RemoteClassHost/Program.cs`, change the holder construction and the two
places that used `holder.Current` as an instance:

```csharp
var holder = new InstanceHolder(() => Activation.Build(type, optsJson, servicesJson, registrar, callbacksJson));
```

and the invoke endpoint:

```csharp
app.MapPost("/invoke", async (InvokeRequest request) =>
    Results.Ok(await Invoker.InvokeAsync(holder.Current.Root!, type, request, jsonOptions)));
```

`LIB_TYPE` is still required at this task, so `Root` is never null here. Task 3
makes it optional and removes the `!`.

- [ ] **Step 8: Run the tests and verify both new cases pass**

```bash
dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
docker build -t remote-class-host:dev . && ./test/run.sh remote-class-host:dev
```

Expected: `factoryroot` and `ifaceroot` now pass, every pre-existing case still
passes, and the Task 1 baseline still reports byte-identical responses.

If the baseline FAILS here, stop: the root-resolution change has altered the
wire format for a v1.0 configuration, which it must not.

- [ ] **Step 9: Verify the new tests are not vacuous**

Revert only the resolution line — change

```csharp
rootInstance = provider.GetService(rootType)
               ?? ActivatorUtilities.CreateInstance(provider, rootType);
```

back to

```csharp
rootInstance = ActivatorUtilities.CreateInstance(provider, rootType);
```

Rebuild, run, and confirm `factoryroot` and `ifaceroot` both fail again. Restore
the line, rebuild, confirm they pass. Report both observations.

- [ ] **Step 10: Commit**

```bash
git add src/RemoteClassHost/HostedGraph.cs src/RemoteClassHost/Activation.cs \
        src/RemoteClassHost/InstanceHolder.cs src/RemoteClassHost/Program.cs \
        test/fixtures/CsLib/Store.cs test/run.sh
git commit -m "Resolve the root from the provider before constructing it"
```

---

### Task 3: Make LIB_TYPE optional, and add GET /services

**Files:**
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `HostedGraph` from Task 2.
- Produces: `GET /services` returning a JSON array of registered service type names; composition-root mode when `LIB_TYPE` is unset.

- [ ] **Step 1: Write the failing tests**

In `test/run.sh`, add:

```sh
echo "== composition-root mode: no LIB_TYPE =="
docker run -d --name "croot-${NET}" --network "${NET}" --network-alias croot \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e LIB_REGISTRAR=CsLib.GraphStartup.Configure \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
wait_healthy croot && ok "the host starts with no LIB_TYPE" \
                  || bad "the host starts with no LIB_TYPE"

docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
  http://croot:8080/services | grep -q "CsLib.IRootFacade" \
  && ok "GET /services lists a registered service" \
  || bad "GET /services lists a registered service"

echo "== neither LIB_TYPE nor LIB_REGISTRAR is fatal =="
docker run -d --name "noconfig-${NET}" --network "${NET}" \
  -v "${HERE}/publish/cslib:/plugin:ro" \
  -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll \
  -e DOTNET_EnableDiagnostics=0 \
  "${IMAGE}" >/dev/null
if wait_stopped "noconfig-${NET}" \
   && [ "$(docker inspect -f '{{.State.ExitCode}}' "noconfig-${NET}")" != "0" ]; then
  ok "a host with neither LIB_TYPE nor LIB_REGISTRAR exits non-zero"
else
  bad "a host with neither LIB_TYPE nor LIB_REGISTRAR exits non-zero"
fi
docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_TYPE" \
  && docker logs "noconfig-${NET}" 2>&1 | grep -q "LIB_REGISTRAR" \
  && ok "the failure names both variables" \
  || bad "the failure names both variables"
docker rm -f "noconfig-${NET}" >/dev/null 2>&1 || true
```

Read `test/run.sh` for the existing `wait_stopped` helper and match its calling
convention; if it does not exist, model the new one on the existing
`wait_healthy`.

- [ ] **Step 2: Run and watch them fail**

Expected: `croot` fails because `LIB_TYPE is required` throws at startup, and the
`noconfig` case fails for the same reason rather than the intended message.

- [ ] **Step 3: Make LIB_TYPE optional**

In `src/RemoteClassHost/Program.cs`, replace the `typeName` line and the load:

```csharp
var typeName = Environment.GetEnvironmentVariable("LIB_TYPE");
```

and after `registrar` is read, add:

```csharp
// One of the two must say what to serve. Without either, the container would
// start and answer nothing, and the first call would fail with something that
// does not name the actual mistake.
if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(registrar))
{
    throw new InvalidOperationException(
        "either LIB_TYPE (to host one class) or LIB_REGISTRAR (to host a " +
        "composition root) is required; neither was set.");
}
```

Replace the type load with a conditional one:

```csharp
var type = string.IsNullOrWhiteSpace(typeName)
    ? null
    : PluginLoader.Load(dir, asmFile, typeName);

// In composition-root mode nothing names a type, so the assembly still has to
// be loaded for the registrar and for service-name lookup.
if (type is null) PluginLoader.LoadAssembly(dir, asmFile);
```

- [ ] **Step 4: Add PluginLoader.LoadAssembly**

In `src/RemoteClassHost/PluginLoader.cs`, extract the assembly-loading half of
`Load` into a public method and have `Load` call it. Read the existing `Load`
first and preserve its resolution behaviour exactly — only the type lookup moves
out:

```csharp
/// <summary>
/// Loads the plugin assembly without requiring a type name. Composition-root
/// mode needs the assembly (for the registrar and for resolving service names)
/// but names no single type.
/// </summary>
public static Assembly LoadAssembly(string dir, string assemblyFile)
```

- [ ] **Step 5: Update the endpoints**

```csharp
app.MapGet("/health", () => Results.Ok(new { type = type?.FullName }));

app.MapGet("/services", () => Results.Ok(holder.Current.ServiceNames));
```

Also replace the `/invoke` body's `holder.Current.Root!` with a null guard. This
task makes `Root` nullable, and the `!` written in Task 2 becomes a live
NullReferenceException the moment a host runs without `LIB_TYPE` — which this
task's own `croot` container does. Task 4 replaces this endpoint wholesale;
until then it must not crash:

```csharp
app.MapPost("/invoke", async (InvokeRequest request) =>
{
    var root = holder.Current.Root;

    if (root is null)
    {
        return Results.Ok(new
        {
            ok = false,
            error = "this host is in composition-root mode (no LIB_TYPE), so a " +
                    "call must name the service it wants in the \"service\" field.",
        });
    }

    return Results.Ok(await Invoker.InvokeAsync(root, type!, request, jsonOptions));
});
```

Add the matching test, so the crash path is covered by this task rather than
left for Task 4:

```sh
out=$(api croot /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Who","args":[]}')
echo "$out" | grep -qi "composition-root\|must name a service" \
  && ok "an invoke with no service says so plainly" \
  || bad "an invoke with no service says so plainly"
```

- [ ] **Step 6: Run and verify**

Expected: both new cases pass, everything else including the baseline still
passes.

- [ ] **Step 7: Verify non-vacuity**

Delete the `if (string.IsNullOrWhiteSpace(typeName) && ...)` guard, rebuild, and
confirm the `noconfig` case fails. Restore it. Report what the container printed
without the guard.

- [ ] **Step 8: Commit**

```bash
git add src/RemoteClassHost/Program.cs src/RemoteClassHost/PluginLoader.cs test/run.sh
git commit -m "Make LIB_TYPE optional and expose the registered services"
```

---

### Task 4: Route /invoke by service name, and reject Scoped

**Files:**
- Modify: `src/RemoteClassHost/Contracts.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `src/RemoteClassHost/HostedGraph.cs`
- Modify: `src/RemoteClassHost/Activation.cs`
- Modify: `test/fixtures/CsLib/Store.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `HostedGraph.Resolve(string)` from Task 2.
- Produces: `InvokeRequest` gains `string? Service`; `/invoke` routes by it.

- [ ] **Step 1: Add the scoped fixture**

Append to `test/fixtures/CsLib/Store.cs`:

```csharp
public interface IScopedThing { string Say(); }

public sealed class ScopedThing : IScopedThing
{
    public string Say() => "scoped";
}
```

and extend `GraphStartup.Configure` with:

```csharp
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddScoped<IScopedThing, ScopedThing>(services);
```

- [ ] **Step 2: Write the failing tests**

```sh
echo "== a call names the service it wants =="
[ "$(api_service croot CsLib.IRootFacade Who)" = '{"ok":true,"result":"root-facade"}' ] \
  && ok "an invoke naming a service reaches it" \
  || bad "an invoke naming a service reaches it"

echo "== an unknown service names what IS registered =="
out=$(api_service croot CsLib.INotRegistered Who)
echo "$out" | grep -q "CsLib.INotRegistered" \
  && echo "$out" | grep -q "CsLib.IRootFacade" \
  && ok "an unknown service error lists the registered ones" \
  || bad "an unknown service error lists the registered ones"

echo "== a scoped service is rejected, not silently rooted =="
out=$(api_service croot CsLib.IScopedThing Say)
echo "$out" | grep -q "IScopedThing" && echo "$out" | grep -qi "scope" \
  && ok "a scoped service is rejected by name" \
  || bad "a scoped service is rejected by name"

```

Add an `api_service` helper next to the existing `api` in `test/run.sh`, matching
its style:

```sh
api_service() { # api_service <alias> <serviceFullName> <method> [jsonArgs]
  # Built on the existing `api` helper (test/run.sh:50), whose signature is
  # `api <alias> <path> [curl args...]`, so there is one place that knows how
  # this suite reaches a container.
  args_="${4:-[]}"
  api "$1" /invoke -X POST -H 'Content-Type: application/json' \
    -d "{\"service\":\"$2\",\"method\":\"$3\",\"args\":${args_}}"
}
```

- [ ] **Step 3: Run and watch all four fail**

Expected: the `service` field is ignored today, so the first three fail and the
fourth fails with a null-reference or a "no method" error rather than the
intended message.

- [ ] **Step 4: Add Service to the request contract**

```csharp
public sealed record InvokeRequest(string Method, JsonElement[] Args, string? Service = null);
```

Placing `Service` last with a default keeps existing bodies deserializing
unchanged.

- [ ] **Step 5: Record scoped services in the graph**

In `Activation.Build`, alongside `serviceNames`, capture the scoped ones:

```csharp
        var scopedNames = services
            .Where(d => d.Lifetime == ServiceLifetime.Scoped)
            .Select(d => d.ServiceType.FullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet();
```

Pass it to the `HostedGraph` constructor (add a fourth parameter
`IReadOnlySet<string> scopedNames`) and check it at the top of `Resolve`:

```csharp
        if (scopedNames.Contains(serviceName))
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is registered Scoped, and a remote " +
                "call has no scope to live in. Register it Singleton or " +
                "Transient, or resolve it inside a method on a service that is.");
        }
```

Update both `new HostedGraph(...)` call sites in `Build` to pass it.

- [ ] **Step 6: Route the invoke endpoint**

```csharp
app.MapPost("/invoke", async (InvokeRequest request) =>
{
    var graph = holder.Current;

    if (!string.IsNullOrWhiteSpace(request.Service))
    {
        object target;
        try
        {
            target = graph.Resolve(request.Service);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Ok(new { ok = false, error = ex.Message });
        }

        return Results.Ok(
            await Invoker.InvokeAsync(target, target.GetType(), request, jsonOptions));
    }

    if (graph.Root is null)
    {
        return Results.Ok(new
        {
            ok = false,
            error = "this host is in composition-root mode (no LIB_TYPE), so a " +
                    "call must name the service it wants in the \"service\" field.",
        });
    }

    return Results.Ok(await Invoker.InvokeAsync(graph.Root, type!, request, jsonOptions));
});
```

Note `target.GetType()` rather than the declared interface: the Invoker matches
methods by name and argument count, and the concrete type carries them. Verify
this against a service registered as an interface — if interface-declared methods
are not found on the concrete type through this path, use the resolved service
type instead and say so in your report.

- [ ] **Step 7: Run and verify all four pass**

Also confirm the Task 1 baseline still passes: a request with no `service` field
against a `LIB_TYPE` host must be byte-identical to v1.0.1.

- [ ] **Step 8: Verify non-vacuity**

For each of the four: remove the behaviour (the scoped check; the unknown-service
listing; the composition-root message; the service routing branch), observe the
failure, restore. Report all four observations.

- [ ] **Step 9: Commit**

```bash
git add src/RemoteClassHost/Contracts.cs src/RemoteClassHost/Program.cs \
        src/RemoteClassHost/HostedGraph.cs src/RemoteClassHost/Activation.cs \
        test/fixtures/CsLib/Store.cs test/run.sh
git commit -m "Route invocations by service name and reject scoped services"
```

---

### Task 5: Reset rebuilds the provider, safely against in-flight calls

**Files:**
- Modify: `src/RemoteClassHost/InstanceHolder.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `test/fixtures/CsLib/Store.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `InstanceHolder` from Task 2.
- Produces: `DELETE /instance` rebuilds the graph; `InstanceHolder` becomes safe to reset while calls are in flight.

- [ ] **Step 1: Add a fixture that proves a rebuild happened**

```csharp
public interface ICounter { int Next(); }

public sealed class Counter : ICounter
{
    private int _n;
    public int Next() => ++_n;
}
```

and register it in `GraphStartup.Configure`:

```csharp
        Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions
            .AddSingleton<ICounter, Counter>(services);
```

- [ ] **Step 2: Write the failing test**

```sh
echo "== DELETE /instance rebuilds the whole graph =="
api_service croot CsLib.ICounter Next >/dev/null
second=$(api_service croot CsLib.ICounter Next)
[ "$second" = '{"ok":true,"result":2}' ] \
  && ok "a singleton counts up within one graph" \
  || bad "a singleton counts up within one graph"

docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s -m 10 \
  -X DELETE http://croot:8080/instance >/dev/null

after=$(api_service croot CsLib.ICounter Next)
[ "$after" = '{"ok":true,"result":1}' ] \
  && ok "reset rebuilt the provider, so the singleton is new" \
  || bad "reset rebuilt the provider, so the singleton is new"
```

- [ ] **Step 3: Run and watch it fail**

Expected: the second assertion fails, returning 3 rather than 1, because reset
currently rebuilds only the root instance and the provider's singletons survive.

- [ ] **Step 4: Make reset safe and rebuilding**

Replace `src/RemoteClassHost/InstanceHolder.cs`:

```csharp
namespace RemoteClassHost;

/// <summary>
/// Owns the graph every call reaches, and swaps it under a lock.
///
/// The lock is not decoration. Reset disposes a provider that an in-flight
/// /invoke may be resolving from; without it, a reset during a call surfaces as
/// an ObjectDisposedException that looks like a bug in the caller's own code.
/// </summary>
public sealed class InstanceHolder(Func<HostedGraph> factory)
{
    private readonly ReaderWriterLockSlim _gate = new();
    private HostedGraph _current = factory();

    /// <summary>
    /// Runs <paramref name="work"/> against a graph that cannot be disposed
    /// while it runs. Callers must not hold the returned object past the call.
    /// </summary>
    public async Task<T> UseAsync<T>(Func<HostedGraph, Task<T>> work)
    {
        _gate.EnterReadLock();
        try
        {
            return await work(_current);
        }
        finally
        {
            _gate.ExitReadLock();
        }
    }

    public void Reset()
    {
        // Build the replacement BEFORE taking the write lock and disposing the
        // old one: if factory() throws, _current must stay pointed at the
        // still-valid old graph, not a disposed one that every later call hits.
        var next = factory();

        _gate.EnterWriteLock();
        try
        {
            var old = _current;
            _current = next;
            old.Dispose();
        }
        finally
        {
            _gate.ExitWriteLock();
        }
    }
}
```

`ReaderWriterLockSlim` is not async-aware, so a read lock held across an `await`
is held by the pool thread that resumes, not the one that took it. Use
`SemaphoreSlim`-based gating instead if the implementer confirms this is a
problem in practice — verify by running two concurrent long calls plus a reset
and reporting what happens. Do not leave a latent deadlock in place because the
tests happened not to hit it.

- [ ] **Step 5: Route calls through UseAsync**

In `Program.cs`, replace direct `holder.Current` reads in `/invoke` and
`/services` with `holder.UseAsync(...)`, so no call resolves from a graph that
reset may dispose mid-flight.

- [ ] **Step 6: Run and verify**

Expected: both assertions pass; everything else including the baseline passes.

- [ ] **Step 7: Verify non-vacuity and concurrency**

Delete the `old.Dispose()` and the rebuild (make `Reset` a no-op), rebuild, and
confirm the counter test fails. Restore.

Then, separately, drive a reset concurrently with two in-flight calls and report
what happens — this is the risk the spec names, and the plan will not accept "it
looked fine" as evidence.

- [ ] **Step 8: Commit**

```bash
git add src/RemoteClassHost/InstanceHolder.cs src/RemoteClassHost/Program.cs \
        test/fixtures/CsLib/Store.cs test/run.sh
git commit -m "Rebuild the whole graph on reset, safely against in-flight calls"
```

---

### Task 6: RemoteHost client and the environment helper

**Files:**
- Create: `src/RemoteClass.Client/RemoteHost.cs`
- Create: `src/RemoteClass.Client/RemoteHostEnvironment.cs`
- Modify: `src/RemoteClass.Client/RemoteClass.cs`
- Create: `test/fixtures/GraphClient/GraphClient.csproj`
- Create: `test/fixtures/GraphClient/Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `GET /services`, `POST /invoke` with `service`.
- Produces:
  - `RemoteHost.At(string baseUrl)`, `Task<T> GetAsync<T>() where T : class`, `Task ResetAsync()`, `IAsyncDisposable`.
  - `RemoteHostEnvironment.For<TStartup>(string? methodName = null)` returning `IDictionary<string, string>`.
  - `RemoteClass.For<T>(string baseUrl)` unchanged, and still NOT sending `service`.

- [ ] **Step 1: Allow the proxy to carry a service name**

In `src/RemoteClass.Client/RemoteClass.cs`, add a private field and an internal
factory beside the existing `For<T>`:

```csharp
    private string? _service;

    /// <summary>
    /// A proxy that names the service on every call. Used by RemoteHost.
    ///
    /// The name, not a handle to a resolved instance: the host resolves afresh
    /// per call, which is what lets ResetAsync rebuild the graph without
    /// invalidating proxies, and what makes a Transient registration yield a
    /// new instance per call.
    /// </summary>
    internal static T ForService<T>(HttpClient http, string service)
    {
        var proxy = Create<T, RemoteClass>()!;
        var self = (RemoteClass)(object)proxy;
        self._http = http;
        self._interfaceName = typeof(T).FullName!;
        self._service = service;
        return proxy;
    }
```

Then in `CallAsync`, include the service in the payload only when set. Read the
existing body construction first and match it; the change is to add
`service = _service` to the anonymous object ONLY when `_service` is not null,
because a `service` field present-but-null must not be sent for `For<T>`.

- [ ] **Step 2: Write RemoteHost**

Create `src/RemoteClass.Client/RemoteHost.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace RemoteClassHost.Client;

/// <summary>
/// A container hosting a composition root. Ask it for the services its startup
/// registered.
/// </summary>
public sealed class RemoteHost(HttpClient http) : IAsyncDisposable
{
    public static RemoteHost At(string baseUrl) =>
        new(new HttpClient { BaseAddress = new Uri(baseUrl) });

    /// <summary>
    /// Resolves the service in the container and returns a typed proxy.
    ///
    /// The round trip is deliberate. A missing registration fails HERE, naming
    /// the interface and listing what IS registered, rather than surfacing
    /// later as a confusing failure at the first method call.
    /// </summary>
    public async Task<T> GetAsync<T>() where T : class
    {
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException(
                $"{typeof(T).FullName} is not an interface. A remote proxy can only " +
                "implement an interface.", nameof(T));
        }

        var name = typeof(T).FullName!;
        var response = await http.GetAsync("/services");
        response.EnsureSuccessStatusCode();

        var registered = await response.Content.ReadFromJsonAsync<string[]>() ?? [];

        if (!registered.Contains(name))
        {
            throw new InvalidOperationException(
                $"the host has no service '{name}'. Registered services: " +
                string.Join(", ", registered));
        }

        return RemoteClass.ForService<T>(http, name);
    }

    /// <summary>Rebuilds the container's provider. Existing proxies stay valid.</summary>
    public async Task ResetAsync()
    {
        var response = await http.DeleteAsync("/instance");
        response.EnsureSuccessStatusCode();
    }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }
}
```

- [ ] **Step 3: Write the environment helper**

Create `src/RemoteClass.Client/RemoteHostEnvironment.cs`:

```csharp
namespace RemoteClassHost.Client;

/// <summary>
/// Builds the container's environment from the startup TYPE, so a fixture
/// cannot typo a name the compiler could have checked. A misspelled
/// LIB_REGISTRAR otherwise fails at container start with a runtime error.
/// </summary>
public static class RemoteHostEnvironment
{
    /// <param name="methodName">Defaults to "Configure".</param>
    public static IDictionary<string, string> For<TStartup>(string? methodName = null)
    {
        var startup = typeof(TStartup);
        var method = methodName ?? "Configure";

        if (startup.GetMethod(method) is null)
        {
            throw new ArgumentException(
                $"{startup.FullName} has no method '{method}'. The startup must expose " +
                $"a static {method}(IServiceCollection).", nameof(methodName));
        }

        return new Dictionary<string, string>
        {
            ["LIB_ASSEMBLY"] = startup.Assembly.GetName().Name + ".dll",
            ["LIB_REGISTRAR"] = $"{startup.FullName}.{method}",
        };
    }
}
```

- [ ] **Step 4: Write the client fixture**

Create `test/fixtures/GraphClient/GraphClient.csproj` modelled on the existing
`test/fixtures/CsClient/CsClient.csproj` — read that file and copy its shape,
changing only the assembly name and adding a project reference to `CsLib`.

Create `test/fixtures/GraphClient/Program.cs`:

```csharp
using CsLib;
using RemoteClassHost.Client;

await using var host = RemoteHost.At("http://croot:8080");

var facade = await host.GetAsync<IRootFacade>();
Console.WriteLine("RESULT: who " + facade.Who());

var counter = await host.GetAsync<ICounter>();
counter.Next();
await host.ResetAsync();
Console.WriteLine("RESULT: after-reset " + counter.Next());

try
{
    await host.GetAsync<IScopedThing>();
    Console.WriteLine("RESULT: scoped NONE-THROWN");
}
catch (Exception ex)
{
    Console.WriteLine("RESULT: scoped " + ex.GetType().Name);
}
```

`GetAsync<IScopedThing>` succeeds — the service IS registered, so the listing
check passes — and the rejection happens at the first CALL. Adjust the fixture to
call a method on it and report the error message, and say in your report which
of the two it turned out to be.

- [ ] **Step 5: Write the test cases**

```sh
echo "== the typed client drives a composition root =="
GC_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/GraphClient/GraphClient.csproj -c Release 2>&1 \
  | grep '^RESULT:') || GC_OUT=""

echo "$GC_OUT" | grep -q "RESULT: who root-facade" \
  && ok "GetAsync returned a working proxy" \
  || bad "GetAsync returned a working proxy"
echo "$GC_OUT" | grep -q "RESULT: after-reset 1" \
  && ok "ResetAsync rebuilt the graph and the proxy still works" \
  || bad "ResetAsync rebuilt the graph and the proxy still works"
```

- [ ] **Step 6: Run, verify, and check non-vacuity**

Delete the `service` field from the client payload, rebuild, and confirm the
`who` assertion fails. Restore. Report the observation.

- [ ] **Step 7: Commit**

```bash
git add src/RemoteClass.Client/RemoteHost.cs \
        src/RemoteClass.Client/RemoteHostEnvironment.cs \
        src/RemoteClass.Client/RemoteClass.cs \
        test/fixtures/GraphClient test/run.sh
git commit -m "Add the RemoteHost client for composition-root hosts"
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: nothing code depends on.

- [ ] **Step 1: Restructure the README**

Verify every claim against the built image, not against this plan. Cover:

1. **Composition-root hosting as the primary path** — the startup plus facade
   pattern, the two environment variables it needs, and `RemoteHost.At` /
   `GetAsync<T>`. Lead with this.
2. **Single-instance hosting as the quick path** — `LIB_TYPE` plus
   `LIB_OPTIONS`, unchanged, for one simple class. Say plainly which to choose:
   composition root when the wiring is non-trivial or more than one surface is
   needed, single instance for one class with simple configuration.
3. **The pass-by-value boundary**, with the measured example: a `Counter` with
   `Count = 5` passed to a method that calls `Bump()` returns `bumped to 6`
   while the caller's object still reads 5. State that an object with methods
   crosses as a COPY and mutations do not return, and that an interface argument
   fails loudly with
   `Deserialization of interface or abstract types is not supported`.
4. **Why the facade pattern follows from that** — a narrow interface taking
   simple values keeps complex objects inside the container where the startup
   built them.
5. **`Scoped` is rejected**, and why.
6. **`DELETE /instance` rebuilds the provider** in composition-root mode, what
   that clears (all service instances) and what it does not (anything written to
   a mounted share).
7. **`GET /services`**, with a real captured response.

- [ ] **Step 2: Verify the examples compile**

Every C# snippet in the README that a consumer would copy must compile against
the real package. Build the `GraphClient` fixture as the proof, and state in your
report which snippets you verified and how.

- [ ] **Step 3: Commit**

```bash
git add README.md
git commit -m "Document composition-root hosting and the value boundary"
```

---

### Task 8: Release v1.1.0

**SCOPE LIMIT: stop at a local commit.** Do not tag, push, or publish. The owner
performs the release.

**Files:**
- Modify: `README.md` (version references only, if any exist)

- [ ] **Step 1: Confirm the release workflow needs no change**

Read `.github/workflows/release.yml`. The tag validator, the image build, the
nuget.org Trusted Publishing step and the pack step should all work unchanged for
a `v1.1.0` tag. Confirm this by reading, and report anything that would not.

Do NOT edit the workflow unless something is actually wrong; it is reviewed and
working.

- [ ] **Step 2: Run the full suite one final time**

```bash
dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
dotnet publish test/fixtures/VbLib/VbLib.vbproj -c Release -o test/publish/vblib
docker build -t remote-class-host:dev .
./test/run.sh remote-class-host:dev
```

Report the final pass/fail count, and confirm explicitly that the Task 1 baseline
case is among the passes.

- [ ] **Step 3: Report and stop**

Report that the work is complete and the release decision is the owner's.

---

## Self-review

**Spec coverage.** Every spec section maps to a task: the problem statement and
root-type resolution to Task 2; the configuration contract and `GET /services` to
Task 3; the wire protocol and lifetimes to Task 4; reset semantics and the
concurrency risk to Task 5; the client API and environment helper to Task 6; the
boundary documentation and two-mode guidance to Task 7; backward compatibility to
Task 1, which guards it for every task that follows. The spec's ten test cases are
distributed across Tasks 1-6; cases 9 and 10 are Task 1's baseline plus Task 4's
no-service routing check.

**Placeholders.** None. Every code step carries real code. Three steps
deliberately instruct the implementer to READ existing code and match it rather
than transcribe my guess — the `api` helper convention in Task 2, the `Load`
split in Task 3, and the `CallAsync` payload shape in Task 6 — because I have not
read those bodies closely enough to reproduce them exactly, and a wrong
transcription would be worse than an instruction to look.

**Type consistency.** `HostedGraph` is constructed in exactly two places, both in
`Activation.Build`, and gains a fourth constructor parameter in Task 4 — both call
sites are named there. `InstanceHolder` changes shape twice (Task 2 to hold a
graph, Task 5 to add locking); Task 5 replaces the whole file rather than patching
it, so the two cannot drift. `RemoteClass.ForService<T>` is defined in Task 6 Step
1 and used in Task 6 Step 2 with matching parameters.

**Known weak points, flagged rather than hidden.** Task 4 Step 6 uses
`target.GetType()` for method lookup and tells the implementer to verify it
against an interface-registered service. Task 5 Step 4 raises that
`ReaderWriterLockSlim` is not async-aware and requires the implementer to prove
the behaviour rather than assume the code as written is correct. Task 6 Step 4
notes that the scoped rejection happens at call time, not at `GetAsync`, and asks
the implementer to confirm which. These are the three places most likely to be
wrong, and each says so.
