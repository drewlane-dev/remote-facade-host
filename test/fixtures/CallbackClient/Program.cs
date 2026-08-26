using System.Text;
using CsLib;
using Moq;
using RemoteFacadeHost.Client;

// A REAL Moq mock, in the test process, serving an instance in a container.
// One setup per return shape the callback leg must handle: string ("anything
// else, deserialised directly"), Task<int> (wrapped value), void (null), and
// Task -- the last configured to THROW, so exception fidelity over the two
// hops (mock -> CallbackHost -> CallbackProxy -> back to this call site) is
// proven with the mock's own message, not a stand-in.
var mock = new Mock<IStamp>();
mock.Setup(s => s.Value()).Returns("from-moq");
mock.Setup(s => s.CountAsync()).ReturnsAsync(42);
mock.Setup(s => s.Ping());
mock.Setup(s => s.FailAsync()).ThrowsAsync(new InvalidOperationException("mock-says-no"));

// The callback leg's ValueTask pair. Both do their real work AFTER a delay on
// purpose: a host that serializes the ValueTask instead of awaiting it answers
// BEFORE the value exists, so the corruption shows up in what ARRIVES, not in
// the {ok:true} envelope.
var vtPinged = false;

async ValueTask<string> VtValueImpl()
{
    await Task.Delay(50);
    return "vt-from-moq";
}

async ValueTask VtPingImpl()
{
    // A non-generic ValueTask carries no value, so only the mock's own side
    // effect can show whether it was awaited. 500ms is a deliberate margin
    // over the ~1ms between the /invoke round trip returning below and the
    // flag being read: an un-awaited ValueTask still completes EVENTUALLY, so
    // the two cases must be separated by orders of magnitude, not a coin flip.
    await Task.Delay(500);
    vtPinged = true;
}

mock.Setup(s => s.VtValueAsync()).Returns(VtValueImpl);
mock.Setup(s => s.VtPingAsync()).Returns(VtPingImpl);

await using var callbacks = CallbackHost.Start(9090);

// The guard exists to fail LOUDLY at registration, not silently mis-key and
// leave every later call to report a misleading 404/"no mock registered".
// Pin the timing (it must throw here, synchronously, not later) and the
// message (it must name the offending type and say an interface is
// required) -- a bare "it threw" would also pass for an unrelated NRE.
try
{
    callbacks.Serve(new FakeStamp());
    Console.WriteLine("RESULT: serve-guard NONE-THROWN");
}
catch (Exception ex)
{
    Console.WriteLine("RESULT: serve-guard " + ex.GetType().Name + ": " + ex.Message);
}

callbacks.Serve<IStamp>(mock.Object);

await using var cbHost = RemoteHost.At("http://cb:8080");
var store = await cbHost.GetAsync<IStore>();

Console.WriteLine("RESULT: stamp " + store.Stamp());
Console.WriteLine("RESULT: count " + await store.StampCountAsync());

store.StampPing();
Console.WriteLine("RESULT: ping ok");

try
{
    await store.StampFailAsync();
    Console.WriteLine("RESULT: fail-message NONE-THROWN");
}
catch (Exception ex)
{
    Console.WriteLine("RESULT: fail-message " + ex.Message);
}

// Verify works for every shape, because the calls really reached this mock.
mock.Verify(s => s.Value(), Times.Once);
mock.Verify(s => s.CountAsync(), Times.Once);
mock.Verify(s => s.Ping(), Times.Once);
mock.Verify(s => s.FailAsync(), Times.Once);
Console.WriteLine("RESULT: verify ok");

// I5: the callback listener binds 0.0.0.0, is unauthenticated, and runs inside
// the developer's OWN test process -- so it must expose only the methods of the
// interface it was asked to serve. Dispatching on target.GetType() made every
// public method of the served object reachable instead: a non-interface
// Secret() and Object's own ToString() were both verified callable.
//
// Its own port, so the container-facing host above is untouched. SecretStamp
// really does implement IStamp AND really does have a public Secret(): probing
// for a method that exists nowhere would pass even with the guard deleted.
await using var guarded = CallbackHost.Start(9091);
guarded.Serve<IStamp>(new SecretStamp());

using var probe = new HttpClient();

async Task<string> Probe(string methodName)
{
    var response = await probe.PostAsync("http://127.0.0.1:9091/callback",
        new StringContent($$"""{"interface":"CsLib.IStamp","method":"{{methodName}}","args":[]}""",
            Encoding.UTF8, "application/json"));

    return (await response.Content.ReadAsStringAsync()).Replace("\n", " ").Replace("\r", " ");
}

// Positive control FIRST: a guard that broke all dispatch would look identical
// to a guard that works if only the refusals were asserted.
Console.WriteLine("RESULT: guard-allowed " + await Probe("Value"));
Console.WriteLine("RESULT: guard-secret " + await Probe("Secret"));
Console.WriteLine("RESULT: guard-tostring " + await Probe("ToString"));

// C1's twin, in the callback direction: the container's Store calls back into
// this Moq mock for a ValueTask<T>, and the VALUE has to survive all three
// hops. Driven through a RAW /invoke rather than the typed client so the
// assertion can be made on the WHOLE ENVELOPE -- the broken payload carries
// the value as a NESTED field, so a substring grep passes against the very bug
// this exists to catch. (That is not hypothetical: it happened to the forward
// direction's first assertion, and was caught only by deleting the fix.)
using var invoker = new HttpClient();

async Task<string> InvokeRaw(string methodName)
{
    var response = await invoker.PostAsync("http://cb:8080/invoke",
        new StringContent($$"""{"service":"CsLib.Store","method":"{{methodName}}","args":[]}""",
            Encoding.UTF8, "application/json"));

    return (await response.Content.ReadAsStringAsync()).Replace("\n", " ").Replace("\r", " ");
}

Console.WriteLine("RESULT: cb-vt-value " + await InvokeRaw("StampVtValueAsync"));

// Read the flag immediately after the round trip returns -- see VtPingImpl.
await InvokeRaw("StampVtPingAsync");
Console.WriteLine("RESULT: cb-vt-pinged " + vtPinged);
