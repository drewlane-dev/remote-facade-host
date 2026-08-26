using System.Text.Json;
using RemoteFacadeHost.Client;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The client half of the protocol, against a real endpoint on localhost.
/// These run in milliseconds, so shapes worth one slow container case each --
/// every awaitable return, every failure mode -- can all be covered.
/// </summary>
public class RemoteFacadeTests
{
    public interface IProbe
    {
        Task<string> TaskOfT(string s);
        ValueTask<int> ValueTaskOfT();
        Task Bare();
        ValueTask BareValueTask();
        void Nothing();
        string Sync();
        Task<Widget> Complex();
    }

    public sealed class Widget
    {
        public string Name { get; set; } = "";
        public int Count { get; set; }
    }

    [Fact]
    public async Task For_T_does_NOT_put_a_service_field_on_the_wire()
    {
        // Load-bearing. test/baseline.sh compares /invoke bodies byte-for-byte
        // against the previous release, and a service field present-but-null
        // would break that for every v1.0-shaped call.
        await using var stub = await StubHost.Serving("""{"ok":true,"result":"x"}""");

        await RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).TaskOfT("hi");

        var body = stub.Bodies.Single();
        Assert.DoesNotContain("service", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("TaskOfT", json.RootElement.GetProperty("method").GetString());
        Assert.Equal("hi", json.RootElement.GetProperty("args")[0].GetString());
    }

    [Fact]
    public async Task ForService_DOES_name_the_service_on_every_call()
    {
        await using var stub = await StubHost.Serving("""{"ok":true,"result":"x"}""");
        using var http = new HttpClient { BaseAddress = new Uri(stub.Url) };

        await RemoteFacadeHost.Client.RemoteFacade.ForService<IProbe>(http, "Some.IThing").TaskOfT("hi");

        using var json = JsonDocument.Parse(stub.Bodies.Single());
        Assert.Equal("Some.IThing", json.RootElement.GetProperty("service").GetString());
    }

    [Fact]
    public async Task A_Task_of_T_carries_the_deserialized_value()
    {
        await using var stub = await StubHost.Serving("""{"ok":true,"result":"pong"}""");
        Assert.Equal("pong", await RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).TaskOfT("ping"));
    }

    [Fact]
    public async Task A_ValueTask_of_T_carries_the_deserialized_value()
    {
        await using var stub = await StubHost.Serving("""{"ok":true,"result":11}""");
        Assert.Equal(11, await RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).ValueTaskOfT());
    }

    [Fact]
    public async Task A_complex_type_round_trips_case_insensitively()
    {
        // The host serializes with web defaults (camelCase); the client must
        // not require the interface's PascalCase to match.
        await using var stub = await StubHost.Serving("""{"ok":true,"result":{"name":"w","count":3}}""");

        var widget = await RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).Complex();

        Assert.Equal("w", widget.Name);
        Assert.Equal(3, widget.Count);
    }

    [Theory]
    [InlineData("Bare")]
    [InlineData("BareValueTask")]
    [InlineData("Nothing")]
    public async Task A_valueless_shape_completes_without_reading_a_result(string method)
    {
        // The envelope deliberately has no "result" at all. Reading one would
        // throw, so this pins that the client does not.
        await using var stub = await StubHost.Serving("""{"ok":true}""");
        var probe = RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url);

        switch (method)
        {
            case "Bare": await probe.Bare(); break;
            case "BareValueTask": await probe.BareValueTask(); break;
            default: probe.Nothing(); break;
        }

        Assert.Single(stub.Bodies);
    }

    [Fact]
    public async Task A_synchronous_method_blocks_and_returns_the_value()
    {
        await using var stub = await StubHost.Serving("""{"ok":true,"result":"now"}""");
        Assert.Equal("now", RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).Sync());
    }

    [Fact]
    public async Task An_ok_false_envelope_is_rethrown_with_the_server_s_message()
    {
        await using var stub = await StubHost.Serving("""{"ok":false,"error":"plugin said no"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).TaskOfT("x"));

        Assert.Equal("plugin said no", ex.Message);
    }

    [Fact]
    public async Task A_non_success_status_names_the_url_the_interface_the_method_and_the_code()
    {
        // The status is checked BEFORE parsing on purpose: an HTML error page
        // otherwise fails as "'<' is an invalid start of a value", which reads
        // like a protocol bug and names none of the four things you need.
        await using var stub = await StubHost.Serving(_ => (502, "<html>bad gateway</html>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).TaskOfT("x"));

        Assert.Contains("502", ex.Message);
        Assert.Contains("TaskOfT", ex.Message);
        Assert.Contains(nameof(IProbe), ex.Message);
        Assert.Contains(stub.Url, ex.Message);
    }

    [Fact]
    public async Task An_async_call_is_handed_back_before_it_completes()
    {
        // What lets two instances be driven at once. Before this, every shape
        // went through GetAwaiter().GetResult() in Invoke, so two calls ran
        // strictly one after the other even under Task.WhenAll.
        var release = new TaskCompletionSource();
        await using var stub = await StubHost.Serving(_ =>
        {
            release.Task.Wait();
            return (200, """{"ok":true,"result":"done"}""");
        });

        var pending = RemoteFacadeHost.Client.RemoteFacade.For<IProbe>(stub.Url).TaskOfT("x");

        Assert.False(pending.IsCompleted);
        release.SetResult();
        Assert.Equal("done", await pending);
    }
}
