using System.Reflection;
using System.Text;
using System.Text.Json;

// The namespace deliberately does NOT begin with "RemoteFacade".
//
// A namespace segment wins name resolution over a type, so a type named
// RemoteFacade inside `namespace RemoteFacade.Client` is unreachable by its
// own short name -- callers get the namespace instead. That defect shipped
// once under the old name (namespace RemoteClass containing class RemoteClass)
// and was reintroduced during the rename to this one.
//
// It no longer bites consumers, because v3 made this class internal and
// RemoteHost is the only entry point, but the trap is still here for anyone
// renaming things: keep the namespace root and any public type name distinct.

namespace RemoteFacadeHost.Client;

/// <summary>
/// The proxy that turns interface calls into HTTP. Internal: v3 removed the
/// public For&lt;T&gt;(url) entry point along with single-class hosting, and
/// <see cref="RemoteHost"/> is now the only way in.
///
/// No code generation: your test project already references the library for its
/// interface, so DispatchProxy can implement that interface and forward every
/// call. The compiler enforces the contract, and nothing regenerates when the
/// interface changes.
///
/// An async method on the interface returns its Task (or ValueTask) to the
/// caller WITHOUT waiting for the HTTP round trip, so
/// <c>Task.WhenAll(a.X(), b.X())</c> against two instances really does overlap
/// them. That is the whole point of the image: several real instances driven
/// concurrently against shared state. A blocking proxy would quietly turn every
/// contention test into a sequential one that passes while proving nothing.
/// </summary>
internal class RemoteFacade : DispatchProxy
{
    // Bound once. MakeGenericMethod below needs the OPEN definitions, and
    // looking a private static up by name on every call would be wasteful.
    private static readonly MethodInfo TypedTaskMethod =
        typeof(RemoteFacade).GetMethod(nameof(TypedTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo TypedValueTaskMethod =
        typeof(RemoteFacade).GetMethod(nameof(TypedValueTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    private HttpClient _http = null!;
    private string _interfaceName = null!;
    private string _service = null!;

    /// <summary>
    /// A proxy that names the service on every call.
    ///
    /// The name, not a handle to a resolved instance: the host resolves afresh
    /// per call, which is what lets ResetAsync rebuild the graph without
    /// invalidating proxies, and what makes a Transient registration yield a
    /// new instance per call.
    /// </summary>
    internal static T ForService<T>(HttpClient http, string service)
    {
        var proxy = Create<T, RemoteFacade>()!;
        var self = (RemoteFacade)(object)proxy;
        self._http = http;
        self._interfaceName = typeof(T).FullName!;
        self._service = service;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);

        var rt = method.ReturnType;

        // The ASYNC shapes hand the in-flight operation straight back to the
        // caller. Nothing here blocks, so the caller's next statement runs
        // while this call is still on the wire -- which is what lets two
        // instances be driven at once. (Before this, every shape went through
        // .GetAwaiter().GetResult() right here, so a method's Task was only
        // ever returned ALREADY COMPLETE: measured, the first call returned
        // its Task at t=85ms and the second at t=166ms, i.e. Task.WhenAll over
        // two instances ran them strictly one after the other.)
        if (rt == typeof(Task)) return CallAsync(method, args, null);
        if (rt == typeof(ValueTask)) return new ValueTask(CallAsync(method, args, null));

        if (rt.IsGenericType)
        {
            var definition = rt.GetGenericTypeDefinition();

            if (definition == typeof(Task<>) || definition == typeof(ValueTask<>))
            {
                var inner = rt.GetGenericArguments()[0];
                var pending = CallAsync(method, args, inner);

                return (definition == typeof(Task<>) ? TypedTaskMethod : TypedValueTaskMethod)
                    .MakeGenericMethod(inner)
                    .Invoke(null, [pending]);
            }
        }

        // Everything else is synchronous from the caller's point of view: a
        // void method, or one returning a plain value. The caller expects the
        // value (or the throw) to have happened by the time the call returns,
        // so this one really must wait. There is no SynchronizationContext to
        // deadlock against in a test process.
        return CallAsync(method, args, rt == typeof(void) ? null : rt).GetAwaiter().GetResult();
    }

    /// <summary>
    /// One HTTP round trip. <paramref name="resultType"/> is the type to
    /// deserialize the envelope's <c>result</c> into, or null when the method
    /// yields no value (void / Task / ValueTask).
    /// </summary>
    private async Task<object?> CallAsync(MethodInfo method, object?[]? args, Type? resultType)
    {
        // Every call names its service: v3 removed the un-named form along with
        // single-class hosting, and the host now rejects a call without one.
        var body = JsonSerializer.Serialize(
            new { method = method.Name, args = args ?? [], service = _service });

        var response = await _http.PostAsync("/invoke",
            new StringContent(body, Encoding.UTF8, "application/json")).ConfigureAwait(false);

        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        // Status BEFORE parsing. A reachable-but-wrong endpoint answers with an
        // HTML error page, which JsonDocument.Parse rejects as "'<' is an
        // invalid start of a value", and an oversized argument body draws a
        // bare 413 -- neither naming the interface, the method, the URL or the
        // status, and both arriving as a JSON error that reads like a protocol
        // bug.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"call to {_http.BaseAddress} for {_interfaceName}.{method.Name} returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}, not a {{ok, result}} body. " +
                $"Body: {text}");
        }

        var json = JsonDocument.Parse(text);

        if (!json.RootElement.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        }

        if (resultType is null) return null;

        var resultEl = json.RootElement.GetProperty("result");
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        return resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(resultType, opts);
    }

    // DispatchProxy can only hand back `object?`, so the untyped Task<object?>
    // above has to become a Task<T> of the method's own declared type before
    // it can be returned as one. A null result becomes default(T) rather than
    // an InvalidCastException, matching what the previous Task.FromResult path
    // produced for a JSON null.
    private static async Task<T> TypedTask<T>(Task<object?> pending)
    {
        var value = await pending.ConfigureAwait(false);
        return value is null ? default! : (T)value;
    }

    private static ValueTask<T> TypedValueTask<T>(Task<object?> pending) => new(TypedTask<T>(pending));
}
