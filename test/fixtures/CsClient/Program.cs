using CsLib;
using RemoteFacadeHost.Client;

// The "test process". It references CsLib only for IStore; the real Store runs
// in the container.
IStore store = RemoteFacade.For<IStore>("http://cs:8080");

await store.WriteAsync("a.txt", "hello");
Console.WriteLine("RESULT: async-void ok");

Console.WriteLine("RESULT: async-value " + await store.ReadAsync("a.txt"));

store.Touch("b.txt");
Console.WriteLine("RESULT: sync-void ok");

Console.WriteLine("RESULT: sync-value " + store.Count());

// D1 regression check: IStore.PolyReturn() is declared to return the BASE
// type (PolyBase), but the container hands back a PolyDerived. The full
// round trip -- Invoker serializing it, this client deserializing the
// response back into PolyBase -- must keep the concrete type, not silently
// downgrade to PolyBase with Extra dropped.
var poly = store.PolyReturn();
Console.WriteLine("RESULT: poly-type " + poly.GetType().Name);

// C1: ValueTask and ValueTask<T>. Both are STRUCTS, so a host testing only
// `result is Task` never awaits them and serializes the awaitable itself as
// data; the client then deserializes {"isCompleted":true,...} into a DEFAULT
// ValueTask<string> and awaits null -- HTTP 200, ok:true, no error anywhere.
// Assert the VALUE that arrives, never merely that the call succeeded: an
// ok:true assertion passes against that null.
await store.VtVoidAsync("vt.txt");
// Reading the file back is what proves the ValueTask was actually AWAITED and
// not just fired: VtVoidAsync writes only after a delay, so a host that
// returns without awaiting answers before this file exists.
Console.WriteLine("RESULT: vt-void " + await store.ReadAsync("vt.txt"));
Console.WriteLine("RESULT: vt-value " + await store.VtValueAsync());

// I1: a client pointed at a reachable-but-wrong endpoint must name the
// interface, the method, the URL and the status -- not fail while parsing the
// error page, as "'<' is an invalid start of a value", which names none of
// them. CallbackHost serves /callback only, so /invoke on it is a real 404
// from a real listener: no stub server needed, and the failure arrives by
// exactly the route a mistyped base URL would take.
await using var wrongEndpoint = CallbackHost.Start(9099);
var misdirected = RemoteFacade.For<IStore>("http://127.0.0.1:9099");

try
{
    misdirected.Count();
    Console.WriteLine("RESULT: status-guard NONE-THROWN");
}
catch (Exception ex)
{
    Console.WriteLine("RESULT: status-guard " + ex.Message.Replace("\n", " ").Replace("\r", " "));
}
