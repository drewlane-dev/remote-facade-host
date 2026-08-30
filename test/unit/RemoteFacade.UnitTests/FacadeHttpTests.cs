using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The five endpoints over a real socket, real Kestrel and the real MVC
/// pipeline -- in-process, so it costs milliseconds and needs no Docker.
///
/// This is the layer FacadeControllerTests cannot reach. Calling an action
/// method returns an IActionResult; it says nothing about whether the route
/// matched, what status code went out, or how MVC serialized the object
/// inside. Moving these handlers off minimal APIs changed all three of those
/// mechanisms at once -- a different routing table, a different result
/// executor, and JSON options read from Microsoft.AspNetCore.Mvc.JsonOptions
/// rather than Microsoft.AspNetCore.Http.Json.JsonOptions -- so the assertions
/// here are deliberately on EXACT BYTES rather than on parsed properties.
/// A camelCase policy that silently changed would still parse; it would not
/// still match test/baseline.sh, which holds these bodies byte-for-byte
/// against the previous release.
///
/// What stays in test/run.sh: everything that needs a real plugin assembly
/// loaded by a real container. Nothing here loads one, so PluginLoader.Assembly
/// is null and services are named by assembly-qualified name.
/// </summary>
public class FacadeHttpTests
{
    public sealed class Store
    {
        public string Put(string name) => $"put:{name}";
        public string Boom() => throw new InvalidOperationException("deliberate failure");
    }

    /// <summary>
    /// The host under test, wired by the SAME production call Program.cs uses.
    ///
    /// Calling AddFacade/MapFacade rather than re-listing AddControllers and
    /// its configuration here is the point: a test that built its own
    /// equivalent pipeline would keep passing while Program.cs's real one
    /// drifted away from it, and the two things most worth pinning --
    /// SuppressModelStateInvalidFilter and which assembly supplies the
    /// controllers -- live in exactly that configuration.
    /// </summary>
    private sealed class Host(WebApplication app) : IAsyncDisposable
    {
        public HttpClient Client { get; } = new() { BaseAddress = new Uri(app.Urls.First()) };

        public static async Task<Host> Serving(
            InstanceHolder holder,
            string registrar = "CsLib.StoreStartup.Configure",
            Func<IEnumerable<string>>? typeNames = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();

            builder.Services.AddFacade(holder, new ServedPlugin(registrar, typeNames ?? (() => [])));

            var app = builder.Build();
            // Port 0: the OS picks a free one, so parallel tests never collide.
            app.Urls.Add("http://127.0.0.1:0");
            app.MapFacade();
            await app.StartAsync();

            return new Host(app);
        }

        public Task<HttpResponseMessage> Post(string service, string method, string args = "[]") =>
            Client.PostAsync("/invoke", new StringContent(
                $$"""{"service":"{{service}}","method":"{{method}}","args":{{args}}}""",
                Encoding.UTF8, "application/json"));

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static InstanceHolder Holding<T>(Func<T> factory) where T : class =>
        new(() =>
        {
            var services = new ServiceCollection();
            services.AddSingleton(_ => factory());
            return new HostedGraph(
                services.BuildServiceProvider(),
                [typeof(T).AssemblyQualifiedName!],
                new HashSet<string>());
        });

