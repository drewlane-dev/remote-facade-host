using System.Reflection;
using System.Text;
using System.Text.Json;

namespace RemoteFacadeHost;

/// <summary>
/// Wraps a registered service so the test process is told before each call, and
/// decides whether it proceeds.
///
/// This is NOT the callback proxy. A callback REPLACES an implementation: the
/// test serves the call and returns a value. This one DELEGATES to the real
/// implementation and merely interposes -- so the graph behaves exactly as it
/// would have, except that the test gets a rendezvous inside the container.
///
/// The rendezvous is the point. The notification is a synchronous HTTP call, so
/// a test handler that does not return HOLDS EXECUTION inside the container at
/// this exact line. That is what makes "kill it after the 27th write" possible
/// without a test hook in the product: the test freezes the container, or kills
/// it, or lets it continue, while the call is suspended mid-graph.
///
/// It sees calls that cross a DI boundary and nothing else. A method calling
/// another method on `this`, or reaching straight for File.WriteAllText, is
/// invisible here -- there is no seam to interpose on.
/// </summary>
public class InterceptProxy : DispatchProxy
{
    private object _inner = null!;
    private HttpClient _http = null!;
    private string _service = null!;
    private int _calls;

    public static object Wrap(Type interfaceType, object inner, string baseUrl)
    {
        // The non-generic overload, added in .NET 10. Reflecting for the
        // generic Create<T, TProxy>() by name is now AMBIGUOUS -- both
        // overloads match GetMethod(nameof(Create)) and it throws before any
        // test can run.
        var proxy = Create(interfaceType, typeof(InterceptProxy));

        var self = (InterceptProxy)proxy;
        self._inner = inner;
        self._service = interfaceType.FullName!;

        // No timeout: a handler is EXPECTED to block, sometimes for as long as
        // it takes to pause or kill this very container. A default 100s ceiling
        // would abort the rendezvous the test is relying on.
        self._http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };

        return proxy;
    }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(method);

        // Interlocked, not ++: two facades resolved from one graph can call the
        // same intercepted service concurrently, and a lost update would make
        // "kill on call 27" land somewhere else.
        var call = Interlocked.Increment(ref _calls);

        var decision = Notify(method.Name, call);

        // "throw" is a fault the test injects; it must look to the graph like
        // the dependency itself failed, not like a transport problem, or the
        // code under test would take a retry path it would never take in
        // production.
        if (decision is { Action: "throw" })
        {
            throw new InvalidOperationException(
                decision.Message ?? $"{_service}.{method.Name} was failed by an interceptor.");
        }

        try
        {
            return method.Invoke(_inner, args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Unwrap, so the real implementation's exception reaches the caller
            // exactly as it would have without the interposition.
            throw ex.InnerException;
        }
    }

    private Decision? Notify(string method, int call)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { service = _service, method, call });

            var response = _http.PostAsync("/intercept",
                new StringContent(body, Encoding.UTF8, "application/json")).GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode) return null;

            var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonSerializer.Deserialize<Decision>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            // A listener that has gone away must not take the graph down with
            // it. The test is the thing that died, and failing the plugin's
            // call would disguise that as an application fault.
            Console.Error.WriteLine($"[intercept] {_service}.{method} notify failed: {ex.Message}");
            return null;
        }
    }

    private sealed record Decision(string? Action, string? Message);
}
