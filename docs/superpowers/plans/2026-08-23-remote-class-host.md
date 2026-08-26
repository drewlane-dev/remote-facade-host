# remote-class-host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One image that runs any .NET class library in a container and exposes its members over HTTP, plus a client package giving test processes type-safe remote calls.

**Architecture:** The host loads a mounted `dotnet publish` folder into the **default** `AssemblyLoadContext`, constructs a configured type by satisfying `IOptions<T>` and `ILogger<T>`, and invokes its methods by reflection through a single `/invoke` endpoint. The client is a `DispatchProxy` over the library's own interface, so there is no code generation and the compiler enforces the contract.

**Tech Stack:** .NET 10 (`sdk:10.0-alpine` to build, `aspnet:10.0-alpine` to run), ASP.NET minimal API, `System.Reflection.DispatchProxy`, optional `cifs-utils`, Docker, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-08-23-remote-class-host-design.md`

## Global Constraints

- Image `ghcr.io/drewlane-dev/remote-class-host`; package `RemoteClass.Client`.
- **Load the plugin into the DEFAULT `AssemblyLoadContext`.** A separate context gives the plugin different type identities, so the host's `typeof(IOptions<>)` is not the plugin's and every dependency shape must be resolved by string. This was measured in a spike; two attempts failed on it before the default context made it work.
- **`NullLogger<T>.Instance` does not come back from `GetProperty("Instance")`** on a constructed generic type — it returns null. Use `Activator.CreateInstance` instead. Also measured.
- The client must handle **four** return shapes: `void` → null, `Task` → completed, `Task<T>` → wrapped, anything else → deserialise directly. An early spike version assumed everything was `Task`/`Task<T>` and threw on the first synchronous method.
- Top-level statements already define `args` — name any local `ctorArgs` or similar.
- Mounting is **optional**: no `SMB_SERVER` means no mount and no elevated capabilities required.
- Multi-arch mandatory: `linux/amd64` and `linux/arm64`. Tags for `v1.2.3`: `1.2.3`, `1.2`, `1`, `latest`.
- **One instance per container, constructed at startup, living until it stops.** Every `/invoke` reaches the same object — that is what lets a `lock()` call be released by a later `unlock()`. Calls are concurrent against it, and state outlives any single test.
- `/invoke` executes arbitrary methods on a loaded assembly. Test networks only; never expose it.

---

### Task 1: Host loads and constructs a class

**Files:**
- Create: `src/RemoteClassHost/RemoteClassHost.csproj`
- Create: `src/RemoteClassHost/PluginLoader.cs`
- Create: `src/RemoteClassHost/Activation.cs`
- Create: `src/RemoteClassHost/Program.cs`
- Create: `Dockerfile`, `.dockerignore`, `LICENSE`
- Create: `test/fixtures/CsLib/CsLib.csproj`, `test/fixtures/CsLib/Store.cs`
- Test: `test/run.sh`

**Interfaces:**
- Consumes: nothing.
- Produces: image `remote-class-host:dev`; `PluginLoader.Load(string dir, string assemblyFile, string typeName)` → `Type`; `Activation.Create(Type, string optionsJson)` → `object`; `GET /health`, `GET /types`.

- [ ] **Step 1: Write the failing test**

Create `test/run.sh`:

```sh
#!/bin/sh
# Self-tests for remote-class-host. Each case runs a real container.
set -eu

IMAGE="${1:-remote-class-host:dev}"
PASS=0
FAIL=0
HERE="$(cd "$(dirname "$0")" && pwd)"

ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; }

# One long-lived container per plugin; api() talks to it over a private network.
NET="rch-test-$$"
cleanup() {
  for c in $(docker ps -aq --filter "network=${NET}" 2>/dev/null); do
    docker rm -f "$c" >/dev/null 2>&1 || true
  done
  docker network rm "${NET}" >/dev/null 2>&1 || true
}
trap cleanup EXIT
docker network create "${NET}" >/dev/null

start_host() { # start_host <alias> <pluginDir> <assembly> <type> <optionsJson>
  docker run -d --rm --name "$1-${NET}" --network "${NET}" --network-alias "$1" \
    -v "$2:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY="$3" -e LIB_TYPE="$4" -e LIB_OPTIONS="$5" \
    "$IMAGE" >/dev/null 2>&1
}

api() { # api <alias> <path> [curl args...]
  alias_="$1"; path="$2"; shift 2
  docker run --rm --network "${NET}" curlimages/curl:8.10.1 -s "$@" "http://${alias_}:8080${path}" 2>/dev/null
}

wait_healthy() {
  for _ in $(seq 1 30); do
    [ "$(api "$1" /health -o /dev/null -w '%{http_code}')" = "200" ] && return 0
    sleep 1
  done
  return 1
}

echo "== loads and constructs a C# class =="
start_host cs "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}'
if wait_healthy cs; then
  ok "host constructs the configured type"
else
  docker logs "cs-${NET}" 2>&1 | tail -4
  bad "host constructs the configured type"
fi

echo "== /types is a usable diagnostic =="
api cs /types | grep -q "CsLib.Store" \
  && ok "/types lists the assembly's types" || bad "/types lists the assembly's types"

echo "== a missing type fails fast =="
if start_host bad1 "${HERE}/publish/cslib" CsLib.dll CsLib.NoSuchType '{}' && wait_healthy bad1; then
  bad "unknown LIB_TYPE exits non-zero"
else
  ok "unknown LIB_TYPE exits non-zero"
fi

echo
echo "passed: $PASS  failed: $FAIL"
[ "$FAIL" -eq 0 ]
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd /Users/drew/repos/remote-class-host && chmod +x test/run.sh && ./test/run.sh`
Expected: FAIL — the image does not exist and `test/publish/cslib` has not been produced.

- [ ] **Step 3: Write the C# test fixture library**

Create `test/fixtures/CsLib/CsLib.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/CsLib/Store.cs`:

```csharp
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CsLib;

// Exercises all four return shapes the client must handle.
public interface IStore
{
    Task WriteAsync(string name, string content);
    Task<string> ReadAsync(string name);
    int Count();
    void Touch(string name);
}

public sealed class StoreOptions { public string RootPath { get; set; } = "/tmp"; }

// The dominant .NET constructor shape: IOptions<T> plus ILogger<T>.
public sealed class Store(IOptions<StoreOptions> options, ILogger<Store> logger) : IStore
{
    private readonly string _root = options.Value.RootPath;

    public Task WriteAsync(string name, string content)
    {
        logger.LogInformation("writing {Name}", name);
        return File.WriteAllTextAsync(Path.Combine(_root, name), content);
    }

    public Task<string> ReadAsync(string name) => File.ReadAllTextAsync(Path.Combine(_root, name));

    public int Count() => Directory.GetFiles(_root).Length;

    public void Touch(string name) => File.WriteAllText(Path.Combine(_root, name), "touched");
}
```

- [ ] **Step 4: Write `PluginLoader`**

Create `src/RemoteClassHost/PluginLoader.cs`:

```csharp
using System.Reflection;

namespace RemoteClassHost;

public static class PluginLoader
{
    /// <summary>
    /// Loads the plugin assembly into the DEFAULT AssemblyLoadContext and returns
    /// the requested type.
    ///
    /// The default context is load-bearing, not a shortcut. A separate
    /// AssemblyLoadContext gives the plugin its own copies of shared assemblies,
    /// so this host's typeof(IOptions&lt;&gt;) becomes a DIFFERENT type identity
    /// from the plugin's — every dependency shape then has to be matched by
    /// string and resolved out of the plugin's assemblies, and the failures are
    /// cryptic nulls. Sharing the context unifies identities. The cost is that
    /// host and plugin must agree on shared package versions.
    /// </summary>
    public static Type Load(string dir, string assemblyFile, string typeName)
    {
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var candidate = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        };

