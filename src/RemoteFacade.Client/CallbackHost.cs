using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

namespace RemoteFacadeHost.Client;

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

    // The served INTERFACE is stored alongside the target, not just used as a
    // key: dispatch resolves methods against it, so nothing outside the
    // interface is reachable. See the lookup in /callback below.
    private readonly ConcurrentDictionary<string, (Type Interface, object Target)> _targets = new();

    private CallbackHost(WebApplication app) => _app = app;

    public static CallbackHost Start(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        var app = builder.Build();
        var host = new CallbackHost(app);

        app.MapPost("/callback", async (HttpRequest request) =>
        {
            // Everything from parsing the request body through awaiting the
            // result lives in ONE try: the GetProperty calls throw on a
            // malformed or unexpected request shape just as readily as
            // method.Invoke throws on a bad call, and both need to come back
            // as a structured {ok:false} error instead of an unhandled 500 --
            // the same reasoning Invoker.InvokeAsync documents for the
            // forward direction.
            try
            {
                using var doc = await JsonDocument.ParseAsync(request.Body);
                var root = doc.RootElement;
                var interfaceName = root.GetProperty("interface").GetString()!;
                var methodName = root.GetProperty("method").GetString()!;
                var argEls = root.GetProperty("args").EnumerateArray().ToArray();

                if (!host._targets.TryGetValue(interfaceName, out var served))
                {
                    return Results.Ok(new
                    {
                        ok = false,
                        error = $"no mock registered for '{interfaceName}'. " +
                                "Was CallbackHost disposed before the container finished calling it?",
                    });
                }

                // Resolved against the SERVED INTERFACE's method table, never
                // against target.GetType(). This listener binds 0.0.0.0, has
                // no authentication, and runs inside the developer's own test
                // process -- dispatching on the target's concrete type made
                // every public method of the mock or fake reachable, not just
                // the ones the interface declares. A non-interface method
                // (Secret()) and Object's own ToString() were both verified
                // callable that way. Base interfaces are included because a
                // member inherited from one IS part of the served contract;
                // Object's members are not on any interface, so they drop out.
                var method = served.Interface.GetMethods()
                    .Concat(served.Interface.GetInterfaces().SelectMany(i => i.GetMethods()))
                    .FirstOrDefault(m => m.Name == methodName && m.GetParameters().Length == argEls.Length);

                if (method is null)
                {
                    return Results.Ok(new { ok = false, error = $"no method '{methodName}' on {interfaceName}" });
                }

                var ps = method.GetParameters();
                var callArgs = new object?[ps.Length];
                var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                for (var i = 0; i < ps.Length; i++)
                {
                    callArgs[i] = argEls[i].Deserialize(ps[i].ParameterType, opts);
                }

                // Invoked through the interface's MethodInfo: the CLR does the
                // interface dispatch onto the target's own implementation.
                var result = method.Invoke(served.Target, callArgs);

                // The SAME normalization Invoker.InvokeAsync performs in the
                // forward direction, and for the same reason. ValueTask and
                // ValueTask<T> are STRUCTS, so `result is Task` is false for
                // both: without this, a mock whose interface method returns
                // ValueTask<T> had the AWAITABLE ITSELF serialized as data
                // ({"ok":true,"result":{"isCompleted":false,...}}), the
                // container's CallbackProxy deserialized that into a DEFAULT
                // ValueTask<T>, and the library received a null/zero value
                // with ok:true and no error at any of the three hops --
                // exactly the corruption C1 described, just pointing the other
                // way. Shipping a release where one direction awaits ValueTask
                // correctly and the other silently corrupts it would be the
                // cross-cutting inconsistency the whole-branch review existed
                // to find, knowingly created.
                //
                // `effectiveReturnType` tracks what `result` is NOW (Task or
                // Task<T>), because the branch below reads Result off that
                // type; for every non-ValueTask method it is
                // method.ReturnType unchanged, so no other shape is affected.
                var effectiveReturnType = method.ReturnType;

                if (result is not null &&
                    (effectiveReturnType == typeof(ValueTask) ||
                     (effectiveReturnType.IsGenericType &&
                      effectiveReturnType.GetGenericTypeDefinition() == typeof(ValueTask<>))))
                {
                    result = effectiveReturnType.GetMethod(nameof(ValueTask.AsTask), Type.EmptyTypes)!
                        .Invoke(result, null);

                    effectiveReturnType = effectiveReturnType.IsGenericType
                        ? typeof(Task<>).MakeGenericType(effectiveReturnType.GetGenericArguments()[0])
                        : typeof(Task);
                }

                if (result is Task task)
                {
                    await task;

                    return effectiveReturnType.IsGenericType
                        ? Results.Ok(new { ok = true, result = effectiveReturnType.GetProperty("Result")!.GetValue(task) })
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
    public void Serve<T>(T target) where T : class
    {
        // Serve(mock.Object), with no explicit type argument, infers T as the
        // mock's concrete proxy type (e.g. Castle.Proxies.IStampProxy), not
        // the interface -- registering under that name means every call from
        // the container misses and reports "no mock registered", pointing at
        // the wrong problem entirely. Catch it here, at registration time,
        // where the fix is obvious, instead of at first call.
        if (!typeof(T).IsInterface)
        {
            throw new ArgumentException(
                $"Serve<T> requires an interface type, but T was inferred as " +
                $"'{typeof(T).FullName}' -- almost certainly the mock's own concrete " +
                "proxy type from calling Serve(mock.Object) without a type argument. " +
                $"Specify the interface explicitly, e.g. Serve<IYourInterface>(mock.Object).");
        }

        _targets[typeof(T).FullName!] = (typeof(T), target);
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
