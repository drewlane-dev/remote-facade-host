using CsLib;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// WithSmbMount, against a real Samba server.
///
/// This is the extension most worth having and the one least verifiable by
/// reading: whether a cifs mount succeeds depends on a capability set and an
/// AppArmor option whose absence produces a mount error that mentions neither.
/// The only way to know it is right is to mount something.
///
/// It is also the property the whole image exists for -- two real SMB clients
/// contending over one share -- expressed the way a consumer would write it.
/// </summary>
public class SmbMountTests : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _samba = null!;

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _samba = new ContainerBuilder()
            .WithImage("ghcr.io/drewlane-dev/azure-files-emulator:1")
            .WithNetwork(_network)
            .WithNetworkAliases("samba")
            .WithEnvironment("SMB_USER", "azure")
            .WithEnvironment("SMB_PASS", "Passw0rd!")
            .WithEnvironment("SMB_UID", "0")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(445))
            .Build();

        await _samba.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_samba is not null) await _samba.DisposeAsync();
        if (_network is not null) await _network.DeleteAsync();
    }

    private IContainer Instance() =>
        new ContainerBuilder()
            .WithImage(HostFixture.Image)
            .WithRemoteFacade(typeof(StoreStartup), HostFixture.PluginDir)
            .WithNetwork(_network)
            .WithEnvironment("STORE_ROOT", "/mnt/share")
            .WithEnvironment("LIB_SERVICES", """{"CsLib.IStamp":"CsLib.RealStamp"}""")
            .WithEnvironment("DOTNET_EnableDiagnostics", "0")
            .WithSmbMount(new SmbMount
            {
                Server = "samba",
                Share = "data",
                // Reached over the container network, not the Docker host.
                AddHostGateway = false,
            })
            .Build();

    [Fact]
    public async Task Two_instances_see_each_other_s_writes_on_one_share()
    {
        await using var a = Instance();
        await using var b = Instance();

        // Both must become healthy, and WithRemoteFacade waits on /health --
        // so reaching this line at all is already evidence the cifs mount
        // succeeded. The host refuses to start when a configured mount fails.
        await Task.WhenAll(a.StartAsync(), b.StartAsync());

        await using var hostA = a.RemoteHost();
        await using var hostB = b.RemoteHost();

        var storeA = await hostA.GetAsync<IStore>();
        var storeB = await hostB.GetAsync<IStore>();

        await storeA.WriteAsync("shared.txt", "written-by-a");

        // The assertion that matters: B is a SEPARATE SMB client in a separate
        // container, so reading A's content proves the bytes went through the
        // server rather than a shared local filesystem.
        Assert.Equal("written-by-a", await storeB.ReadAsync("shared.txt"));
    }
}