        var path = Path.Combine(dir, assemblyFile);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"assembly not found: {path}");
        }

        Assembly = Assembly.LoadFrom(path);

        return Assembly.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"type '{typeName}' not found in {assemblyFile}. " +
                $"Available: {string.Join(", ", TypeNames())}");
    }

    /// <summary>The loaded assembly, exposed so /types can report on it.</summary>
    public static Assembly? Assembly { get; private set; }

    /// <summary>
    /// Public type names in the loaded assembly. Exists because a wrong LIB_TYPE
    /// is otherwise a dead end — notably for VB libraries, where RootNamespace is
    /// PREPENDED to declared namespaces, so the real name is often not what a C#
    /// developer would write.
    /// </summary>
    public static IEnumerable<string> TypeNames() =>
        Assembly?.GetExportedTypes().Select(t => t.FullName!) ?? [];
}
```

- [ ] **Step 5: Write `Activation`**

Create `src/RemoteClassHost/Activation.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace RemoteClassHost;

public static class Activation
{
    /// <summary>
    /// Constructs the type, satisfying the two dependency shapes that dominate
    /// .NET libraries. Anything else fails fast, naming the parameter type,
    /// rather than throwing a NullReferenceException from deep in reflection.
    /// </summary>
    public static object Create(Type type, string optionsJson)
    {
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{type.FullName} has no public constructor");

        var ctorArgs = ctor.GetParameters().Select(p =>
        {
            var pt = p.ParameterType;

            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(IOptions<>))
            {
                var inner = pt.GetGenericArguments()[0];
                var value = JsonSerializer.Deserialize(optionsJson, inner,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                return typeof(Options).GetMethod("Create")!
                    .MakeGenericMethod(inner).Invoke(null, [value]);
            }

            if (pt.IsGenericType && pt.GetGenericTypeDefinition() == typeof(ILogger<>))
            {
                // NOT GetProperty("Instance").GetValue(null) — on a constructed
                // generic that returns null and you get a NullReferenceException
                // with no indication why. Measured.
                return Activator.CreateInstance(
                    typeof(NullLogger<>).MakeGenericType(pt.GetGenericArguments()[0]));
            }

            throw new InvalidOperationException(
                $"cannot supply constructor parameter '{p.Name}' of type {pt.FullName}. " +
                "Only IOptions<T> and ILogger<T> are supported.");
        }).ToArray();

        return ctor.Invoke(ctorArgs);
    }
}
```

- [ ] **Step 6: Write `Program.cs`**

Create `src/RemoteClassHost/Program.cs`:

```csharp
using RemoteClassHost;

var dir      = Environment.GetEnvironmentVariable("LIB_DIR") ?? "/plugin";
var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var typeName = Environment.GetEnvironmentVariable("LIB_TYPE")
               ?? throw new InvalidOperationException("LIB_TYPE is required");
var optsJson = Environment.GetEnvironmentVariable("LIB_OPTIONS") ?? "{}";
var port     = Environment.GetEnvironmentVariable("LIB_PORT") ?? "8080";

// Fail before serving. A host that starts without a usable instance would make
// every test using it fail confusingly at first call instead of at startup.
var type = PluginLoader.Load(dir, asmFile, typeName);

// One instance serves every call for the container's lifetime; that is what
// allows a method to acquire a resource and a later call to release it. Task 2
// replaces this local with an InstanceHolder so DELETE /instance can reset it.
var instance = Activation.Create(type, optsJson);

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { type = type.FullName }));
app.MapGet("/types", () => Results.Ok(PluginLoader.TypeNames()));

app.Run();
```

- [ ] **Step 7: Write the project file, `Dockerfile`, `.dockerignore`, `LICENSE`**

Create `src/RemoteClassHost/RemoteClassHost.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
  </PropertyGroup>
  <ItemGroup>
    <!-- Shared with plugins so type identities unify in the default load context.
         Keep this list as small as possible: every package here is one the
         plugin must agree with on version. -->
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Create `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/RemoteClassHost/RemoteClassHost.csproj src/RemoteClassHost/
RUN dotnet restore src/RemoteClassHost/RemoteClassHost.csproj
COPY src/RemoteClassHost/ src/RemoteClassHost/
RUN dotnet publish src/RemoteClassHost/RemoteClassHost.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine

# Only needed when LIB mounting is configured; harmless otherwise and small.
RUN apk add --no-cache cifs-utils

WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "RemoteClassHost.dll"]
```

Create `.dockerignore`:

```
**/bin
**/obj
.git
docs
test
```

Create `LICENSE` — the standard MIT License text, copyright `2026 Drew Lane`.

- [ ] **Step 8: Publish the fixture and run the tests**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o /w/test/publish/cslib
docker build -t remote-class-host:dev .
./test/run.sh
```
Expected: PASS — `passed: 3  failed: 0`.

- [ ] **Step 9: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add src Dockerfile .dockerignore LICENSE test
git commit -m "Add host that loads and constructs a class from a mounted publish folder"
```

---

### Task 2: `/invoke` and the client package

**Files:**
- Create: `src/RemoteClassHost/Invoker.cs`
- Create: `src/RemoteClassHost/Contracts.cs`
- Create: `src/RemoteClass.Client/RemoteClass.Client.csproj`
- Create: `src/RemoteClass.Client/RemoteClass.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Create: `test/fixtures/CsClient/CsClient.csproj`, `test/fixtures/CsClient/Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `PluginLoader.Load`, `Activation.Create` from Task 1.
- Produces: `POST /invoke`; `record InvokeRequest(string Method, JsonElement[] Args)`; `RemoteClass.For<T>(string baseUrl)` → `T`.

- [ ] **Step 1: Write the failing test**

Append to `test/run.sh` before the summary `echo`:

```sh
echo "== all four return shapes over /invoke =="
CLIENT_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/CsClient/CsClient.csproj -c Release 2>&1 | grep '^RESULT:')

echo "$CLIENT_OUT" | grep -q "RESULT: async-void ok" \
  && ok "Task (async, no value)" || bad "Task (async, no value)"
echo "$CLIENT_OUT" | grep -q "RESULT: async-value hello" \
  && ok "Task<T> (async with value)" || bad "Task<T> (async with value)"
echo "$CLIENT_OUT" | grep -q "RESULT: sync-value 2" \
  && ok "synchronous method with a return value" || bad "synchronous method with a return value"
echo "$CLIENT_OUT" | grep -q "RESULT: sync-void ok" \
  && ok "synchronous void method" || bad "synchronous void method"

echo "== instance lifetime =="
# Count reflects files written by EARLIER calls, so a non-zero count proves the
# same instance served both -- a per-call instance would still see the files, so
# assert on the reset instead: after DELETE /instance the object is new.
api cs /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Touch","args":["persist.txt"]}' >/dev/null
code=$(api cs /instance -X DELETE -o /dev/null -w '%{http_code}')
[ "$code" = "204" ] && ok "DELETE /instance resets the object" \
                    || bad "DELETE /instance resets the object (got $code)"
api cs /health | grep -q "CsLib.Store" \
  && ok "host still serves after a reset" || bad "host still serves after a reset"
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd /Users/drew/repos/remote-class-host && ./test/run.sh`
Expected: FAIL — the four new cases fail; `CsClient` does not exist and `/invoke` returns 404.

- [ ] **Step 3: Write `Contracts` and `Invoker`**

Create `src/RemoteClassHost/Contracts.cs`:

```csharp
using System.Text.Json;

namespace RemoteClassHost;

public sealed record InvokeRequest(string Method, JsonElement[] Args);
```

Create `src/RemoteClassHost/Invoker.cs`:

```csharp
using System.Reflection;
using System.Text.Json;

namespace RemoteClassHost;

public static class Invoker
{
    /// <summary>
    /// Invokes a method by name and shapes the result for the wire. Handles both
    /// Task and Task&lt;T&gt; as well as plain synchronous returns — the host does
    /// not care whether the library is async, and neither should callers.
    /// </summary>
    public static async Task<object> InvokeAsync(object instance, Type type, InvokeRequest request)
    {
        var argCount = request.Args?.Length ?? 0;

        var method = type.GetMethods()
            .FirstOrDefault(m => m.Name == request.Method && m.GetParameters().Length == argCount);

        if (method is null)
        {
            return new { ok = false, error = $"no method '{request.Method}' taking {argCount} argument(s)" };
        }

        var ps = method.GetParameters();
        var callArgs = new object?[ps.Length];

        for (var i = 0; i < ps.Length; i++)
        {
            callArgs[i] = request.Args![i].Deserialize(ps[i].ParameterType,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }

        try
        {
            var result = method.Invoke(instance, callArgs);

            if (result is Task task)
            {
                await task;

                return method.ReturnType.IsGenericType
                    ? new { ok = true, result = method.ReturnType.GetProperty("Result")!.GetValue(task) }
                    : new { ok = true, result = (object?)null };
            }

            return new { ok = true, result };
        }
        catch (TargetInvocationException ex)
        {
            // Unwrap: the caller wants the library's exception, not reflection's.
            return new { ok = false, error = ex.InnerException?.Message ?? ex.Message };
        }
    }
}
```

