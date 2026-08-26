using System.Reflection;
using System.Text.Json;

namespace RemoteFacadeHost;

public static class Invoker
{
    /// <summary>
    /// Invokes a method by name and shapes the result for the wire. Handles
    /// Task, Task&lt;T&gt;, ValueTask and ValueTask&lt;T&gt; as well as plain
    /// synchronous returns — the host does not care whether the library is
    /// async, or which awaitable it chose, and neither should callers.
    /// </summary>
    /// <param name="responseOptions">
    /// The SAME <see cref="JsonSerializerOptions"/> Program.cs's minimal API
    /// pipeline actually serializes responses with (resolved once from
    /// <c>Microsoft.AspNetCore.Http.Json.JsonOptions</c>), not a private copy.
    /// Pre-serializing the return value here, ahead of Results.Ok, must not
    /// change its shape on the wire -- a second, independently-constructed
    /// options instance could silently drift from the app's real one.
    /// </param>
    public static async Task<object> InvokeAsync(
        object instance, Type type, InvokeRequest request, JsonSerializerOptions responseOptions)
    {
        var argCount = request.Args?.Length ?? 0;

        // Base-interface members are part of the served contract.
        // Type.GetMethods() returns inherited members for a CLASS but NOT for
        // an INTERFACE -- it returns only what that interface itself declares.
        // v1.0 always dispatched against the concrete class, so this never
        // mattered; both of v1.1's paths (a "service"-routed call, and
        // LIB_TYPE naming an interface) dispatch against the interface, so for
        // IDerived : IBase, FromBase() was unreachable while FromDerived()
        // worked. Measured: {"ok":false,"error":"no method 'FromBase' taking 0
        // argument(s)"} against a live container.
        //
        // This is the SAME ruling CallbackHost.cs makes in the reverse
        // direction ("a member inherited from one IS part of the served
        // contract"), reached the same way, deliberately: two files
        // implementing one protocol must not resolve methods differently.
        // GetInterfaces() on an interface returns every ancestor
        // transitively, so one level of concatenation covers arbitrary depth.
        var candidates = type.IsInterface
            ? type.GetMethods().Concat(type.GetInterfaces().SelectMany(i => i.GetMethods()))
            : type.GetMethods();

        var method = candidates
            .FirstOrDefault(m => m.Name == request.Method && m.GetParameters().Length == argCount);

        if (method is null)
        {
            return new { ok = false, error = $"no method '{request.Method}' taking {argCount} argument(s)" };
        }

        // Open generic methods (e.g. T Echo<T>(T value)) have no closed type
        // arguments here to bind to. Without this check, the FIRST thing to
        // fail is argument binding below -- request.Args![i].Deserialize(T,
        // ...) -- which throws System.InvalidOperationException ("The type
        // 'T' is invalid for serialization or deserialization because it is
        // a pointer type, is a ref struct, or contains generic parameters
        // that have not been replaced by specific types."), naming the
        // unbound generic parameter but not this method or why it's unbound.
        // Caught here, before any argument work, with a message that names
        // both. (Measured directly against IStore.Echo<T> -- it is
        // InvalidOperationException, not NotSupportedException, despite the
        // message reading like the latter.)
        if (method.ContainsGenericParameters)
        {
            return new
            {
                ok = false,
                error = $"method '{request.Method}' is an open generic method; " +
                        "/invoke has no way to supply type arguments, so it cannot be called"
            };
        }

        var ps = method.GetParameters();

        // ref/out/in parameters have no representation in a JSON request
        // body: a ref/out argument is a location for the callee to WRITE
        // to, not a value the caller can serialize going in, and even `in`
        // (a byref that's read-only on the callee's side) is still, at the
        // CLR level, a byref TYPE ("System.Int32&") that JsonSerializer
        // refuses outright. Without this check, argument binding below hits
        // that refusal first and throws System.InvalidOperationException
        // ("The type 'System.Int32&' is invalid for serialization or
        // deserialization because it is a pointer type, is a ref struct, or
        // contains generic parameters that have not been replaced by
        // specific types."), naming the byref TYPE but not this method, the
        // parameter, or which of ref/out/in it is. (Measured directly
        // against IStore.RefArg -- InvalidOperationException, same as the
        // open-generic case above.)
        var byRefParam = ps.FirstOrDefault(p => p.ParameterType.IsByRef);
        if (byRefParam is not null)
        {
            var kind = byRefParam.IsOut ? "out" : byRefParam.IsIn ? "in" : "ref";
            return new
            {
                ok = false,
                error = $"method '{request.Method}' has {kind} parameter '{byRefParam.Name}'; " +
                        "ref/out/in parameters cannot cross /invoke"
            };
        }

        var callArgs = new object?[ps.Length];

