using System.Reflection;
using CsLib;
using RemoteFacadeHost.Client;

// Review finding 1 (Critical): RemoteHost's constructor must be PRIVATE, so
// At(string) is the only supported way to build one -- a public primary
// constructor would be a second, undocumented entry point that bypasses it.
// Nothing about USING RemoteHost normally would ever fail if this regressed
// (a public constructor compiles and runs identically to a private one from
// the caller's side), so reflection is the only way this suite can pin the
// shape rather than the behavior.
var publicCtors = typeof(RemoteHost).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
Console.WriteLine("RESULT: ctor-private " + (publicCtors.Length == 0 ? "yes" : "no"));

// Review round 2 finding: RemoteHostEnvironment.For<TStartup>() (a generic
// type parameter) could never be called with an actual startup, because a
// startup is a "static class" holder for a static Configure method and C#
// forbids a static type as a generic type argument (CS0718). Pinned here
// against CsLib.GraphStartup -- a REAL "public static class" startup, the
// exact shape every other composition-root example in this suite uses --
// with typeof(), which does accept it.
var env = RemoteHostEnvironment.For(typeof(GraphStartup));
Console.WriteLine("RESULT: env-assembly " + env["LIB_ASSEMBLY"]);
Console.WriteLine("RESULT: env-registrar " + env["LIB_REGISTRAR"]);

await using var host = RemoteHost.At("http://croot:8080");

var facade = await host.GetAsync<IRootFacade>();
Console.WriteLine("RESULT: who " + facade.Who());

var counter = await host.GetAsync<ICounter>();
counter.Next();
await host.ResetAsync();
Console.WriteLine("RESULT: after-reset " + counter.Next());

// IScopedThing IS registered (GraphStartup.Configure adds it AddScoped), so
// GetAsync's "is it registered" check -- which only inspects GET /services --
// passes. The rejection lives in HostedGraph.Resolve, which /invoke consults
// per call, so it only fires on the first actual METHOD call against the
// proxy, not on GetAsync itself.
var scoped = await host.GetAsync<IScopedThing>();
Console.WriteLine("RESULT: scoped-get ok");

try
{
    scoped.Say();
    Console.WriteLine("RESULT: scoped-call NONE-THROWN");
}
catch (Exception ex)
{
    Console.WriteLine("RESULT: scoped-call " + ex.Message.Replace("\n", " ").Replace("\r", " "));
}
