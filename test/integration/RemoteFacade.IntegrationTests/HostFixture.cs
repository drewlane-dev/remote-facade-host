using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// One container per test class, started from the image under test with the
/// CsLib composition root mounted.
///
/// This is also where RemoteFacade.Testcontainers is dogfooded: the fixture
/// uses the same extensions a consumer would, so a regression in them fails
/// this suite rather than being discovered by whoever installs the package.
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

    public RemoteHost Host { get; private set; } = null!;

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

    /// <summary>
    /// Everything a facade container needs, in one call. Compare against what
    /// this replaced: a bind mount, three environment variables, a port
    /// binding and a wait strategy, hand-written per fixture.
    /// </summary>
    public static ContainerBuilder Builder(string pluginDir) =>
        new ContainerBuilder()
            .WithImage(Image)
            .WithRemoteFacade(typeof(CsLib.GraphStartup), pluginDir)
            .WithEnvironment("DOTNET_EnableDiagnostics", "0");

    public async ValueTask InitializeAsync()
    {
        _container = Builder(PluginDir).Build();
        await _container.StartAsync();
        Host = _container.RemoteHost();
    }

    public async ValueTask DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        if (_container is not null) await _container.DisposeAsync();
    }
}
