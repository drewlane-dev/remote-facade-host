using System.Text.Json;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// Method lookup and return-shaping, driven directly rather than over HTTP.
/// The container suite proves the protocol end to end; these cover the input
/// space -- every awaitable shape, every rejection -- at a cost per case that
/// makes covering all of them reasonable.
/// </summary>
public class InvokerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public sealed class Subject
    {
        public string Sync(string s) => $"sync:{s}";
        public int Number() => 7;
        public void Nothing() { }
        public Task<string> TaskOfT(string s) => Task.FromResult($"task:{s}");
        public Task Bare() => Task.CompletedTask;
        public ValueTask<int> ValueTaskOfT() => new(11);
        public ValueTask BareValueTask() => default;
        public T Generic<T>(T value) => value;
        public string Overloaded() => "none";
        public string Overloaded(string a) => $"one:{a}";
        public string ByRef(ref int x) => x.ToString();
        public string Throws() => throw new InvalidOperationException("plugin said no");
        public string NeedsInt(int n) => $"n={n}";
    }

    private static async Task<JsonElement> Call(string method, params object?[] args)
    {
        var elements = args.Select(a => JsonSerializer.SerializeToElement(a, Options)).ToArray();
        var result = await Invoker.InvokeAsync(
            new Subject(), typeof(Subject), new InvokeRequest(method, elements), Options);

        // Round-tripped through JSON on purpose: the anonymous object is an
        // implementation detail, and what callers actually receive is the
        // serialized envelope.
        return JsonSerializer.SerializeToElement(result, Options);
    }

    [Theory]
    [InlineData("Sync", "sync:hi")]
    [InlineData("TaskOfT", "task:hi")]
    public async Task Every_string_returning_shape_produces_the_same_envelope(string method, string expected)
    {
        var json = await Call(method, "hi");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(expected, json.GetProperty("result").GetString());
    }

    [Fact]
    public async Task A_synchronous_value_return_is_shaped_like_an_async_one()
    {
        var json = await Call("Number");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(7, json.GetProperty("result").GetInt32());
    }

    [Fact]
    public async Task ValueTask_of_T_carries_its_value()
    {
        // The shape that regressed once: a ValueTask<T> result arrived as null
        // with ok:true, because the value was never pulled out of it.
        var json = await Call("ValueTaskOfT");
        Assert.True(json.GetProperty("ok").GetBoolean());
        Assert.Equal(11, json.GetProperty("result").GetInt32());
    }

    [Theory]
    [InlineData("Nothing")]
    [InlineData("Bare")]
    [InlineData("BareValueTask")]
    public async Task A_void_or_bare_awaitable_still_reports_success(string method)
    {
        var json = await Call(method);
        Assert.True(json.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task An_unknown_method_names_the_method_and_the_argument_count()
    {
        var json = await Call("NoSuchMethod", "a");
        Assert.False(json.GetProperty("ok").GetBoolean());
        var error = json.GetProperty("error").GetString()!;
        Assert.Contains("NoSuchMethod", error);
        Assert.Contains("1 argument", error);
    }

    [Fact]
    public async Task Overloads_are_selected_by_argument_count()
    {
        Assert.Equal("none", (await Call("Overloaded")).GetProperty("result").GetString());
        Assert.Equal("one:x", (await Call("Overloaded", "x")).GetProperty("result").GetString());
    }

    [Fact]
    public async Task An_open_generic_method_is_rejected_by_name()
    {
        var json = await Call("Generic", "x");
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("Generic", json.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task A_by_ref_parameter_is_rejected_and_the_parameter_is_named()
    {
        var json = await Call("ByRef", 1);
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("x", json.GetProperty("error").GetString()!);
    }

    [Fact]
    public async Task A_throwing_method_returns_the_library_s_own_message()
    {
        // Not reflection's TargetInvocationException wrapper: the caller wants
        // to see what their own code said.
        var json = await Call("Throws");
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Equal("plugin said no", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task An_argument_that_cannot_deserialize_names_the_parameter()
    {
        var json = await Call("NeedsInt", "not-a-number");
        Assert.False(json.GetProperty("ok").GetBoolean());
        Assert.Contains("n", json.GetProperty("error").GetString()!);
    }
}
