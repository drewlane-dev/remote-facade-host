using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// One container per test class, started from the image under test with the
/// CsLib composition root mounted.
///
/// This is the same path a consumer walks -- Testcontainers, the real client
/// package, a real image -- rather than the shell suite's curl. The two are
/// complementary: run.sh pins the wire format, this pins that the CLIENT
/// LIBRARY can actually drive it.
/// </summary>
public sealed class HostFixture : IAsyncLifetime
{
    /// <summary>
    /// Overridable so CI can point at the tag it just built. Defaults to the
    /// local dev tag rather than a published one: a test that silently fell
    /// back to :latest would validate a different artifact than the one the
    /// pipeline is about to ship.
    /// </summary>
    public static string Image =>
        Environment.GetEnvironmentVariable("REMOTE_FACADE_IMAGE") ?? "remote-facade-host:dev";

    private IContainer _container = null!;

    public string Url { get; private set; } = "";

    /// <summary>
    /// The published CsLib directory. Located by walking up to the repo root
    /// rather than by a relative path from the test binary, which changes with
    /// configuration and TFM.
    /// </summary>
    public static string PluginDir
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, "test", "publish", "cslib");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "test/publish/cslib not found above " + AppContext.BaseDirectory +
                ". Run: dotnet publish test/fixtures/CsLib/CsLib.csproj -c Release -o test/publish/cslib");
        }
    }

    public static ContainerBuilder Builder(string pluginDir) =>
        new ContainerBuilder()
            .WithImage(Image)
            .WithBindMount(pluginDir, "/plugin", AccessMode.ReadOnly)
            .WithEnvironment("LIB_DIR", "/plugin")
            // Derived from the TYPE, not typed as strings. This is the helper
            // under test as much as it is setup: a wrong LIB_REGISTRAR would
            // fail at container start, and deriving it means the compiler
            // catches a rename.
            .WithEnvironment(new Dictionary<string, string>(RemoteHostEnvironment.For(typeof(CsLib.GraphStartup))))
            .WithEnvironment("LIB_OPTIONS", "{}")
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            .WithPortBinding(8080, true)
            // /health, not just an open port: the host binds before the graph
            // is built, so a port check can hand back a container that is not
            // yet able to serve anything.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPath("/health").ForPort(8080)));

    public async ValueTask InitializeAsync()
    {
        _container = Builder(PluginDir).Build();
        await _container.StartAsync();
        Url = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(8080)}";
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null) await _container.DisposeAsync();
    }
}
