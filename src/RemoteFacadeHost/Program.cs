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

// Before the plugin is loaded, not after: a type initializer can P/Invoke on
// the very first touch, and the resolver has to be subscribed by then or that
// first load fails with nothing to catch it.
NativeResolver.Install(dir);

// Fail before serving. A host that starts without a usable graph would make
// every test using it fail confusingly at first call instead of at startup.
PluginLoader.LoadAssembly(dir, asmFile);

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