    [Fact]
    public async Task Health_answers_200_with_the_registrar()
    {
        await using var host = await Host.Serving(Holding(() => new Store()), "My.Startup.Configure");

        var response = await host.Client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"registrar":"My.Startup.Configure"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Types_answers_200_with_a_bare_json_array()
    {
        // A bare array, not an object wrapping one: RemoteHost.GetAsync
        // deserializes /services straight into string[], and /types is the same
        // shape by construction.
        await using var host = await Host.Serving(
            Holding(() => new Store()), typeNames: () => ["CsLib.Store", "CsLib.IStore"]);

        var response = await host.Client.GetAsync("/types");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""["CsLib.Store","CsLib.IStore"]""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Services_answers_200_with_a_bare_json_array()
    {
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.GetAsync("/services");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"""["{typeof(Store).AssemblyQualifiedName}"]""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Reset_answers_204_with_no_body()
    {
        // RemoteHost.ResetAsync accepts any 2xx, but test/run.sh asserts 204
        // exactly, and README documents 204.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.DeleteAsync("/instance");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_successful_invoke_answers_200_with_the_ok_envelope()
    {
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Post(typeof(Store).AssemblyQualifiedName!, "Put", """["ledger"]""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":true,"result":"put:ledger"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_method_that_throws_answers_200_with_the_error_envelope_not_500()
    {
        // The invariant README states outright: errors never arrive as a bare
        // 500 with an empty body.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Post(typeof(Store).AssemblyQualifiedName!, "Boom");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("""{"ok":false,"error":"deliberate failure"}""", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_route_is_404_rather_than_reaching_a_handler()
    {
        await using var host = await Host.Serving(Holding(() => new Store()));

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/nope")).StatusCode);
    }

    [Fact]
    public async Task Invoke_refuses_a_body_that_is_not_json()
    {
        // Pinned rather than asserted as desirable: a malformed body has never
        // been an {ok:false} case -- it fails before binding produces an
        // InvokeRequest at all -- and this records which status the MVC
        // pipeline gives it, so a later change to it is a visible diff.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsync(
            "/invoke", new StringContent("{not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        // Empty, not a ProblemDetails document. [ApiController] maps client
        // error statuses onto RFC 9110 bodies by default; the pre-migration
        // image sent nothing at all, and a traceId in the body would also make
        // these responses unreproducible byte-for-byte.
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Invoke_refuses_a_body_sent_without_a_json_content_type()
    {
        // Every /invoke in test/run.sh sets Content-Type: application/json, so
        // this is the one wire behaviour the container suite never exercises.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsync(
            "/invoke", new StringContent("""{"service":"x","method":"y","args":[]}""",
                Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    public async Task Invoke_refuses_a_body_that_binds_to_nothing(string body)
    {
        // Both bind to a null InvokeRequest. Measured at 400 on the
        // pre-migration image, so they are 400 here -- and specifically NOT the
        // 500 that a null slipping through to request.Service would produce.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsync(
            "/invoke", new StringContent(body, Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invoke_on_the_wrong_verb_is_405()
    {
        await using var host = await Host.Serving(Holding(() => new Store()));

        Assert.Equal(
            HttpStatusCode.MethodNotAllowed, (await host.Client.GetAsync("/invoke")).StatusCode);
    }

    [Fact]
    public async Task A_body_with_no_method_reaches_the_envelope_rather_than_being_rejected()
    {
        // Guards SuppressModelStateInvalidFilter. Method is a non-nullable
        // member of InvokeRequest, so [ApiController]'s automatic validation
        // would answer 400 here; the pre-migration image answers 200 with this
        // envelope, and Invoker is what produces it.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsync("/invoke", new StringContent(
            $$"""{"service":"{{typeof(Store).AssemblyQualifiedName}}","args":[]}""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"ok":false,"error":"no method '' taking 0 argument(s)"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_body_with_no_args_is_treated_as_a_zero_argument_call()
    {
        // The other half of the same guard: Invoker reads
        // request.Args?.Length ?? 0, so an absent "args" is a no-arg call, not
        // a validation failure.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsync("/invoke", new StringContent(
            $$"""{"service":"{{typeof(Store).AssemblyQualifiedName}}","method":"Boom"}""",
            Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            """{"ok":false,"error":"deliberate failure"}""",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_invoke_naming_no_service_answers_200_with_the_v3_explanation()
    {
        // Reachable only over the wire with a body that binds but carries no
        // "service": the JSON property is optional on InvokeRequest.
        await using var host = await Host.Serving(Holding(() => new Store()));

        var response = await host.Client.PostAsJsonAsync("/invoke", new { method = "Put", args = Array.Empty<int>() });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"ok\":false", body);
        Assert.Contains("must name the service it wants", body);
    }
}
