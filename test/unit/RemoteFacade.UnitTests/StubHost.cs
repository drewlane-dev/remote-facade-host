using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RemoteFacade.UnitTests;

/// <summary>
/// A real HTTP endpoint, in-process, on an ephemeral port.
///
/// Not a fake HttpMessageHandler: RemoteFacade.For&lt;T&gt; constructs its own
/// HttpClient from a URL, so there is nothing to inject. Serving a real
/// endpoint also means the body asserted here is the one that actually went
/// over a socket, serializer and all -- which is the thing worth pinning,
/// since test/baseline.sh holds that shape byte-for-byte against the previous
/// release.
/// </summary>
public sealed class StubHost(WebApplication app, string url, List<string> bodies) : IAsyncDisposable
{
    public string Url { get; } = url;

    /// <summary>Raw request bodies, in arrival order.</summary>
    public IReadOnlyList<string> Bodies { get { lock (bodies) return bodies.ToArray(); } }

    public static Task<StubHost> Serving(string json, int status = 200) =>
        Serving(_ => (status, json));

    public static async Task<StubHost> Serving(Func<string, (int Status, string Json)> respond)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();

        var app = builder.Build();
        // Port 0: the OS picks a free one, so parallel tests never collide.
        app.Urls.Add("http://127.0.0.1:0");
        var bodies = new List<string>();

        app.MapPost("/{**rest}", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            lock (bodies) bodies.Add(body);

            var (status, json) = respond(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync(json);
        });

        await app.StartAsync();

        // The BOUND address, not the requested one: port 0 means the OS picks,
        // so reading back what it picked is the only way to reach it.
        return new StubHost(app, app.Urls.First(), bodies);
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
}
