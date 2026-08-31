using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace RemoteFacadeHost;

/// <summary>
/// Every endpoint the host serves.
/// </summary>
/// <param name="jsonOptions">
/// The SAME options this pipeline will serialize the response with, so that
/// Invoker's pre-serialization of the return value cannot change its shape on
/// the wire.
///
/// Note the type: <c>Microsoft.AspNetCore.Mvc.JsonOptions</c>, NOT
/// <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>. They are different
/// objects configured independently, and while these handlers were minimal
/// APIs it was the latter that mattered. Resolving the wrong one would
/// reintroduce exactly the drift this parameter exists to prevent -- silently,
/// because both default to JsonSerializerDefaults.Web and would agree until
/// somebody configured one of them.
/// </param>
[ApiController]
[Route("/")]
public sealed class FacadeController(
    InstanceHolder holder,
    ServedPlugin plugin,
    IOptions<JsonOptions> jsonOptions) : ControllerBase
{
    [HttpGet("health")]
    public IActionResult Health() => Ok(new { plugin.Registrar });

    [HttpGet("types")]
    public IActionResult Types() => Ok(plugin.TypeNames());

    [HttpGet("services")]
    public IActionResult Services() => Ok(holder.Use(graph => graph.ServiceNames));

    [HttpDelete("instance")]
    public IActionResult Reset()
    {
        holder.Reset();
        return NoContent();
    }

    [HttpPost("invoke")]
    public async Task<IActionResult> Invoke([FromBody] InvokeRequest? request)
    {
        // A body that does not parse, is empty, or is literally "null" binds to
        // nothing. The pre-migration pipeline answered all three with 400 and an
        // empty body, and this restores that exactly.
        //
        // It has to be done by hand because the automatic 400 that would
        // otherwise cover it is switched off in FacadeWiring, for reasons that
        // apply to bodies which DO bind. Without this guard the null reaches
        // request.Service below and throws, and the caller gets a 500 with an
        // empty body -- the one outcome README promises never happens.
        if (request is null) return BadRequest();

        // Everything this call needs the graph for happens INSIDE the lease:
        // resolving the service, dispatching against it, and awaiting whatever
        // the plugin returns. A reset landing mid-call retires this graph but
        // cannot dispose it until the lease is released, so the call finishes
        // against the graph it started on. Measured before the lease existed:
        // two calls in flight across one DELETE /instance both came back
        // {"ok":false,"error":"Cannot access a disposed object. Object name:
        // 'IServiceProvider'."}.
        //
        // The lease is deliberately NOT held over the framework's own
        // serialization back out: Invoker pre-serializes the return value to a
        // JsonElement, so what leaves here is already-detached JSON that
        // touches nothing in the graph.
        return await holder.UseAsync<IActionResult>(async graph =>
        {
            if (string.IsNullOrWhiteSpace(request.Service))
            {
                return Ok(new
                {
                    ok = false,
                    error = "every call must name the service it wants in the " +
                            "\"service\" field. v2's un-named calls went to the single " +
                            "LIB_TYPE instance, which no longer exists.",
                });
            }

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
                return Ok(new { ok = false, error = ex.Message });
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
                return Ok(new
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
            return Ok(await Invoker.InvokeAsync(
                resolved.Instance, resolved.Type, request, jsonOptions.Value.JsonSerializerOptions));
        });
    }
}
