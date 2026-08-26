using CsLib;
using DotNet.Testcontainers.Builders;
using RemoteFacadeHost.Client;

namespace RemoteFacade.IntegrationTests;

/// <summary>
/// The composition-root path end to end: a startup registers a graph inside
/// the container, and the test drives it through the real client package.
/// </summary>
public class CompositionPathTests(HostFixture fixture) : IClassFixture<HostFixture>
{
    [Fact]
    public async Task A_registered_facade_is_reachable_through_the_typed_client()
    {
        await using var host = RemoteHost.At(fixture.Url);

        var facade = await host.GetAsync<IRootFacade>();

        // A value the container computed, not a default: proves the graph was
        // built and the call reached a real instance.
        Assert.Equal("root-facade", facade.Who());
    }

    [Fact]
    public async Task A_method_inherited_from_a_BASE_interface_is_part_of_the_served_contract()
    {
        // Type.GetMethods() on an interface returns only what that interface
        // itself declares, so FromBase() was once unreachable while
        // FromDerived() worked.
        await using var host = RemoteHost.At(fixture.Url);

        var facade = await host.GetAsync<IDerivedFacade>();

        Assert.Equal("base-method", facade.FromBase());
        Assert.Equal("derived-method", facade.FromDerived());
    }

    [Fact]
    public async Task ResetAsync_rebuilds_the_graph_and_existing_proxies_keep_working()
    {
        await using var host = RemoteHost.At(fixture.Url);
        var counter = await host.GetAsync<ICounter>();

        Assert.Equal(1, counter.Next());
        Assert.Equal(2, counter.Next());

        await host.ResetAsync();

        // The SAME proxy object, deliberately: the host resolves per call from
        // whatever the current graph is, which is what lets a reset rebuild
        // state without invalidating anything the test is holding.
        Assert.Equal(1, counter.Next());
    }

    [Fact]
    public async Task A_Scoped_registration_resolves_as_a_service_but_is_refused_at_the_CALL()
    {
        await using var host = RemoteHost.At(fixture.Url);

        // GetAsync only consults the registration list, and IScopedThing IS
        // registered -- so this must succeed.
        var scoped = await host.GetAsync<IScopedThing>();

        // The rejection lives where the resolution happens: per call.
        var ex = Assert.Throws<InvalidOperationException>(() => scoped.Say());

        Assert.Contains(nameof(IScopedThing), ex.Message);
        Assert.Contains("Scoped", ex.Message);
    }

    [Fact]
    public async Task Asking_for_something_the_startup_never_registered_fails_with_the_list()
    {
        await using var host = RemoteHost.At(fixture.Url);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => host.GetAsync<IStore>());

        // Naming what IS registered is the difference between a two-minute fix
        // and a hunt through three candidate causes.
        Assert.Contains(nameof(IStore), ex.Message);
    }
}

/// <summary>
/// The property the whole image exists for: several real instances, each in
/// its own container, driven at once from one test.
/// </summary>
public class TwoInstanceTests
{
    [Fact]
    public async Task Each_container_gets_its_OWN_graph()
    {
        var plugin = HostFixture.PluginDir;
        await using var a = HostFixture.Builder(plugin).Build();
        await using var b = HostFixture.Builder(plugin).Build();

        await Task.WhenAll(a.StartAsync(), b.StartAsync());

        await using var hostA = RemoteHost.At($"http://{a.Hostname}:{a.GetMappedPublicPort(8080)}");
        await using var hostB = RemoteHost.At($"http://{b.Hostname}:{b.GetMappedPublicPort(8080)}");

        var counterA = await hostA.GetAsync<ICounter>();
        var counterB = await hostB.GetAsync<ICounter>();

        counterA.Next();
        counterA.Next();

        // Two separate processes, so B's singleton has never been touched. If
        // the two shared a graph this would read 3.
        Assert.Equal(3, counterA.Next());
        Assert.Equal(1, counterB.Next());
    }
}
