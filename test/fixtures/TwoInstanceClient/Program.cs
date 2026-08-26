using CsLib;
using RemoteFacadeHost.Client;

// Two REAL instances, each in its own container with its own SMB mount and its
// own SMB session. This is what the image exists for.
await using var hostA = RemoteHost.At("http://ia:8080");
await using var hostB = RemoteHost.At("http://ib:8080");
var a = await hostA.GetAsync<IStore>();
var b = await hostB.GetAsync<IStore>();

await a.WriteAsync("shared.txt", "written-by-a");

// B reads it back over a DIFFERENT mount and a different session.
Console.WriteLine("RESULT: b-read " + await b.ReadAsync("shared.txt"));

// I2: the reason this image exists is several REAL instances driven
// CONCURRENTLY against shared state. A blocking client proxy turns
// Task.WhenAll(a.X(), b.X()) into two sequential calls, and a contention test
// then passes while overlapping nothing at all.
//
// Asserted on server-side truth, not on wall clock: each container reports the
// UTC tick window over which its own call actually ran, and the two windows
// must INTERSECT. A blocking proxy cannot even begin B's call until A's has
// returned, so its two windows are disjoint BY CONSTRUCTION -- there is no
// timing tolerance to tune and no slow-machine flake, in either direction.
var callA = a.SleepWindowAsync(2000);
var callB = b.SleepWindowAsync(2000);
var reported = await Task.WhenAll(callA, callB);

static (long Start, long End) Window(string value)
{
    var parts = value.Split(':');
    return (long.Parse(parts[0]), long.Parse(parts[1]));
}

var windowA = Window(reported[0]);
var windowB = Window(reported[1]);
var overlapped = windowA.Start < windowB.End && windowB.Start < windowA.End;

Console.WriteLine($"RESULT: overlap {(overlapped ? "yes" : "no")} " +
    $"a=[{windowA.Start},{windowA.End}] b=[{windowB.Start},{windowB.End}]");