- [ ] **Step 4: Wire `/invoke`**

In `src/RemoteClassHost/Program.cs`, insert immediately before `app.Run();`:

```csharp
app.MapPost("/invoke", async (InvokeRequest request) =>
    Results.Ok(await Invoker.InvokeAsync(holder.Current, type, request)));
```

- [ ] **Step 4b: Write `InstanceHolder` and `DELETE /instance`**

Create `src/RemoteClassHost/InstanceHolder.cs`:

```csharp
namespace RemoteClassHost;

/// <summary>
/// Owns the single instance every call reaches.
///
/// Reset exists because the container outlives any individual test: a test that
/// deliberately leaves a lock held would otherwise poison the next one, and the
/// resulting failure would point at the wrong test.
/// </summary>
public sealed class InstanceHolder(Func<object> factory)
{
    private object _current = factory();

    public object Current => _current;

    public void Reset()
    {
        (_current as IDisposable)?.Dispose();
        _current = factory();
    }
}
```

Then in `src/RemoteClassHost/Program.cs`, immediately after the `/invoke` mapping:

```csharp
app.MapDelete("/instance", () =>
{
    holder.Reset();
    return Results.NoContent();
});
```

- [ ] **Step 5: Write the client package**

Create `src/RemoteClass.Client/RemoteClass.Client.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PackageId>RemoteClass.Client</PackageId>
    <Description>Type-safe client for remote-class-host. Protocol only: no test framework or container dependencies.</Description>
    <Authors>Drew Lane</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
  </PropertyGroup>
</Project>
```

Create `src/RemoteClass.Client/RemoteClass.cs`:

```csharp
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RemoteClass;

/// <summary>
/// A typed client for a class hosted in a remote-class-host container.
///
/// No code generation: your test project already references the library for its
/// interface, so DispatchProxy can implement that interface and forward every
/// call. The compiler enforces the contract, and nothing regenerates when the
/// interface changes.
/// </summary>
public class RemoteClass : DispatchProxy
{
    private HttpClient _http = null!;

    public static T For<T>(string baseUrl)
    {
        var proxy = Create<T, RemoteClass>()!;
        ((RemoteClass)(object)proxy)._http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);

        var body = JsonSerializer.Serialize(new { method = method.Name, args = args ?? [] });

        var response = _http.PostAsync("/invoke",
            new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

        var json = JsonDocument.Parse(
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        if (!json.RootElement.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        }

        var resultEl = json.RootElement.GetProperty("result");
        var rt = method.ReturnType;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        // Four shapes, all distinct. An earlier version assumed everything was
        // Task or Task<T> and threw on the first synchronous method.
        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;

        if (!rt.IsGenericType || rt.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(rt, opts);
        }

        var inner = rt.GetGenericArguments()[0];
        var value = resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(inner, opts);

        return typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(inner).Invoke(null, [value]);
    }
}
```

- [ ] **Step 6: Write the client test fixture**

Create `test/fixtures/CsClient/CsClient.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../CsLib/CsLib.csproj" />
    <ProjectReference Include="../../../src/RemoteClass.Client/RemoteClass.Client.csproj" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/CsClient/Program.cs`:

```csharp
using CsLib;
using RemoteClass;

// The "test process". It references CsLib only for IStore; the real Store runs
// in the container.
IStore store = RemoteClass.RemoteClass.For<IStore>("http://cs:8080");

await store.WriteAsync("a.txt", "hello");
Console.WriteLine("RESULT: async-void ok");

Console.WriteLine("RESULT: async-value " + await store.ReadAsync("a.txt"));

store.Touch("b.txt");
Console.WriteLine("RESULT: sync-void ok");

Console.WriteLine("RESULT: sync-value " + store.Count());
```

- [ ] **Step 7: Rebuild and run**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker build -q -t remote-class-host:dev . && ./test/run.sh
```
Expected: PASS — `passed: 9  failed: 0`.

- [ ] **Step 8: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add src test
git commit -m "Add /invoke and the RemoteClass.Client DispatchProxy

Handles all four return shapes: void, Task, Task<T>, and plain synchronous
returns."
```

---

### Task 3: Prove language independence with VB

**Files:**
- Create: `test/fixtures/VbLib/VbLib.vbproj`, `test/fixtures/VbLib/Store.vb`
- Create: `test/fixtures/VbClient/VbClient.csproj`, `test/fixtures/VbClient/Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: everything from Tasks 1-2.
- Produces: nothing new; proves an existing claim.

- [ ] **Step 1: Write the failing test**

Append to `test/run.sh` before the summary `echo`:

```sh
echo "== the same image serves a VB library, unchanged =="
start_host vb "${HERE}/publish/vblib" VbLib.dll VbLib.VbStore '{"RootPath":"/tmp"}'
if wait_healthy vb; then
  ok "host constructs a VB type"
else
  docker logs "vb-${NET}" 2>&1 | tail -4
  bad "host constructs a VB type"
fi

VB_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/VbClient/VbClient.csproj -c Release 2>&1 | grep '^RESULT:')

echo "$VB_OUT" | grep -q "RESULT: vb-sync VB store" \
  && ok "VB synchronous method with a return value" || bad "VB synchronous method with a return value"
echo "$VB_OUT" | grep -q "RESULT: vb-async touched by VB" \
  && ok "VB async method" || bad "VB async method"
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd /Users/drew/repos/remote-class-host && ./test/run.sh`
Expected: FAIL — `test/publish/vblib` does not exist.

- [ ] **Step 3: Write the VB fixture**

Create `test/fixtures/VbLib/VbLib.vbproj`. **Note the empty `RootNamespace`** — VB *prepends* it to declared namespaces, so leaving the default would make the real type `VbLib.VbLib.VbStore`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <RootNamespace></RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Options" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/VbLib/Store.vb`:

```vb
Imports System.IO
Imports System.Threading.Tasks
Imports Microsoft.Extensions.Logging
Imports Microsoft.Extensions.Options

Namespace VbLib

    Public Interface IVbStore
        Function Describe() As String
        Sub Touch(name As String)
        Function ReadAsync(name As String) As Task(Of String)
    End Interface

    Public Class VbOptions
        Public Property RootPath As String = "/tmp"
    End Class

    Public Class VbStore
        Implements IVbStore

        Private ReadOnly _root As String

        Public Sub New(options As IOptions(Of VbOptions), logger As ILogger(Of VbStore))
            _root = options.Value.RootPath
        End Sub

        Public Function Describe() As String Implements IVbStore.Describe
            Return "VB store rooted at " & _root
        End Function

        Public Sub Touch(name As String) Implements IVbStore.Touch
            File.WriteAllText(Path.Combine(_root, name), "touched by VB")
        End Sub

        Public Async Function ReadAsync(name As String) As Task(Of String) Implements IVbStore.ReadAsync
            Return Await File.ReadAllTextAsync(Path.Combine(_root, name))
        End Function

    End Class

End Namespace
```

- [ ] **Step 4: Write the VB client fixture**

Create `test/fixtures/VbClient/VbClient.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../VbLib/VbLib.vbproj" />
    <ProjectReference Include="../../../src/RemoteClass.Client/RemoteClass.Client.csproj" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/VbClient/Program.cs` — a **C#** process driving a **VB** interface:

```csharp
using RemoteClass;
using VbLib;

IVbStore store = RemoteClass.RemoteClass.For<IVbStore>("http://vb:8080");

Console.WriteLine("RESULT: vb-sync " + store.Describe());

store.Touch("vb.txt");
Console.WriteLine("RESULT: vb-async " + await store.ReadAsync("vb.txt"));
```

- [ ] **Step 5: Publish the VB fixture and run**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish test/fixtures/VbLib/VbLib.vbproj -c Release -o /w/test/publish/vblib
./test/run.sh
```
Expected: PASS — `passed: 12  failed: 0`.

- [ ] **Step 6: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add test
git commit -m "Prove the same image serves a VB library unchanged"
```

