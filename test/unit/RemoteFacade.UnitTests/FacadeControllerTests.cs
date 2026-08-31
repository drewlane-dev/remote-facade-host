using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The endpoint layer driven as a plain class -- no HTTP, no container.
///
/// This is the coverage that did not exist while these five handlers were
/// minimal-API lambdas in Program.cs: a lambda inside a top-level statement is
/// not reachable from a test, so every one of /invoke's rejection branches was
/// provable only by starting a container and speaking the protocol to it. The
/// branches are pure decisions over an InstanceHolder, and HostedGraph is
/// constructible over any ServiceProvider (see Graphs), so they cost
/// milliseconds here.
///
/// What deliberately stays elsewhere: the wire itself. These tests call
/// methods and read the object handed to ObjectResult.Value, which says
/// nothing about routing, status codes or how MVC serializes that object --
/// FacadeHttpTests covers those over a real pipeline, and test/run.sh covers
/// them over a real container.
/// </summary>
public class FacadeControllerTests
{
    private static FacadeController Controller(
        InstanceHolder? holder = null,
        string registrar = "CsLib.StoreStartup.Configure",
        Func<IEnumerable<string>>? typeNames = null) =>
        new(holder ?? new InstanceHolder(() => Graphs.Named("Some.IThing", new Counted())),
            new ServedPlugin(registrar, typeNames ?? (() => [])),
            Options.Create(new JsonOptions()));

    [Fact]
    public void Health_reports_the_registrar_it_was_started_with()
    {
        // The one thing a caller can ask that proves the host got as far as
        // reading its own configuration. WithRemoteFacade's wait strategy
        // polls this, so its shape is load-bearing for every consumer.
        var result = Assert.IsType<OkObjectResult>(Controller(registrar: "My.Startup.Configure").Health());

        Assert.Equal(
            "My.Startup.Configure",
            result.Value!.GetType().GetProperty("Registrar")!.GetValue(result.Value));
    }

    [Fact]
    public void Types_reports_the_public_type_names_of_the_loaded_assembly()
    {
        // The endpoint exists because a wrong LIB_REGISTRAR or service name is
        // otherwise a dead end -- notably for VB, where RootNamespace is
        // prepended and the real name is not what a C# developer would write.
        var result = Assert.IsType<OkObjectResult>(
            Controller(typeNames: () => ["VbLib.VbLib.VbStore", "VbLib.VbLib.VbStartup"]).Types());

        Assert.Equal(
            new[] { "VbLib.VbLib.VbStore", "VbLib.VbLib.VbStartup" },
            Assert.IsAssignableFrom<IEnumerable<string>>(result.Value));
    }

    [Fact]
    public void Services_reports_what_the_startup_registered()
    {
        var holder = new InstanceHolder(() => Graphs.Named("CsLib.IStore", new Counted()));

        var result = Assert.IsType<OkObjectResult>(Controller(holder).Services());

        Assert.Equal(["CsLib.IStore"], Assert.IsAssignableFrom<IEnumerable<string>>(result.Value));
    }

    [Fact]
    public void Reset_rebuilds_the_graph_and_answers_204()
    {
        // 204 specifically: RemoteHost.ResetAsync throws on anything else, and
        // names 204 in the message it throws.
        var built = 0;
        var holder = new InstanceHolder(() =>
        {
            built++;
            return Graphs.Named($"Graph.{built}", new Counted());
        });

        Assert.IsType<NoContentResult>(Controller(holder).Reset());

        Assert.Equal(2, built);
    }

    // ---- /invoke -------------------------------------------------------
    //
    // Every case below asserts the ENVELOPE, not an exception: /invoke's
    // contract is that a failure of any kind arrives as HTTP 200 carrying
    // {"ok":false,"error":...}. README states it outright -- "Errors never
    // arrive as a bare 500 with an empty body" -- and it is the property that
    // lets a client parse one shape rather than branching on status first.

    public sealed class Store
    {
        public string Put(string name) => $"put:{name}";
    }

