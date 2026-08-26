using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using RemoteFacadeHost;

var dir      = Environment.GetEnvironmentVariable("LIB_DIR") ?? "/plugin";
var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var typeName = Environment.GetEnvironmentVariable("LIB_TYPE");
var optsJson = Environment.GetEnvironmentVariable("LIB_OPTIONS") ?? "{}";
var servicesJson = Environment.GetEnvironmentVariable("LIB_SERVICES") ?? "{}";
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");
var callbacksJson = Environment.GetEnvironmentVariable("LIB_CALLBACKS") ?? "{}";
var port     = Environment.GetEnvironmentVariable("LIB_PORT") ?? "8080";

// One of the two must say what to serve. Without either, the container would
// start and answer nothing, and the first call would fail with something that
// does not name the actual mistake.
if (string.IsNullOrWhiteSpace(typeName) && string.IsNullOrWhiteSpace(registrar))
{
    throw new InvalidOperationException(
        "either LIB_TYPE (to host one class) or LIB_REGISTRAR (to host a " +
        "composition root) is required; neither was set.");
}

// Before constructing anything: the library may open paths on the share in its
// constructor.
ShareMounter.MountIfConfigured();

// Before the plugin is loaded, not after: a type initializer can P/Invoke on
// the very first touch, and the resolver has to be subscribed by then or that
// first load fails with nothing to catch it.
NativeResolver.Install(dir);

// Fail before serving. A host that starts without a usable instance would make
// every test using it fail confusingly at first call instead of at startup.
var type = string.IsNullOrWhiteSpace(typeName)
    ? null
    : PluginLoader.Load(dir, asmFile, typeName);

// In composition-root mode nothing names a type, so the assembly still has to
// be loaded for the registrar and for service-name lookup.
if (type is null) PluginLoader.LoadAssembly(dir, asmFile);

// One instance serves every call for the container's lifetime; that is what
// allows a method to acquire a resource and a later call to release it.
// InstanceHolder owns it so DELETE /instance can reset it between tests.
var holder = new InstanceHolder(() => Activation.Build(type, optsJson, servicesJson, registrar, callbacksJson));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

var app = builder.Build();

// The exact options minimal APIs serialize responses with, resolved once.
// Invoker pre-serializes the return value itself (so a value System.Text.Json
// can't handle is reported instead of reaching Results.Ok's own serialization
// as a raw CLR value) and MUST use these same options to do it, not a second,
// independently-constructed instance that could drift from this one.
var jsonOptions = app.Services.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;

app.MapGet("/health", () => Results.Ok(new { type = type?.FullName }));
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
        if (!string.IsNullOrWhiteSpace(request.Service))
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
                // with catch (Exception), CallbackHost does the same in the
                // reverse direction, and README's "Errors" section states
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
});

app.MapDelete("/instance", () =>
{
    holder.Reset();
    return Results.NoContent();
});

app.Run();