---

### Task 4: Optional SMB mount and the two-instance test

The payoff: two containers behaving as two independent SMB clients.

**Files:**
- Create: `src/RemoteClassHost/ShareMounter.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Create: `test/fixtures/TwoInstanceClient/TwoInstanceClient.csproj`, `.../Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: Tasks 1-2.
- Produces: `ShareMounter.MountIfConfigured()` → `bool` (true if a mount happened).

- [ ] **Step 1: Write the failing test**

Append to `test/run.sh` before the summary `echo`:

```sh
echo "== two instances against one share =="
docker run -d --rm --name "samba-${NET}" --network "${NET}" --network-alias samba \
  -e SMB_USER=azure -e SMB_PASS='Passw0rd!' -e SMB_UID=0 \
  ghcr.io/drewlane-dev/azure-files-emulator:1 >/dev/null
sleep 6

for n in ia ib; do
  docker run -d --rm --name "$n-${NET}" --network "${NET}" --network-alias "$n" \
    --cap-add SYS_ADMIN --cap-add DAC_READ_SEARCH --security-opt apparmor=unconfined \
    -v "${HERE}/publish/cslib:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY=CsLib.dll -e LIB_TYPE=CsLib.Store \
    -e LIB_OPTIONS='{"RootPath":"/mnt/share"}' \
    -e SMB_SERVER=samba -e SMB_SHARE=data \
    "$IMAGE" >/dev/null 2>&1
done
wait_healthy ia && wait_healthy ib && ok "both instances mounted the share" \
  || bad "both instances mounted the share"

TWO_OUT=$(docker run --rm --network "${NET}" -v "${HERE}/..:/w" -w /w \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/TwoInstanceClient/TwoInstanceClient.csproj -c Release 2>&1 | grep '^RESULT:')

echo "$TWO_OUT" | grep -q "RESULT: b-read written-by-a" \
  && ok "instance B reads what A wrote, over its own mount" \
  || bad "instance B reads what A wrote, over its own mount"

# Server-side truth. Without this a host whose mount silently failed would write
# to its own container filesystem and the test above could still pass.
docker exec "samba-${NET}" sh -c 'cat /srv/data/shared.txt 2>/dev/null' | grep -q "written-by-a" \
  && ok "the bytes really landed on the share (verified server-side)" \
  || bad "the bytes really landed on the share (verified server-side)"
```

- [ ] **Step 2: Run to verify it fails**

Run: `cd /Users/drew/repos/remote-class-host && ./test/run.sh`
Expected: FAIL — the hosts ignore `SMB_SERVER`, so `/mnt/share` is a plain directory inside each container and B cannot see A's file.

- [ ] **Step 3: Write `ShareMounter`**

Create `src/RemoteClassHost/ShareMounter.cs`:

```csharp
using System.Diagnostics;

namespace RemoteClassHost;

public static class ShareMounter
{
    /// <summary>
    /// Mounts an SMB share if one is configured, and returns whether it did.
    ///
    /// Optional by design: a library under test may need no filesystem at all,
    /// and such a host should not require CAP_SYS_ADMIN. When a mount IS
    /// configured, failure is fatal — serving against an unmounted path would
    /// write to the container's own filesystem, contend with nothing, and make
    /// multi-instance tests pass while proving nothing.
    /// </summary>
    public static bool MountIfConfigured()
    {
        var server = Environment.GetEnvironmentVariable("SMB_SERVER");
        var share = Environment.GetEnvironmentVariable("SMB_SHARE");

        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(share))
        {
            return false;
        }

        var user = Environment.GetEnvironmentVariable("SMB_USER") ?? "azure";
        var pass = Environment.GetEnvironmentVariable("SMB_PASS") ?? "Passw0rd!";
        var mountPoint = Environment.GetEnvironmentVariable("SMB_MOUNT_POINT") ?? "/mnt/share";
        var options = Environment.GetEnvironmentVariable("SMB_MOUNT_OPTIONS")
            ?? "vers=3.1.1,uid=0,gid=0,file_mode=0777,dir_mode=0777,serverino,nosharesock,actimeo=30,mfsymlinks,seal";

        Directory.CreateDirectory(mountPoint);

        var psi = new ProcessStartInfo("mount")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("-t");
        psi.ArgumentList.Add("cifs");
        psi.ArgumentList.Add($"//{server}/{share}");
        psi.ArgumentList.Add(mountPoint);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"username={user},password={pass},{options}");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start mount");

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"mount -t cifs //{server}/{share} failed with exit code " +
                $"{process.ExitCode}: {stderr.Trim()}");
        }

        return true;
    }
}
```

- [ ] **Step 4: Mount before loading**

In `src/RemoteClassHost/Program.cs`, insert immediately after the `var port = ...` line and before `var type = PluginLoader.Load(...)`:

```csharp
// Before constructing anything: the library may open paths on the share in its
// constructor.
ShareMounter.MountIfConfigured();
```

- [ ] **Step 5: Write the two-instance client fixture**

Create `test/fixtures/TwoInstanceClient/TwoInstanceClient.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../CsLib/CsLib.csproj" />
    <ProjectReference Include="../../../src/RemoteClass.Client/RemoteClass.Client.csproj" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/TwoInstanceClient/Program.cs`:

```csharp
using CsLib;
using RemoteClass;

// Two REAL instances, each in its own container with its own SMB mount and its
// own SMB session. This is what the image exists for.
IStore a = RemoteClass.RemoteClass.For<IStore>("http://ia:8080");
IStore b = RemoteClass.RemoteClass.For<IStore>("http://ib:8080");

await a.WriteAsync("shared.txt", "written-by-a");

// B reads it back over a DIFFERENT mount and a different session.
Console.WriteLine("RESULT: b-read " + await b.ReadAsync("shared.txt"));
```

- [ ] **Step 6: Rebuild and run**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker build -q -t remote-class-host:dev . && ./test/run.sh
```
Expected: PASS — `passed: 15  failed: 0`.

If "instance B reads what A wrote" passes but the server-side check fails, the mount silently failed and both instances wrote to their own container filesystems. Do not relax the server-side assertion; it exists precisely to catch that.

- [ ] **Step 7: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add src test
git commit -m "Add optional SMB mount and the two-instance test

Two containers, two mounts, two SMB sessions. Verified server-side, because a
silently failed mount would otherwise make the test pass while proving nothing."
```

---

### Task 5: Service registration for nested dependencies

Lets the host construct types whose constructors need more than `IOptions<T>` and `ILogger<T>`. Without this the image only serves the one narrow constructor shape, and pointing it at real application code fails at startup.

**Files:**
- Modify: `src/RemoteClassHost/RemoteClassHost.csproj`
- Modify: `src/RemoteClassHost/Activation.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `test/fixtures/CsLib/Store.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `Activation.Create(Type, string)` from Task 1; `PluginLoader.Assembly` from Task 1.
- Produces: `Activation.Create(Type type, string optionsJson, string servicesJson)` — the third parameter is new; callers in `Program.cs` must pass `LIB_SERVICES`.

- [ ] **Step 1: Write the failing test**

Add a dependency to the fixture so there is something to register. In `test/fixtures/CsLib/Store.cs`, add above `Store`:

```csharp
public interface IStamp { string Value(); }

public sealed class RealStamp : IStamp { public string Value() => "real"; }

// A fake compiled INTO the plugin. Mocks from the test process cannot be
// injected into a remote instance; a fake named in LIB_SERVICES is how you
// substitute behaviour.
public sealed class FakeStamp : IStamp { public string Value() => "fake"; }

// A CONCRETE dependency, the shape a real library usually has — e.g.
// GitManager(GitConfigManager). Not sealed, method virtual, so it can be
// substituted; a sealed class with non-virtual members could not be.
public class Inner { public virtual string Describe() => "inner-real"; }

public sealed class FakeInner : Inner { public override string Describe() => "inner-fake"; }

public sealed class Outer(Inner inner)
{
    public string Describe() => inner.Describe();
}

// The registration method a real application already has. LIB_REGISTRAR points
// the host at one of these so the graph wires itself with the app's own
// lifetimes, instead of every interface being enumerated in an env var.
public static class Registration
{
    public static IServiceCollection AddCsLib(this IServiceCollection services)
    {
        services.AddSingleton<IStamp, RealStamp>();
        return services;
    }
}
```