    /// <summary>
    /// A graph holding one real service, named the way /invoke has to name it
    /// when no plugin is loaded.
    ///
    /// HostedGraph.Resolve looks a service up with
    /// <c>PluginLoader.Assembly?.GetType(name) ?? Type.GetType(name)</c>.
    /// PluginLoader.Assembly is null in a unit test, and Type.GetType is called
    /// from RemoteFacadeHost.dll, so a bare "RemoteFacade.UnitTests.Store"
    /// resolves to nothing. The assembly-qualified name is what makes the
    /// success path reachable without loading a plugin -- a container passes
    /// the plain name because its PluginLoader.Assembly is set.
    /// </summary>
    private static HostedGraph GraphWith<T>(Func<T> factory) where T : class
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => factory());
        var provider = services.BuildServiceProvider();

        return new HostedGraph(provider, [typeof(T).AssemblyQualifiedName!], new HashSet<string>());
    }

    private static async Task<JsonElement> Invoke(FacadeController controller, InvokeRequest request)
    {
        var result = Assert.IsType<OkObjectResult>(await controller.Invoke(request));

        // Round-tripped through JSON for the same reason InvokerTests does it:
        // the anonymous object is an implementation detail, and what a caller
        // actually receives is the serialized envelope.
        return JsonSerializer.SerializeToElement(
            result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_call_naming_no_service_is_told_that_v2s_un_named_calls_are_gone(string? service)
    {
        // Whitespace counts as un-named, not as a service called "   ", so the
        // reader gets the real explanation rather than "not a type in the
        // plugin assembly".
        var json = await Invoke(Controller(), new InvokeRequest("Put", [], service));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("must name the service it wants", json.GetProperty("error").GetString());
        Assert.Contains("LIB_TYPE", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_unregistered_service_gets_Resolves_own_message_verbatim()
    {
        // Verbatim, NOT prefixed: Resolve's misses already name the service
        // and list what IS registered, so attributing them again would say the
        // same thing twice.
        var holder = new InstanceHolder(() => Graphs.Named("CsLib.IStore", new Counted()));

        var json = await Invoke(Controller(holder), new InvokeRequest("Put", [], "CsLib.INope"));

        Assert.False(json.GetProperty("ok").GetBoolean());
        var error = json.GetProperty("error").GetString()!;
        Assert.StartsWith("service 'CsLib.INope' is not a type in the plugin assembly.", error);
        Assert.Contains("CsLib.IStore", error);
    }

    [Fact]
    public async Task A_service_constructor_that_throws_is_attributed_to_the_service()
    {
        // The branch that used to escape UseAsync entirely and reach Kestrel as
        // a zero-byte HTTP 500: DI does not wrap a constructor's exception, so
        // an ArgumentException from one propagates unwrapped past a catch that
        // only names InvalidOperationException. Attributed, because unlike
        // Resolve's own messages this one comes from the plugin and does not
        // know where it came from.
        var holder = new InstanceHolder(() => GraphWith<Store>(
            () => throw new ArgumentException("wiring is wrong: no connection string")));

        var json = await Invoke(
            Controller(holder), new InvokeRequest("Put", [], typeof(Store).AssemblyQualifiedName));

        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal(
            $"cannot resolve service '{typeof(Store).AssemblyQualifiedName}': " +
            "ArgumentException: wiring is wrong: no connection string",
            json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_resolved_service_is_dispatched_against_and_its_return_value_comes_back()
    {
        var holder = new InstanceHolder(() => GraphWith(() => new Store()));
        var args = new[] { JsonSerializer.SerializeToElement("ledger") };

        var json = await Invoke(
            Controller(holder), new InvokeRequest("Put", args, typeof(Store).AssemblyQualifiedName));

        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("put:ledger", json.GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_reset_landing_mid_call_does_not_disposethe_graph_the_call_is_running_on()
    {
        // The property the lease exists for, driven through the endpoint rather
        // than through InstanceHolder directly. Before the lease, two calls in
        // flight across one DELETE /instance both came back
        // {"ok":false,"error":"Cannot access a disposed object..."}.
        var released = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var holder = new InstanceHolder(() => GraphWith(() => new Gated(entered, released)));
        var controller = Controller(holder);

        var call = Invoke(controller, new InvokeRequest("Wait", [], typeof(Gated).AssemblyQualifiedName));
        await entered.Task;

        controller.Reset();
        released.SetResult();

        var json = await call;
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal("finished", json.GetProperty("result").GetString());
    }

    public sealed class Gated(TaskCompletionSource entered, TaskCompletionSource released)
    {
        public async Task<string> Wait()
        {
            entered.SetResult();
            await released.Task;
            return "finished";
        }
    }
}