        // Everything from argument binding through awaiting the result lives in
        // ONE try: an async method's exception surfaces at `await task` as the
        // ORIGINAL exception type, never wrapped in TargetInvocationException,
        // so a catch scoped only around method.Invoke (or placed only around a
        // synchronous-looking call) lets it escape as an unhandled 500 — the
        // common case, since most library methods are async. A malformed
        // argument throws before the call even happens, and needs the same
        // treatment so the caller sees {ok:false} instead of a 500 with no
        // body the client can parse.
        try
        {
            for (var i = 0; i < ps.Length; i++)
            {
                try
                {
                    callArgs[i] = request.Args![i].Deserialize(ps[i].ParameterType,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                }
                catch (Exception ex)
                {
                    // Deliberately not narrowed to JsonException: a bad
                    // System.Type argument fails with NotSupportedException,
                    // a ref/out/generic shape (were the checks above ever
                    // bypassed) fails with InvalidOperationException, and a
                    // constructor or property setter that throws during
                    // binding surfaces its OWN exception type -- every one
                    // of these ran only because we were binding THIS
                    // argument, so every one of them is attributable to it.
                    var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                    return new
                    {
                        ok = false,
                        error = $"argument '{ps[i].Name}' ({ps[i].ParameterType.Name}): {real.Message}"
                    };
                }
            }

            var result = method.Invoke(instance, callArgs);

            // ValueTask and ValueTask<T> are awaitable but are NOT Tasks --
            // they are STRUCTS, so `result is Task` is false for both and
            // they used to fall through to the synchronous branch below and
            // be handed to System.Text.Json AS DATA. A method returning
            // "vt-value" came back as
            // {"ok":true,"result":{"isCompleted":true,...,"result":"vt-value"}},
            // which the client then deserialized into a DEFAULT
            // ValueTask<string> (a struct with a public parameterless ctor and
            // get-only properties, so nothing was rejected) and awaited as
            // null -- HTTP 200, ok:true, no error at any hop. It was also a
            // race, because the ValueTask was never awaited: a still-running
            // operation was reported complete, and a throwing one was
            // mislabelled as a serialization failure of the ValueTask struct
            // rather than reported as the library's own exception.
            //
            // Normalized here via AsTask(), NOT rejected the way ref/out and
            // open generics are: those have no wire representation at all,
            // whereas ValueTask is ordinary in modern .NET and a library
            // using it is exactly the kind this image exists to host. One
            // conversion at the top means the single await path below --
            // and everything downstream of it, including the polymorphic
            // serialization -- stays exactly as it was for Task/Task<T>.
            //
            // `effectiveReturnType` tracks what `result` is NOW (Task or
            // Task<T>), because the branch below reads the Result property
            // off that type. For every non-ValueTask method it is
            // method.ReturnType unchanged, so no other shape sees any
            // difference.
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

            object? returnValue;
            Type declaredReturnType;

            if (result is Task task)
            {
                await task;

                if (effectiveReturnType.IsGenericType)
                {
                    declaredReturnType = effectiveReturnType.GetGenericArguments()[0];
                    returnValue = effectiveReturnType.GetProperty("Result")!.GetValue(task);
                }
                else
                {
                    declaredReturnType = typeof(void);
                    returnValue = null;
                }
            }
            else
            {
                declaredReturnType = effectiveReturnType;
                returnValue = result;
            }

            // Serialized HERE, inside the guarded path -- not left to
            // Results.Ok's own serialization back in Program.cs, which runs
            // AFTER this method returns, outside every catch in this file.
            // A return type System.Text.Json refuses (System.Type, an
            // object cycle, ...) used to reach that unguarded serialization
            // as a raw CLR value, produce an empty HTTP 500 with no body,
            // and surface at the client as "JsonReaderException: The input
            // does not contain any JSON tokens" -- naming neither the
            // method nor the type. Same defect class already fixed for
            // callback responses (Task 8 Finding 1) and for arguments
            // (above): an error escaping the {ok,...} envelope and arriving
            // unattributable. Pre-serializing to a JsonElement here means
            // the only thing Program.cs ever hands to Results.Ok is
            // already-valid JSON -- re-emitting a JsonElement cannot fail
            // -- and a failure here gets the same purpose-built shape as a
            // bad argument.
            //
            // The declared/input type passed to SerializeToElement below
            // MUST be typeof(object), not returnValue's own runtime type.
            // System.Text.Json only emits a polymorphic "$type" discriminator
            // (via [JsonPolymorphic]/[JsonDerivedType] on a base type) when
            // it serializes through a declared type OTHER than the exact
            // runtime type -- which is exactly what the ORIGINAL
            // Results.Ok(new { ok = true, result }) path did, because the
            // anonymous type's `result` property is declared as `object`.
            // Passing returnValue.GetType() collapses that to "serialize as
            // its own exact type", which is indistinguishable from every
            // OTHER shape on the wire (byte-identical for every
            // non-polymorphic case measured) but silently drops the
            // discriminator for a polymorphic one -- the interface's
            // declared return type deserializes back into the wrong
            // (base, not derived) object client-side, or throws if that
            // base is abstract, with no error at all on the server side.
            try
            {
                var resultElement = JsonSerializer.SerializeToElement(returnValue, typeof(object), responseOptions);
                return new { ok = true, result = resultElement };
            }
            catch (Exception ex)
            {
                // Deliberately not narrowed to NotSupportedException or
                // JsonException: those cover System.Type/IntPtr and object
                // cycles, but a Stream return fails with
                // InvalidOperationException ("Timeouts are not supported on
                // this stream.") -- a message that reads like a network
                // fault and is not one -- and a property getter that throws
                // during serialization surfaces its own exception type.
                // Every one of these ran only because we were serializing
                // THIS return value, so every one of them is attributable
                // to it, whatever type it turns out to be.
                var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
                return new
                {
                    ok = false,
                    error = $"return value of '{request.Method}' ({declaredReturnType.Name}): {real.Message}"
                };
            }
        }
        catch (Exception ex)
        {
            // Unwrap: the caller wants the library's own exception, not
            // reflection's TargetInvocationException wrapper. A synchronous
            // throw from method.Invoke arrives wrapped; a faulted Task's
            // exception, observed at `await task`, arrives already unwrapped
            // — so only unwrap when there's actually a wrapper to remove.
            var real = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return new { ok = false, error = real.Message + NativeResolver.HintFor(real) };
        }
    }
}
