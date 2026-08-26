using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The acquisition path. v3 made this the only way to obtain a proxy, so the
/// first thing any client says to a host is GET /services -- which means a
/// mistyped base URL is caught here rather than on the first call.
///
/// This coverage used to live in the container suite, driven through a real
/// listener that did not serve the protocol (CallbackHost, borrowed for the
/// purpose). With callbacks removed there is no such listener to borrow, and
/// serving a status directly is both faster and more precise than arranging
/// for one.
/// </summary>
public class RemoteHostTests
{
    public interface IThing { string Go(); }

    [Fact]
    public async Task A_non_success_from_services_names_the_url_the_request_and_the_status()
    {
        // Not a JSON parse failure. An HTML error page would otherwise surface
        // as "'<' is an invalid start of a value", naming none of the three
        // things needed to find the mistake.
        await using var stub = await StubHost.ServingServices("<html>not found</html>", 404);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteHost.At(stub.Url).GetAsync<IThing>());

        Assert.Contains("404", ex.Message);
        Assert.Contains("/services", ex.Message);
        Assert.Contains(stub.Url, ex.Message);
    }

    [Fact]
    public async Task A_service_the_host_does_not_have_is_refused_with_the_list()
    {
        await using var stub = await StubHost.ServingServices("""["Some.IOther","Some.IThird"]""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteHost.At(stub.Url).GetAsync<IThing>());

        Assert.Contains(typeof(IThing).FullName!, ex.Message);
        Assert.Contains("Some.IOther", ex.Message);
    }

    [Fact]
    public async Task A_registered_service_yields_a_working_proxy()
    {
        var name = typeof(IThing).FullName!;
        await using var stub = await StubHost.ServingServices(
            $"""["{name}"]""", onPost: """{"ok":true,"result":"went"}""");

        var thing = await RemoteHost.At(stub.Url).GetAsync<IThing>();

        Assert.Equal("went", thing.Go());
    }

    [Fact]
    public async Task A_non_interface_is_refused_before_any_HTTP_happens()
    {
        await using var stub = await StubHost.ServingServices("[]");

        await Assert.ThrowsAsync<ArgumentException>(
            () => RemoteHost.At(stub.Url).GetAsync<string>());

        // Nothing was asked of the host: the mistake is in the caller's own
        // code and needs no round trip to diagnose.
        Assert.Empty(stub.Bodies);
    }
}
