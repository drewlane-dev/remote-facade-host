using System.Runtime.Loader;
using RemoteFacadeHost;

var dir      = Environment.GetEnvironmentVariable("LIB_DIR") ?? "/plugin";
var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");
var port     = Environment.GetEnvironmentVariable("LIB_PORT") ?? "8080";

// registrar is used to wireup the service graph, so it is required.
if (string.IsNullOrWhiteSpace(registrar))
{
    throw new InvalidOperationException(
        "LIB_REGISTRAR is required: name the static registration method that " +
        "builds your service graph, as 'Namespace.TypeName.MethodName'.");
}

// throw on deprecated property usage
foreach (var (gone, removedIn) in ((string Name, string Version)[])
         [("LIB_TYPE", "v3"), ("LIB_OPTIONS", "v3"), ("LIB_SERVICES", "v4")])
{
    var value = Environment.GetEnvironmentVariable(gone)?.Trim();

    if (!string.IsNullOrEmpty(value) && value != "{}")
    {
        throw new InvalidOperationException(
            $"{gone} was removed in {removedIn} and is no longer read, but is " +
            $"set to '{value}'. Move this configuration into the startup named " +
            "by LIB_REGISTRAR, which registers services and configures their " +
            "options in ordinary C#. A startup can call another startup and " +
            "then services.Replace(...) to keep real wiring with one thing " +
            "faked, choosing the lifetime itself.");
    }
}

// wire up a cifs mount when the env vars are set
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
