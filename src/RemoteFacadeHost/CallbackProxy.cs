using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RemoteFacadeHost;

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
        // Type.EmptyTypes disambiguates from DispatchProxy's other public
        // overload, Create(Type, Type) -- both are named "Create" and a
        // parameterless GetMethod(name) lookup is ambiguous between them.
        var proxy = typeof(DispatchProxy)
            .GetMethod(nameof(DispatchProxy.Create), Type.EmptyTypes)!
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

        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        if (!response.IsSuccessStatusCode)
        {
            // A reachable-but-wrong endpoint (an unmatched route, a 500 from
            // the CallbackHost's own request parsing) returns a body that
            // JsonDocument.Parse can't make sense of; that failure names
            // neither the URL nor which interface/method was being called.
            // Catch it here instead, where both are still in scope.
            throw new InvalidOperationException(
                $"callback to {_http.BaseAddress} for {_interfaceName}.{method.Name} returned " +
                $"{(int)response.StatusCode} {response.ReasonPhrase}, not a {{ok, result}} body. " +
                $"Body: {text}");
        }

        var json = JsonDocument.Parse(text);

        if (!json.RootElement.GetProperty("ok").GetBoolean())
        {
            throw new InvalidOperationException(json.RootElement.GetProperty("error").GetString());
        }

        var resultEl = json.RootElement.GetProperty("result");
        var rt = method.ReturnType;
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        if (rt == typeof(void)) return null;
        if (rt == typeof(Task)) return Task.CompletedTask;

        // ValueTask and ValueTask<T> are STRUCTS, so neither matched any
        // branch here and both fell through to "anything else, deserialised
        // directly" -- which quietly SUCCEEDED, because a ValueTask has a
        // public parameterless constructor and only get-only properties, so
        // System.Text.Json built a DEFAULT one and rejected nothing. The
        // library then awaited null (or 0), with ok:true and no error on any
        // hop. The mirror image of the /invoke path's C1, and fixed to match.
        if (rt == typeof(ValueTask)) return default(ValueTask);

        var isTaskOfT = rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>);
        var isValueTaskOfT = rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(ValueTask<>);

        if (!isTaskOfT && !isValueTaskOfT)
        {
            return resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(rt, opts);
        }

        var inner = rt.GetGenericArguments()[0];
        var value = resultEl.ValueKind == JsonValueKind.Null ? null : resultEl.Deserialize(inner, opts);

        // ValueTask<T> is built through a typed helper rather than
        // Activator.CreateInstance: ValueTask<T> has both a (T result) and a
        // (Task<T> task) constructor, which are ambiguous by reflection when T
        // is itself object or a Task.
        return isTaskOfT
            ? typeof(Task).GetMethod("FromResult")!.MakeGenericMethod(inner).Invoke(null, [value])
            : ToValueTaskMethod.MakeGenericMethod(inner).Invoke(null, [value]);
    }

    private static readonly MethodInfo ToValueTaskMethod =
        typeof(CallbackProxy).GetMethod(nameof(ToValueTask), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static ValueTask<T> ToValueTask<T>(object? value) => new(value is null ? default! : (T)value);
}
