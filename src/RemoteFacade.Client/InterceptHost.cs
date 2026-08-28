using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteFacadeHost.Client;

/// <summary>What an intercepted call should do next.</summary>
public sealed class Intercepted
{
    /// <summary>Full name of the intercepted service type.</summary>
    public required string Service { get; init; }

    /// <summary>Method being called.</summary>
    public required string Method { get; init; }

    /// <summary>1-based call number, counted per service across the container's lifetime.</summary>
    public required int Call { get; init; }
}

/// <summary>
/// Listens for intercepted calls from a container, in the TEST process.
///
/// A handler that does not return holds execution inside the container at that
/// call. That is the whole feature: it gives a test a known point mid-graph at
/// which to pause the container, kill it, start a competitor, or check what a
/// half-finished operation left behind.
///
/// Bind it with LIB_INTERCEPT={"Full.IService":"http://host.docker.internal:PORT"}.
/// </summary>
public sealed class InterceptHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly Dictionary<string, Func<Intercepted, Task<string?>>> _handlers = new();
    private readonly List<Intercepted> _seen = [];

    private InterceptHost(WebApplication app) => _app = app;

    /// <summary>Every call observed, in order. Useful as an assertion of its own.</summary>
    public IReadOnlyList<Intercepted> Seen { get { lock (_seen) return _seen.ToArray(); } }

    public static InterceptHost Start(int port)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        app.Urls.Add($"http://0.0.0.0:{port}");

        var host = new InterceptHost(app);

        app.MapPost("/intercept", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var call = JsonSerializer.Deserialize<Intercepted>(await reader.ReadToEndAsync(),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            lock (host._seen) host._seen.Add(call);

            Func<Intercepted, Task<string?>>? handler;
            lock (host._handlers)
            {
                host._handlers.TryGetValue(Key(call.Service, call.Method), out handler);
            }

            // The await here is what suspends the container. Nothing is written
            // back until the handler returns, and the proxy on the other end is
            // blocked on this response.
            var message = handler is null ? null : await handler(call);

            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(message is null
                ? """{"action":"continue"}"""
                : JsonSerializer.Serialize(new { action = "throw", message }));
        });

        app.StartAsync().GetAwaiter().GetResult();
        return host;
    }

    /// <summary>
    /// Runs <paramref name="handler"/> before each call to
    /// <paramref name="method"/>. Return null to let the call proceed, or a
    /// message to make the dependency throw instead.
    /// </summary>
    /// <param name="method">
    /// Use <c>nameof</c>. It is compiler-checked, so a renamed method breaks
    /// the test rather than silently never firing.
    /// </param>
    public InterceptHost On<T>(string method, Func<Intercepted, Task<string?>> handler)
    {
        lock (_handlers) _handlers[Key(typeof(T).FullName!, method)] = handler;
        return this;
    }

    /// <summary>The observe-only form, for holding or killing without failing the call.</summary>
    public InterceptHost On<T>(string method, Func<Intercepted, Task> handler) =>
        On<T>(method, async c => { await handler(c); return null; });

    private static string Key(string service, string method) => $"{service}.{method}";

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