`CsLib.csproj` needs `<PackageReference Include="Microsoft.Extensions.DependencyInjection.Abstractions" Version="10.0.0" />` added to its existing `ItemGroup` for `IServiceCollection`.

Change `Store`'s constructor and add a method exposing the dependency:

```csharp
public sealed class Store(IOptions<StoreOptions> options, ILogger<Store> logger, IStamp stamp) : IStore
{
    private readonly string _root = options.Value.RootPath;

    public string Stamp() => stamp.Value();
```

and add `string Stamp();` to `IStore`.

Then append to `test/run.sh` before the summary `echo`:

```sh
echo "== service registration =="
start_host stampreal "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.RealStamp"}'
wait_healthy stampreal \
  && ok "constructs a type with a registered dependency" \
  || bad "constructs a type with a registered dependency"

api stampreal /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "resolves the registered implementation" || bad "resolves the registered implementation"

# The same library, a different implementation, no rebuild: this is how you
# substitute a fake for a dependency.
start_host stampfake "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.FakeStamp"}'
wait_healthy stampfake >/dev/null 2>&1
api stampfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"fake"' \
  && ok "a fake can be substituted by configuration alone" \
  || bad "a fake can be substituted by configuration alone"

echo "== a concrete dependency needs no registration, and can still be swapped =="
# CsLib.Store takes IStamp (an interface), but a real library often takes a
# concrete class. Nested.Outer(Nested.Inner) proves both halves: Inner resolves
# with no config, and can be replaced by a subclass with one line.
start_host nested "${HERE}/publish/cslib" CsLib.dll CsLib.Outer '{}' \
  '{"CsLib.IStamp":"CsLib.RealStamp"}'
wait_healthy nested \
  && ok "concrete dependency resolves with no registration" \
  || bad "concrete dependency resolves with no registration"

api nested /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Describe","args":[]}' | grep -q '"result":"inner-real"' \
  && ok "the real concrete dependency was used" || bad "the real concrete dependency was used"

start_host nestedfake "${HERE}/publish/cslib" CsLib.dll CsLib.Outer '{}' \
  '{"CsLib.IStamp":"CsLib.RealStamp","CsLib.Inner":"CsLib.FakeInner"}'
wait_healthy nestedfake >/dev/null 2>&1
api nestedfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Describe","args":[]}' | grep -q '"result":"inner-fake"' \
  && ok "a concrete dependency can be replaced by a subclass" \
  || bad "a concrete dependency can be replaced by a subclass"

echo "== LIB_REGISTRAR wires the graph from the app's own code =="
start_host reg "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' '{}' \
  CsLib.Registration.AddCsLib
wait_healthy reg \
  && ok "registrar supplies dependencies with no LIB_SERVICES" \
  || bad "registrar supplies dependencies with no LIB_SERVICES"

api reg /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"real"' \
  && ok "registrar's registration is used" || bad "registrar's registration is used"

# The combination that matters: real wiring, one thing faked.
start_host regfake "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' \
  '{"CsLib.IStamp":"CsLib.FakeStamp"}' CsLib.Registration.AddCsLib
wait_healthy regfake >/dev/null 2>&1
api regfake /invoke -X POST -H 'Content-Type: application/json' \
  -d '{"method":"Stamp","args":[]}' | grep -q '"result":"fake"' \
  && ok "LIB_SERVICES overrides the registrar" || bad "LIB_SERVICES overrides the registrar"

echo "== an unregistered dependency still fails fast =="
start_host stampmissing "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp"}' '{}' 
if wait_healthy stampmissing; then
  bad "unregistered dependency exits non-zero"
else
  ok "unregistered dependency exits non-zero"
fi
```

`start_host` now takes a sixth argument. Update its definition to accept and pass `LIB_SERVICES`:

```sh
start_host() { # start_host <alias> <pluginDir> <assembly> <type> <optionsJson> [servicesJson]
  docker run -d --rm --name "$1-${NET}" --network "${NET}" --network-alias "$1" \
    -v "$2:/plugin:ro" \
    -e LIB_DIR=/plugin -e LIB_ASSEMBLY="$3" -e LIB_TYPE="$4" -e LIB_OPTIONS="$5" \
    -e LIB_SERVICES="${6:-\{\}}" \
    "$IMAGE" >/dev/null 2>&1
}
```

- [ ] **Step 2: Run to verify it fails**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker run --rm -v "$PWD:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o /w/test/publish/cslib
./test/run.sh
```
Expected: FAIL — every host now fails to construct `Store`, because `IStamp` is an unsupported constructor parameter.

- [ ] **Step 3: Add the DI packages**

In `src/RemoteClassHost/RemoteClassHost.csproj`, add inside the existing `ItemGroup`:

```xml
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging" Version="10.0.0" />
```

These widen the set of packages a plugin must agree with on version — the known cost of loading into the shared default context. Keep the list to these four.

- [ ] **Step 4: Rewrite `Activation`**

Replace the body of `src/RemoteClassHost/Activation.cs` with:

```csharp
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RemoteClassHost;

public static class Activation
{
    /// <summary>
    /// Constructs the type using a real service container, so dependencies may
    /// nest arbitrarily.
    ///
    /// IOptions&lt;T&gt; is bound from JSON and ILogger&lt;T&gt; is supplied
    /// automatically, as before. Everything else must be named in
    /// <paramref name="servicesJson"/>, an interface-to-implementation map whose
    /// types are resolved out of the PLUGIN assembly.
    ///
    /// This is also how a dependency is faked. A mock created in the test process
    /// cannot be injected into an instance living in this container — that would
    /// need a call back into the test process. A fake compiled into the plugin and
    /// named here is the supported substitute.
    /// </summary>
    public static object Create(
        Type type, string optionsJson, string servicesJson, string? registrar)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The application's own registration method, if named. Runs FIRST so the
        // explicit map below can override any part of it — that ordering is what
        // makes "real wiring, one thing faked" expressible.
        InvokeRegistrar(services, registrar);

