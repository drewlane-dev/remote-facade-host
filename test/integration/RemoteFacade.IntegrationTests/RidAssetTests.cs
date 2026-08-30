using CsLib;
using DotNet.Testcontainers.Builders;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// A plugin's RID-specific MANAGED assemblies must win over the copy in the
/// plugin root.
///
/// Packages that ship per-platform builds put a reference assembly at the root
/// and the working one under runtimes/&lt;rid&gt;/lib. Normally the runtime
/// chooses between them from the app's deps.json, but a plugin loaded with
/// Assembly.LoadFrom gets none of that -- the HOST's deps.json governs and has
/// never heard of the plugin's packages.
///
/// Microsoft.Data.SqlClient is the case that found this: its root assembly is a
/// stub whose members throw "not supported on this platform". Constructing a
/// SqlConnection is therefore a complete test on its own, with no database
/// anywhere near it.
/// </summary>
public class RidAssetTests
{
    [Fact]
    public async Task A_plugin_gets_the_RID_specific_assembly_not_the_root_stub()
    {
        await using var container = new ContainerBuilder()
            .WithImage(HostFixture.Image)
            .WithRemoteFacade(typeof(SqlProbeStartup), HostFixture.PluginDir)
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            .Build();

        await container.StartAsync();

        await using var host = container.RemoteHost();
        var probe = await host.GetAsync<ISqlProbe>();

        // Throws before returning anything if the stub was loaded, so reaching
        // the assertion at all is already most of the proof.
        var described = probe.Describe();

        Assert.Contains("runtimes/", described);
        Assert.Contains("/lib/", described);
    }
}
