using System.Runtime.Loader;
using RemoteFacadeHost;

// Fixed, not configurable. LIB_DIR and LIB_PORT were env vars that nothing
// ever set -- not this repo's suites, not RemoteFacade.Testcontainers, not any
// documented example -- and that could only do harm if they were. The image
// EXPOSEs 8080 and WithRemoteFacade connects to 8080, so a different LIB_PORT
// desynchronises the container from every consumer of it and from the /health
// wait strategy. LIB_DIR names a bind mount the image documents at exactly one
// path. A knob wired to nothing that breaks two things when turned is worse
// than no knob.
const string dir = "/plugin";
const string port = "8080";

var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");

// The startup is the only way to say what to serve. Without it the container
// would start and answer nothing, and the first call would fail with something
// that does not name the actual mistake.
if (string.IsNullOrWhiteSpace(registrar))
{
    throw new InvalidOperationException(
        "LIB_REGISTRAR is required: name the static registration method that " +
        "builds your service graph, as 'Namespace.TypeName.MethodName'.");
}

// v2 also accepted LIB_TYPE (host one class, constructed by this host) and
// LIB_OPTIONS (JSON bound onto that class's IOptions<T> parameters); v3 also
// accepted LIB_SERVICES (a JSON map of interface name to implementation name,
// applied with Replace after the startup ran); and v4 removed LIB_DIR and
// LIB_PORT above. Failing here rather than ignoring them is the point: a
// config carried forward unchanged would otherwise start cleanly and serve
// something that silently did not match what the operator asked for. For
// LIB_SERVICES that is the worst version of it -- a suite that believed it was
// running against a fake would run against the real dependency and still pass
// its health check.
//
// Version and remedy are carried PER VARIABLE. Telling an operator their
// config was removed in the wrong release sends them to the wrong migration
// notes, and the two v4 additions are not service wiring at all, so the advice
// that fits the other three would be actively misleading for them.
//
// The INERT value is the one that asks for exactly what it already gets, and
// it has to keep working. "{}" was LIB_OPTIONS's own default in v2 and
// LIB_SERVICES's in v3; "/plugin" and "8080" were LIB_DIR's and LIB_PORT's. So
// every harness and compose file in existence passes one of them as filler,
// and refusing those would reject configurations that change nothing -- the
// same over-strict guard this codebase already got wrong once and documented
// ("Only non-empty LIB_OPTIONS is fatal"). A value that actually diverges
// still fails, because it would otherwise be silently ignored.
var wiringRemedy =
    "Move this configuration into the startup named by LIB_REGISTRAR, which " +
    "registers services and configures their options in ordinary C#. A startup " +
    "can call another startup and then services.Replace(...) to keep real " +
    "wiring with one thing faked, choosing the lifetime itself.";

foreach (var (gone, removedIn, inert, remedy) in
         ((string Name, string Version, string Inert, string Remedy)[])
         [
             ("LIB_TYPE", "v3", "{}", wiringRemedy),
             ("LIB_OPTIONS", "v3", "{}", wiringRemedy),
             ("LIB_SERVICES", "v4", "{}", wiringRemedy),
             ("LIB_DIR", "v4", dir,
              $"The plugin directory is always {dir}; bind-mount your publish " +
              "output there instead of naming a different path."),
             ("LIB_PORT", "v4", port,
              $"The host always listens on {port}, which is the port the image " +
              "EXPOSEs and the port RemoteFacade.Testcontainers connects to. " +
              "Map it to whatever you like with Docker's own -p."),
         ])
{
    var value = Environment.GetEnvironmentVariable(gone)?.Trim();

    if (!string.IsNullOrEmpty(value) && value != inert)
    {
        throw new InvalidOperationException(
            $"{gone} was removed in {removedIn} and is no longer read, but is " +
            $"set to '{value}'. {remedy}");
    }
}

// Before constructing anything: the library may open paths on the share in its
// constructor.
ShareMounter.MountIfConfigured();

// Every resolution decision below reads the plugin's own deps.json, so its
// absence is settled here -- before anything has been loaded and while the
// message can still name the real problem. See PluginLoader for what loading
// without it silently does instead.
PluginLoader.RequireDependencyFile(dir, asmFile);

// Constructed from the ASSEMBLY path, not the deps.json path just validated:
// AssemblyDependencyResolver takes "the path to the component's managed entry
// point" and derives the deps.json name from it. Handing it the deps.json
// instead makes it look for <name>.deps.json.deps.json, find nothing, and
// resolve nothing -- silently, because resolving nothing is also what it does
// for a library that genuinely declares no assets. Caught by the native case
// in test/run.sh that runs with no LD_LIBRARY_PATH; every other case was
// covered by the entrypoint script's search path and stayed green.
var deps = new AssemblyDependencyResolver(Path.Combine(dir, asmFile));

// Fail before serving. A host that starts without a usable graph would make
// every test using it fail confusingly at first call instead of at startup.
PluginLoader.LoadAssembly(dir, asmFile, deps);

// One graph serves every call for the container's lifetime; that is what
// allows a method to acquire a resource and a later call to release it.
// InstanceHolder owns it so DELETE /instance can reset it between tests.
//
// Built HERE, eagerly, rather than resolved lazily from DI on first request:
// InstanceHolder runs the factory in its own initializer, so constructing it
// now is what makes a broken registrar fail the CONTAINER at startup instead
// of failing whichever call happens to arrive first.
var holder = new InstanceHolder(() => Activation.Build(registrar));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// PluginLoader.TypeNames is passed as the method group, not its result: see
// ServedPlugin for why GET /types has to stay deferred.
builder.Services.AddFacade(holder, new ServedPlugin(registrar, PluginLoader.TypeNames));

var app = builder.Build();

app.MapFacade();

app.Run();
