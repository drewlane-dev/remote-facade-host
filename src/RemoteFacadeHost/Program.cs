using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using RemoteFacadeHost;

var dir      = Environment.GetEnvironmentVariable("LIB_DIR") ?? "/plugin";
var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var servicesJson = Environment.GetEnvironmentVariable("LIB_SERVICES") ?? "{}";
var interceptsJson = Environment.GetEnvironmentVariable("LIB_INTERCEPT") ?? "{}";
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");
var port     = Environment.GetEnvironmentVariable("LIB_PORT") ?? "8080";

// The startup is the only way to say what to serve. Without it the container
// would start and answer nothing, and the first call would fail with something
// that does not name the actual mistake.
if (string.IsNullOrWhiteSpace(registrar))
{
    throw new InvalidOperationException(
        "LIB_REGISTRAR is required: name the static registration method that " +
        "builds your service graph, as 'Namespace.TypeName.MethodName'.");
}

// v2 also accepted LIB_TYPE (host one class, constructed by this host) and
// LIB_OPTIONS (JSON bound onto that class's IOptions<T> parameters). Both were
// removed in v3. Failing here rather than ignoring them is the point: a config
// carried forward unchanged would otherwise start cleanly and serve a graph
// that silently did not include what the operator asked for.
// "{}" does not count as set. It was LIB_OPTIONS's own default in v2, so
// every harness and compose file in existence passes it as inert filler --
// refusing it would reject configurations that ask for nothing at all, which
// is the same over-strict guard this codebase already got wrong once and
// documented ("Only non-empty LIB_OPTIONS is fatal"). A value that actually
// carries configuration still fails, because that configuration would
// otherwise be silently dropped.
foreach (var gone in (string[])["LIB_TYPE", "LIB_OPTIONS"])
{
    var value = Environment.GetEnvironmentVariable(gone)?.Trim();

    if (!string.IsNullOrEmpty(value) && value != "{}")
    {
        throw new InvalidOperationException(
            $"{gone} was removed in v3 and is no longer read, but is set to " +
            $"'{value}'. Move this configuration into the startup named by " +
            "LIB_REGISTRAR, which registers services and configures their " +
            "options in ordinary C#.");
    }
}

// Before constructing anything: the library may open paths on the share in its
// constructor.
ShareMounter.MountIfConfigured();

// Before the plugin is loaded, not after: a type initializer can P/Invoke on
// the very first touch, and the resolver has to be subscribed by then or that
// first load fails with nothing to catch it.
NativeResolver.Install(dir);

// Fail before serving. A host that starts without a usable graph would make
// every test using it fail confusingly at first call instead of at startup.
PluginLoader.LoadAssembly(dir, asmFile);

// One graph serves every call for the container's lifetime; that is what
// allows a method to acquire a resource and a later call to release it.
// InstanceHolder owns it so DELETE /instance can reset it between tests.
var holder = new InstanceHolder(() => Activation.Build(registrar, servicesJson, interceptsJson));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// The exact options minimal APIs serialize responses with, resolved once.
// Invoker pre-serializes the return value itself (so a value System.Text.Json
// can't handle is reported instead of reaching Results.Ok's own serialization
// as a raw CLR value) and MUST use these same options to do it, not a second,
// independently-constructed instance that could drift from this one.
var jsonOptions = app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

app.MapGet("/health", () => Results.Ok(new { registrar }));
app.MapGet("/types", () => Results.Ok(PluginLoader.TypeNames()));

app.MapGet("/services", () => Results.Ok(holder.Use(graph => graph.ServiceNames)));

app.MapPost("/invoke", async (InvokeRequest request) =>
{
    // Everything this call needs the graph for happens INSIDE the lease:
    // resolving the service, dispatching against it, and awaiting whatever the
    // plugin returns. A reset landing mid-call retires this graph but cannot
    // dispose it until the lease is released, so the call finishes against the
    // graph it started on. Measured before the lease existed: two calls in
    // flight across one DELETE /instance both came back
    // {"ok":false,"error":"Cannot access a disposed object. Object name:
    // 'IServiceProvider'."}.
    //
    // The lease is deliberately NOT held over Results.Ok's own serialization
    // back out: Invoker pre-serializes the return value to a JsonElement, so
    // what leaves here is already-detached JSON that touches nothing in the
    // graph.
    return await holder.UseAsync<IResult>(async graph =>
    {
        if (string.IsNullOrWhiteSpace(request.Service))
        {
            return Results.Ok(new
            {
                ok = false,
                error = "every call must name the service it wants in the " +
                        "\"service\" field. v2's un-named calls went to the single " +
                        "LIB_TYPE instance, which no longer exists.",
            });
        }

        {
            (object Instance, Type Type) resolved;
            try
            {
                resolved = graph.Resolve(request.Service);
            }
            catch (InvalidOperationException ex)
            {
                // Resolve's own three misses (unknown type, not registered,
                // Scoped) are InvalidOperationException and already name the
                // service and list what IS registered, so they go out
                // verbatim -- prefixing them would say the same thing twice.
                return Results.Ok(new { ok = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                // Anything ELSE out of GetService is the plugin's own code
                // throwing, almost always a service CONSTRUCTOR -- which is
                // the single most likely place for a wiring mistake to fail,
                // and the whole reason wiring moved into C#. DI does NOT wrap
                // a constructor's exception, so an ArgumentException from one
                // propagates unwrapped, and with only the catch above it
                // escaped UseAsync entirely and reached Kestrel: measured as
                // HTTP 500 with a ZERO-byte body, the plugin's real message
                // ("wiring is wrong: no connection string") reaching the
                // container log and nowhere else.
                //
                // That contradicts the property the rest of this protocol
                // holds to -- Invoker guards everything after method lookup
                // with catch (Exception), and README's "Errors" section states
                // outright that neither ever reaches the caller as a bare 500
                // with an empty body. This path was the one exception.
                //
                // Attributed, because unlike Resolve's own messages this one
                // comes from the plugin and does not know where it came from:
                // on a multi-service host an unadorned message is
                // unattributable.
                return Results.Ok(new
                {
                    ok = false,
                    error = $"cannot resolve service '{request.Service}': " +
                            $"{ex.GetType().Name}: {ex.Message}",
                });
            }

            // Dispatch against the REGISTERED type Resolve() found, not
            // resolved.Instance.GetType(): Invoker matches methods by name via
            // Type.GetMethods(), and an interface implemented EXPLICITLY
            // compiles to a private, specially-named method that the concrete
            // type's own GetMethods() does not surface at all -- even with
            // non-public binding flags its Name would not equal the plain
            // method name a caller sends. Only GetMethods() on the type actually
            // NAMED by "service" (typically the interface) finds it by its plain
            // name, and invoking that MethodInfo against the concrete instance
            // still dispatches correctly through the interface. Measured
            // directly: CsLib.IExplicitThing.Go() returns
            // {"ok":false,"error":"no method 'Go' ..."} via the concrete type and
            // {"ok":true,"result":"explicit"} via this one.
            return Results.Ok(
                await Invoker.InvokeAsync(resolved.Instance, resolved.Type, request, jsonOptions));
        }
    });
});

app.MapDelete("/instance", () =>
{
    holder.Reset();
    return Results.NoContent();
});

app.Run();
