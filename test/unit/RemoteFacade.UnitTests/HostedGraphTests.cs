using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The rejection messages a composition-root mistake produces, and the
/// disposal guarantee.
///
/// Successful resolution needs PluginLoader.Assembly, which only a real loaded
/// plugin sets, so it stays with the container suite -- faking it here would
/// test the fake.
/// </summary>
public class HostedGraphTests
{
    private static HostedGraph Graph(params string[] scoped) =>
        new(new ServiceCollection().BuildServiceProvider(),
            ["Some.IThing", "Some.IOther"], new HashSet<string>(scoped));

    [Fact]
    public void A_scoped_service_is_refused_with_the_reason_and_the_remedy()
    {
        using var graph = Graph("Some.IScoped");

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Resolve("Some.IScoped"));

        Assert.Contains("Some.IScoped", ex.Message);
        Assert.Contains("Scoped", ex.Message);
        Assert.Contains("Singleton or", ex.Message);
    }

    [Fact]
    public void An_unknown_service_name_lists_what_IS_registered()
    {
        // "not found" alone leaves the reader guessing between a typo, a
        // missing registration and the wrong assembly.
        using var graph = Graph();

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Resolve("Nope.Missing"));

        Assert.Contains("Nope.Missing", ex.Message);
        Assert.Contains("Some.IThing", ex.Message);
        Assert.Contains("Some.IOther", ex.Message);
    }

    [Fact]
    public void The_scoped_check_runs_before_the_type_lookup()
    {
        // A scoped name that is also not a loadable type must report the
        // SCOPED problem: that is the one the caller can act on, and the type
        // lookup failing is a consequence of the same mistake.
        using var graph = Graph("Definitely.Not.A.Type");

        var ex = Assert.Throws<InvalidOperationException>(() => graph.Resolve("Definitely.Not.A.Type"));

        Assert.Contains("Scoped", ex.Message);
    }

    [Fact]
    public void Disposing_the_graph_disposes_the_singletons_the_provider_created()
    {
        // v3's whole disposal story. With LIB_TYPE gone there is no root the
        // container did not build, so .NET's own rule -- the provider disposes
        // what it created -- covers everything, and HostedGraph no longer needs
        // a separate branch that had to avoid double-disposing.
        var tracked = new Counted();
        var services = new ServiceCollection();
        services.AddSingleton(_ => tracked);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<Counted>();

        new HostedGraph(provider, [], new HashSet<string>()).Dispose();

        Assert.Equal(1, tracked.Disposals);
    }

    [Fact]
    public void A_singleton_that_was_never_resolved_is_never_disposed()
    {
        // Registered is not created. This is not a defect to fix -- it is the
        // container's own rule -- but it IS the trap that made two container
        // cases read "disposed 0 times" for a host behaving correctly, so it
        // is pinned here rather than rediscovered.
        var tracked = new Counted();
        var services = new ServiceCollection();
        services.AddSingleton(_ => tracked);

        new HostedGraph(services.BuildServiceProvider(), [], new HashSet<string>()).Dispose();

        Assert.Equal(0, tracked.Disposals);
    }
}