        // Registrations from config, resolved out of the plugin assembly.
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                      string.IsNullOrWhiteSpace(servicesJson) ? "{}" : servicesJson)
                  ?? [];

        foreach (var (serviceName, implName) in map)
        {
            var serviceType = PluginLoader.Assembly?.GetType(serviceName)
                ?? throw new InvalidOperationException(
                    $"LIB_SERVICES names service type '{serviceName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            var implType = PluginLoader.Assembly?.GetType(implName)
                ?? throw new InvalidOperationException(
                    $"LIB_SERVICES names implementation '{implName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            // Replace, not Add: these are overrides on top of whatever the
            // registrar already wired, and Replace says so unambiguously.
            services.Replace(ServiceDescriptor.Singleton(serviceType, implType));
        }

        // IOptions<T> for whatever closed generics this constructor asks for.
        var ctor = type.GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{type.FullName} has no public constructor");

        foreach (var p in ctor.GetParameters())
        {
            var pt = p.ParameterType;

            if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(IOptions<>))
            {
                continue;
            }

            var inner = pt.GetGenericArguments()[0];
            var value = JsonSerializer.Deserialize(optionsJson, inner,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var wrapped = typeof(Options).GetMethod("Create")!
                .MakeGenericMethod(inner).Invoke(null, [value])!;

            services.AddSingleton(pt, wrapped);
        }

        // Auto-register concrete dependencies as themselves, recursively.
        // ActivatorUtilities resolves every parameter from the container and
        // throws for anything unregistered — it does NOT construct concrete
        // types on its own. Without this, a perfectly ordinary
        // GitManager(GitConfigManager) would fail at startup and force the
        // caller to self-register every concrete dependency by hand.
        //
        // Anything already named in LIB_SERVICES is left alone, so substituting
        // a fake stays a one-line change:
        //   {"MyApp.GitConfigManager":"MyApp.Testing.FakeGitConfigManager"}
        // (the fake must derive from it — a sealed class with non-virtual
        // members cannot be substituted by any mechanism, here or in-process).
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>([type]);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            if (!seen.Add(current))
            {
                continue;
            }

            var currentCtor = current.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (currentCtor is null)
            {
                continue;
            }

            foreach (var p in currentCtor.GetParameters())
            {
                var pt = p.ParameterType;

                var alreadyHandled =
                    map.ContainsKey(pt.FullName ?? string.Empty)
                    || pt.IsInterface
                    || pt.IsAbstract
                    || pt.IsPrimitive
                    || pt == typeof(string)
                    || (pt.IsGenericType
                        && (pt.GetGenericTypeDefinition() == typeof(IOptions<>)
                            || pt.GetGenericTypeDefinition() == typeof(ILogger<>)));

                if (alreadyHandled)
                {
                    continue;
                }

                services.AddSingleton(pt, pt);
                pending.Enqueue(pt);
            }
        }

        var provider = services.BuildServiceProvider();

        try
        {
            return ActivatorUtilities.CreateInstance(provider, type);
        }
        catch (InvalidOperationException ex)
        {
            // ActivatorUtilities' own message names the unresolvable parameter,
            // but not what to do about it.
            throw new InvalidOperationException(
                $"cannot construct {type.FullName}: {ex.Message}. " +
                "Register the missing dependency in LIB_SERVICES as " +
                "{\"Full.IService\":\"Full.Implementation\"}, or point " +
                "LIB_REGISTRAR at your own registration method.", ex);
        }
    }

    /// <summary>
    /// Invokes a static registration method named as
    /// "Namespace.TypeName.MethodName", passing the service collection.
    ///
    /// Extension methods work unchanged: an extension is a static method on a
    /// static class, and reflection sees it that way.
    /// </summary>
    private static void InvokeRegistrar(IServiceCollection services, string? registrar)
    {
        if (string.IsNullOrWhiteSpace(registrar))
        {
            return;
        }

        var lastDot = registrar.LastIndexOf('.');

        if (lastDot <= 0)
        {
            throw new InvalidOperationException(
                $"LIB_REGISTRAR '{registrar}' must be 'Namespace.TypeName.MethodName'.");
        }

        var typeName = registrar[..lastDot];
        var methodName = registrar[(lastDot + 1)..];

        var declaring = PluginLoader.Assembly?.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"LIB_REGISTRAR names type '{typeName}', which is not in the assembly. " +
                $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

        var method = declaring.GetMethods()
            .FirstOrDefault(m => m.Name == methodName
                                 && m.IsStatic
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType
                                        .IsAssignableFrom(typeof(IServiceCollection)))
            ?? throw new InvalidOperationException(
                $"LIB_REGISTRAR: '{typeName}' has no static method '{methodName}' " +
                "taking a single IServiceCollection parameter.");

        method.Invoke(null, [services]);
    }
}
```

- [ ] **Step 5: Pass `LIB_SERVICES` through**

In `src/RemoteClassHost/Program.cs`, add beside the other env reads:

```csharp
var servicesJson = Environment.GetEnvironmentVariable("LIB_SERVICES") ?? "{}";
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");
```

and change the construction call to pass it. If Task 2 has already introduced `InstanceHolder`, the factory becomes:

```csharp
var holder = new InstanceHolder(() => Activation.Create(type, optsJson, servicesJson, registrar));
```

- [ ] **Step 6: Rebuild and run**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker build -q -t remote-class-host:dev . && ./test/run.sh
```
Expected: PASS — every earlier case plus the four new ones.

- [ ] **Step 7: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add src test
git commit -m "Construct through a service container so dependencies can nest

LIB_SERVICES maps interfaces to implementations from the plugin assembly. Also
the supported way to substitute a fake: a mock in the test process cannot be
injected into an instance living in a container."
```

---

### Task 6: README and CI

**Files:**
- Create: `README.md`
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Consumes: `test/run.sh` from Task 5.
- Produces: a CI job named `test`.

- [ ] **Step 1: Write `README.md`**

Verify every claim against the built image rather than this plan. Cover: what the image is, and that it exists so tests can drive **several real instances** of a library against shared state; the env-var table (`LIB_DIR`, `LIB_ASSEMBLY`, `LIB_TYPE`, `LIB_OPTIONS`, `LIB_PORT`, and the optional `SMB_*` set) with defaults read from the code; the three endpoints with real request/response bodies captured from a running container; that mounting is optional and only then requires `CAP_SYS_ADMIN`; the constructor shapes supported (`IOptions<T>`, `ILogger<T>`) and the exact error when one isn't; that host and plugin must agree on shared package versions, because the plugin loads into the default `AssemblyLoadContext`; and the boundary — no `ref`/`out`, no open generics, no streams, arguments must be JSON-serializable.

Include a prominent security note: **`/invoke` executes arbitrary methods on a loaded assembly. Test networks only.**

Include the VB note, since it costs a confusing startup failure: VB prepends `RootNamespace` to declared namespaces, so `LIB_TYPE` for a VB library is often not what a C# developer expects — and `GET /types` lists what the assembly actually contains.

And a consumer example:

```csharp
var store = RemoteClass.For<IDocumentStore>("http://instance-a:8080");
await store.WriteAsync("doc.json", payload);   // runs in the container
```

- [ ] **Step 2: Write `.github/workflows/ci.yml`**

```yaml
name: ci

on:
  push:
    branches: [main]
  pull_request:

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      # Needed only by the two-instance case, which mounts a real share.
      - name: Ensure kernel cifs support
        run: |
          sudo modprobe cifs \
            || (sudo apt-get update \
                && sudo apt-get install -y "linux-modules-extra-$(uname -r)" \
                && sudo modprobe cifs)
          grep -qw cifs /proc/filesystems && echo "cifs OK"

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Publish test fixtures
        run: |
          dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
          dotnet publish test/fixtures/VbLib/VbLib.vbproj -c Release -o test/publish/vblib

      - name: Build image
        run: docker build -t remote-class-host:dev .

      - name: Self-tests
        run: ./test/run.sh remote-class-host:dev
```

- [ ] **Step 3: Validate the workflow YAML**

Run: `cd /Users/drew/repos/remote-class-host && python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml')); print('ci.yml OK')"`
Expected: `ci.yml OK`

- [ ] **Step 4: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add README.md .github/workflows/ci.yml
git commit -m "Add README and CI workflow"
```

---

### Task 7: Release workflow

**Files:**
- Create: `.github/workflows/release.yml`

**SCOPE LIMIT: do not create the GitHub repo, push, tag, or publish.** Publishing is the owner's decision and is irreversible. This task ends at a local commit.

**Interfaces:**
- Consumes: the CI job from Task 6.
- Produces: a workflow publishing both the image and the NuGet package from one tag.

- [ ] **Step 1: Write `.github/workflows/release.yml`**

```yaml
name: release

on:
  push:
    tags: ["v*"]

jobs:
  publish:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      # Gate the publish on the suite passing for THIS commit. Without it a tag
      # can publish bytes no test ever validated.
      - name: Ensure kernel cifs support
        run: |
          sudo modprobe cifs \
            || (sudo apt-get update \
                && sudo apt-get install -y "linux-modules-extra-$(uname -r)" \
                && sudo modprobe cifs)
          grep -qw cifs /proc/filesystems && echo "cifs OK"

      - name: Publish test fixtures
        run: |
          dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib
          dotnet publish test/fixtures/VbLib/VbLib.vbproj -c Release -o test/publish/vblib

      - name: Build image for self-tests (amd64)
        run: docker build -t remote-class-host:citest .

      - name: Self-tests
        run: ./test/run.sh remote-class-host:citest

      - uses: docker/setup-qemu-action@v3
      - uses: docker/setup-buildx-action@v3

      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - id: meta
        uses: docker/metadata-action@v5
        with:
          images: ghcr.io/drewlane-dev/remote-class-host
          tags: |
            type=semver,pattern={{version}}
            type=semver,pattern={{major}}.{{minor}}
            type=semver,pattern={{major}}
            type=raw,value=latest,enable=${{ !contains(github.ref, '-') }}

      # The self-tests above ran on amd64 only; arm64 correctness rests on this
      # cross-build alone and is exercised by no test.
      - uses: docker/build-push-action@v6
        with:
          context: .
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ steps.meta.outputs.tags }}
          labels: ${{ steps.meta.outputs.labels }}

      # The client is the other half of the same wire protocol, so it carries the
      # same version and ships from the same tag. Drift between them would be a
      # protocol mismatch nobody could diagnose from either side alone.
      - name: Pack and push the client
        run: |
          VERSION="${GITHUB_REF_NAME#v}"
          dotnet pack src/RemoteClass.Client/RemoteClass.Client.csproj \
            -c Release -p:PackageVersion="$VERSION" -o ./nupkg
          dotnet nuget push "./nupkg/*.nupkg" \
            --source "https://nuget.pkg.github.com/drewlane-dev/index.json" \
            --api-key "${{ secrets.GITHUB_TOKEN }}" --skip-duplicate
