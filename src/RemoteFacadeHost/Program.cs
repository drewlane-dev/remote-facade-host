using RemoteFacadeHost;

var dir      = Environment.GetEnvironmentVariable("LIB_DIR") ?? "/plugin";
var asmFile  = Environment.GetEnvironmentVariable("LIB_ASSEMBLY")
               ?? throw new InvalidOperationException("LIB_ASSEMBLY is required");
var servicesJson = Environment.GetEnvironmentVariable("LIB_SERVICES") ?? "{}";
var registrar = Environment.GetEnvironmentVariable("LIB_REGISTRAR");
var port     = Environment.GetEnvironmentVariable("LIB_PORT") ?? "8080";

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
// LIB_OPTIONS (JSON bound onto that class's IOptions<T> parameters). Both were
// removed in v3. Failing here rather than ignoring them is the point: a config
// carried forward unchanged would otherwise start cleanly and serve a graph
// that silently did not include what the operator asked for.
// "{}" does not count as set. It was LIB_OPTIONS's own default in v2, so
// every harness and compose file in existence passes it as inert filler --
// refusing it would reject configurations that ask for nothing at all, which
// is the same over-strict guard this codebase already got wrong once and
// documented ("Only non-empty LIB_OPTIONS is fatal"). A value that actually
// carries configuration still fails, because that configuration would
// otherwise be silently dropped.
foreach (var gone in (string[])["LIB_TYPE", "LIB_OPTIONS"])
{
    var value = Environment.GetEnvironmentVariable(gone)?.Trim();

    if (!string.IsNullOrEmpty(value) && value != "{}")
    {
        throw new InvalidOperationException(
            $"{gone} was removed in v3 and is no longer read, but is set to " +
            $"'{value}'. Move this configuration into the startup named by " +
            "LIB_REGISTRAR, which registers services and configures their " +
            "options in ordinary C#.");
    }
}

// Before constructing anything: the library may open paths on the share in its
// constructor.
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
var holder = new InstanceHolder(() => Activation.Build(registrar, servicesJson));

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// PluginLoader.TypeNames is passed as the method group, not its result: see
// ServedPlugin for why GET /types has to stay deferred.
builder.Services.AddFacade(holder, new ServedPlugin(registrar, PluginLoader.TypeNames));

var app = builder.Build();

app.MapFacade();

app.Run();