```

- [ ] **Step 2: Validate the workflow YAML**

Run: `cd /Users/drew/repos/remote-class-host && python3 -c "import yaml; yaml.safe_load(open('.github/workflows/release.yml')); print('release.yml OK')"`
Expected: `release.yml OK`

- [ ] **Step 3: Verify the multi-arch build locally**

Run: `cd /Users/drew/repos/remote-class-host && docker buildx build --platform linux/amd64,linux/arm64 -t remote-class-host:multiarch .`
Expected: both platforms build. No `--push`, so nothing leaves the machine. The amd64 half is emulated on Apple Silicon and may take several minutes.

- [ ] **Step 4: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add .github/workflows/release.yml
git commit -m "Add release workflow publishing image and client from one tag"
```

- [ ] **Step 5: STOP — report and wait**

Do not create the repo, push, or tag. Report that the plan is complete and the publish decision is the owner's.

---

### Task 8: Callback proxies — a real mock in the test process

Adds the second substitution mechanism. `LIB_SERVICES` gives a fake compiled into the plugin; `LIB_CALLBACKS` forwards an interface's calls back to the test process so an ordinary Moq mock serves them. Both coexist; naming an interface in both is a startup error.

**Files:**
- Create: `src/RemoteClassHost/CallbackProxy.cs`
- Create: `src/RemoteClass.Client/CallbackHost.cs`
- Modify: `src/RemoteClassHost/Activation.cs`
- Modify: `src/RemoteClassHost/Program.cs`
- Modify: `src/RemoteClass.Client/RemoteClass.Client.csproj`
- Create: `test/fixtures/CallbackClient/CallbackClient.csproj`, `.../Program.cs`
- Modify: `test/run.sh`

**Interfaces:**
- Consumes: `Activation.Create(Type, string optionsJson, string servicesJson, string? registrar)` from Task 5; `PluginLoader.Assembly`; `Invoker.InvokeAsync` from Task 2 (as the shape to mirror).
- Produces: `Activation.Create(..., string callbacksJson)` — a fifth parameter; `CallbackHost.Start(int port)` → `IAsyncDisposable` with a `Serve<T>(T target)` method.

- [ ] **Step 1: Write the failing test**

Create `test/fixtures/CallbackClient/CallbackClient.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>disable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="../CsLib/CsLib.csproj" />
    <ProjectReference Include="../../../src/RemoteClass.Client/RemoteClass.Client.csproj" />
    <PackageReference Include="Moq" Version="4.20.72" />
  </ItemGroup>
</Project>
```

Create `test/fixtures/CallbackClient/Program.cs`:

```csharp
using CsLib;
using Moq;
using RemoteClass;

// A REAL Moq mock, in the test process, serving an instance in a container.
var mock = new Mock<IStamp>();
mock.Setup(s => s.Value()).Returns("from-moq");

await using var callbacks = CallbackHost.Start(9090);
callbacks.Serve<IStamp>(mock.Object);

var store = RemoteClass.RemoteClass.For<IStore>("http://cb:8080");

Console.WriteLine("RESULT: stamp " + store.Stamp());

// Verify works, because the calls really reached this mock.
mock.Verify(s => s.Value(), Times.Once);
Console.WriteLine("RESULT: verify ok");
```

Append to `test/run.sh` before the summary `echo`. Note the runner container needs a network alias so the host can reach it:

```sh
echo "== callback proxies: a Moq mock serving a remote instance =="
start_host cb "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp/cb-data"}' '{}' '' \
  '{"CsLib.IStamp":"http://testrunner:9090"}'
wait_healthy cb && ok "host starts with a callback-backed dependency" \
              || bad "host starts with a callback-backed dependency"

CB_OUT=$(docker run --rm --network "${NET}" --network-alias testrunner \
  -v "${HERE}/..:/w" -w /w mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet run --project test/fixtures/CallbackClient/CallbackClient.csproj -c Release 2>&1 \
  | grep '^RESULT:') || CB_OUT=""

echo "$CB_OUT" | grep -q "RESULT: stamp from-moq" \
  && ok "the mock's configured value reached the remote instance" \
  || bad "the mock's configured value reached the remote instance"
echo "$CB_OUT" | grep -q "RESULT: verify ok" \
  && ok "Moq Verify sees the call the container made" \
  || bad "Moq Verify sees the call the container made"

echo "== naming an interface in both mechanisms is a startup error =="
start_host cbdup "${HERE}/publish/cslib" CsLib.dll CsLib.Store '{"RootPath":"/tmp/d"}' \
  '{"CsLib.IStamp":"CsLib.FakeStamp"}' '' '{"CsLib.IStamp":"http://testrunner:9090"}'
if wait_healthy cbdup; then
  bad "an interface in both LIB_SERVICES and LIB_CALLBACKS exits non-zero"
else
  ok "an interface in both LIB_SERVICES and LIB_CALLBACKS exits non-zero"
fi
```

`start_host` gains an eighth argument. Update it to pass `-e LIB_CALLBACKS="${8:-\{\}}"`.

- [ ] **Step 2: Run to verify it fails**

Run: `./test/run.sh`
Expected: FAIL — `LIB_CALLBACKS` is ignored, so `IStamp` is unregistered and the `cb` host fails to construct `Store`.

- [ ] **Step 3: Write `CallbackProxy` (host side)**

Create `src/RemoteClassHost/CallbackProxy.cs`:

```csharp
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RemoteClassHost;

/// <summary>
/// Stands in for a dependency whose real implementation is a mock living in the
/// test process. Every call is forwarded there over HTTP.
///
/// This is the client's proxy pointing outward instead of inward — same wire
/// shape, same four return types.
/// </summary>
public class CallbackProxy : DispatchProxy
{
    private HttpClient _http = null!;
    private string _interfaceName = null!;

    public static object Create(Type interfaceType, string baseUrl)
    {
        var proxy = typeof(DispatchProxy)
            .GetMethod(nameof(DispatchProxy.Create))!
            .MakeGenericMethod(interfaceType, typeof(CallbackProxy))
            .Invoke(null, null)!;

        var self = (CallbackProxy)proxy;
        self._http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        self._interfaceName = interfaceType.FullName!;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);

        var body = JsonSerializer.Serialize(new
        {
            @interface = _interfaceName,
            method = method.Name,
            args = args ?? [],
        });

        HttpResponseMessage response;
        try
        {
            response = _http.PostAsync("/callback",
                new StringContent(body, Encoding.UTF8, "application/json"))
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            // The commonest cause is the test process having moved on, or never
            // being reachable at the configured address. Say which, because a
            // bare connection error here is otherwise very hard to place.
            throw new InvalidOperationException(
                $"callback to {_http.BaseAddress} for {_interfaceName}.{method.Name} failed. " +
                "Is the test process still running and reachable on the Docker network " +
                $"at that address? ({ex.Message})", ex);
        }

        var json = JsonDocument.Parse(
            response.Content.ReadAsStringAsync().GetAwaiter().GetResult());

        if (!json.RootElement.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        }

        var resultEl = json.RootElement.GetProperty("result");
        var rt = method.ReturnType;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;

        if (!rt.IsGenericType || rt.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(rt, opts);
        }

        var inner = rt.GetGenericArguments()[0];
        var value = resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(inner, opts);

        return typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(inner).Invoke(null, [value]);
    }
}
```

- [ ] **Step 4: Register callbacks in `Activation`**

Change the signature to take a fifth parameter and register the proxies. Add this immediately after the `LIB_SERVICES` map registration loop:

```csharp
        var callbacks = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            string.IsNullOrWhiteSpace(callbacksJson) ? "{}" : callbacksJson)
                        ?? [];

        foreach (var (interfaceName, url) in callbacks)
        {
            // Ambiguous configuration fails loudly rather than resolving to
            // whichever mechanism happened to be checked first.
            if (map.ContainsKey(interfaceName))
            {
                throw new InvalidOperationException(
                    $"'{interfaceName}' is named in BOTH LIB_SERVICES and LIB_CALLBACKS. " +
                    "Use one or the other.");
            }

            var interfaceType = PluginLoader.Assembly?.GetType(interfaceName)
                ?? throw new InvalidOperationException(
                    $"LIB_CALLBACKS names '{interfaceName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            services.Replace(ServiceDescriptor.Singleton(
                interfaceType, CallbackProxy.Create(interfaceType, url)));
        }
```

and change the method signature to:

```csharp
    public static object Create(
        Type type, string optionsJson, string servicesJson, string? registrar, string callbacksJson)
```

- [ ] **Step 5: Pass `LIB_CALLBACKS` through**

In `src/RemoteClassHost/Program.cs`, add beside the other env reads:

```csharp
var callbacksJson = Environment.GetEnvironmentVariable("LIB_CALLBACKS") ?? "{}";
```

and pass it as the fifth argument to `Activation.Create` inside the `InstanceHolder` factory.

- [ ] **Step 6: Write `CallbackHost` (client side)**

Create `src/RemoteClass.Client/CallbackHost.cs`:

```csharp
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace RemoteClass;

/// <summary>
/// Serves mocks living in the test process to instances running in containers.
///
/// Dispose it with the FIXTURE, not with a test. A container can call back after
/// a test method returns, and a disposed listener turns that into a confusing
/// connection error attributed to whatever runs next.
///
/// One mock per instance is the supported pattern. Sharing one across several
/// instances works, but calls interleave and Verify counts across all of them.
/// </summary>
public sealed class CallbackHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, object> _targets = new();

    private CallbackHost(WebApplication app) => _app = app;

    public static CallbackHost Start(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();
        var host = new CallbackHost(app);

        app.MapPost("/callback", async (HttpRequest request) =>
        {
            using var doc = await JsonDocument.ParseAsync(request.Body);
            var root = doc.RootElement;
            var interfaceName = root.GetProperty("interface").GetString()!;
            var methodName = root.GetProperty("method").GetString()!;
            var argEls = root.GetProperty("args").EnumerateArray().ToArray();

            if (!host._targets.TryGetValue(interfaceName, out var target))
            {
                return Results.Ok(new
                {
                    ok = false,
                    error = $"no mock registered for '{interfaceName}'. " +
                            "Was CallbackHost disposed before the container finished calling it?",
                });
            }

            var method = target.GetType().GetMethods()
                .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argEls.Length);

            if (method is null)
            {
                return Results.Ok(new { ok = false, error = $"no method '{methodName}' on {interfaceName}" });
            }

            var ps = method.GetParameters();
            var callArgs = new object?[ps.Length];
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            try
            {
                for (var i = 0; i < ps.Length; i++)
                {
                    callArgs[i] = argEls[i].Deserialize(ps[i].ParameterType, opts);
                }

                var result = method.Invoke(target, callArgs);

                if (result is Task task)
                {
                    await task;

                    return method.ReturnType.IsGenericType
                        ? Results.Ok(new { ok = true, result = method.ReturnType.GetProperty("Result")!.GetValue(task) })
                        : Results.Ok(new { ok = true, result = (object?)null });
                }

                return Results.Ok(new { ok = true, result });
            }
            catch (Exception ex)
            {
                // Unwrap so the original call site sees the mock's own message
                // after two hops, not reflection's wrapper.
                var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                return Results.Ok(new { ok = false, error = real.Message });
            }
        });

        app.Start();
        return host;
    }

    /// <summary>Registers a mock to serve calls for <typeparamref name="T"/>.</summary>
    public void Serve<T>(T target) where T : notnull =>
        _targets[typeof(T).FullName!] = target;

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
```

Add to `src/RemoteClass.Client/RemoteClass.Client.csproj`, inside a new `ItemGroup`:

```xml
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
```

This is the one dependency the client package takes on. It is a framework reference rather than a package, and it is required because serving callbacks means hosting HTTP. The package remains free of any test-framework or container dependency.

- [ ] **Step 7: Publish fixtures, rebuild and run**

Run:
```bash
cd /Users/drew/repos/remote-class-host
docker build -q -t remote-class-host:dev . && ./test/run.sh
```
Expected: PASS — every earlier case plus the four new ones.

- [ ] **Step 8: Prove the mock is really being consulted**

Change the mock's `Returns("from-moq")` to a different string, re-run only the callback case, and confirm the assertion fails on the new value rather than passing on a stale one. Restore afterwards. A callback test that passes because a fake happened to return the right string would be worthless.

- [ ] **Step 9: Commit**

```bash
cd /Users/drew/repos/remote-class-host
git add src/RemoteClassHost/CallbackProxy.cs src/RemoteClassHost/Activation.cs \
        src/RemoteClassHost/Program.cs src/RemoteClass.Client/CallbackHost.cs \
        src/RemoteClass.Client/RemoteClass.Client.csproj \
        test/fixtures/CallbackClient test/run.sh
git commit -m "Add callback proxies so a real mock can serve a remote instance

LIB_CALLBACKS forwards an interface's calls back to the test process, where an
ordinary Moq mock answers them. Complements LIB_SERVICES, which substitutes a
fake compiled into the plugin; naming an interface in both is a startup error."
```

---

## Self-Review

**Spec coverage.** Purpose and multi-instance rationale → Task 4. Default `AssemblyLoadContext` → Task 1 Step 4, with the reasoning in the doc comment and in Global Constraints. Construction from `IOptions<T>`/`ILogger<T>` → Task 1 Step 5. `/health`, `/types`, `/invoke` → Tasks 1 Step 6 and 2 Step 4. Optional mount → Task 4 Step 3. Client package, protocol-only → Task 2 Step 5. Four return shapes → Task 2 Steps 1 and 5. Config table → Task 5 Step 1 (README) and read from code throughout. Release of both artifacts from one tag → Task 6. Spec test cases 1-3 → Task 2; case 4 (VB) → Task 3; case 5 (missing type + `/types`) → Task 1; case 7 (two instances, server-verified) → Task 4.

**Spec case 6 — "a constructor parameter that cannot be satisfied fails fast, naming the type" — has no test.** `Activation.Create` throws with the parameter name and type, and Task 1's "unknown `LIB_TYPE` exits non-zero" case already proves the container exits rather than serving on a construction failure. A second fixture library existing only to have an unsatisfiable constructor is more maintenance than the coverage is worth. Recorded as a deliberate omission rather than dropped silently.

**`IRemoteClassModule` escape hatch is NOT implemented.** The spec describes it for libraries needing real wiring. No task builds it, deliberately: nothing in the test fixtures or the known consumer needs it, and building an extension point before it has a user is how it ends up the wrong shape. The spec's own risk section already flags that if most consumers need it, the reflection default is wrong — which is the signal to build it then. This is a scope decision to confirm, not an oversight.

**Placeholder scan.** No TBD/TODO. Every code step carries literal content. Task 5 Step 1 describes README content rather than dictating prose — deliberate, since it instructs verification against the built image.

**Type consistency.** `PluginLoader.Load(string, string, string)` → `Type` defined Task 1 Step 4, called Task 1 Step 6. `PluginLoader.TypeNames()` defined Task 1 Step 4, used Task 1 Steps 4 and 6. `Activation.Create(Type, string)` defined Task 1 Step 5, called Task 1 Step 6. `InvokeRequest(string Method, JsonElement[] Args)` defined Task 2 Step 3, bound Task 2 Step 4, produced by the client in Task 2 Step 5. `Invoker.InvokeAsync(object, Type, InvokeRequest)` defined Task 2 Step 3, called Task 2 Step 4. `RemoteClass.For<T>(string)` defined Task 2 Step 5, used in Tasks 2, 3 and 4 fixtures. `ShareMounter.MountIfConfigured()` defined Task 4 Step 3, called Task 4 Step 4.

**Naming wrinkle carried into execution.** The client class is `RemoteClass.RemoteClass` — namespace and type share a name, so fixtures must write `RemoteClass.RemoteClass.For<T>(...)` or add an alias. Ugly but valid. If the implementer prefers, renaming the type to `RemoteProxy` inside namespace `RemoteClass` is an improvement; the plan uses the doubled form consistently so either choice stays coherent.
