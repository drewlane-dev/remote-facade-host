using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The rejection messages a composition-root mistake produces. Successful
/// resolution needs PluginLoader.Assembly, which only a real loaded plugin
/// sets, so it stays with the container suite -- faking it here would test
/// the fake.
/// </summary>
public class HostedGraphTests
{
    private static HostedGraph Graph(params string[] scoped) =>
        new(new ServiceCollection().BuildServiceProvider(), null, false,
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
    public void A_root_the_provider_owns_is_not_disposed_twice()
    {
        // Double disposal is made impossible rather than merely harmless:
        // nothing guarantees a third-party type's Dispose() is idempotent.
        // Registered BY TYPE, not as an instance. A container does not dispose
        // an object it did not create, so AddSingleton(instance) would leave
        // this at zero and the test would pass while proving nothing.
        var services = new ServiceCollection();
        services.AddSingleton<CountingRoot>();
        var provider = services.BuildServiceProvider();
        var root = provider.GetRequiredService<CountingRoot>();

        new HostedGraph(provider, root, rootOwnedByProvider: true, [], new HashSet<string>()).Dispose();

        // Exactly one: the provider disposes it, and HostedGraph deliberately
        // skips it. Drop that skip and this reads 2.
        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public void A_root_the_provider_does_NOT_own_is_disposed_by_the_graph()
    {
        // Built by ActivatorUtilities, never handed to the container, so
        // nothing else tracks it. v1.0 leaked one of these per reset.
        var root = new CountingRoot();

        new HostedGraph(new ServiceCollection().BuildServiceProvider(), root, false, [], new HashSet<string>())
            .Dispose();

        Assert.Equal(1, root.Disposals);
    }

    [Fact]
    public void A_root_whose_Dispose_throws_still_lets_the_provider_dispose()
    {
        // Measured once: a throwing root took the provider down with it, and
        // every singleton the graph held leaked while the host kept reporting
        // itself healthy.
        var services = new ServiceCollection();
        services.AddSingleton<CountingRoot>();
        var provider = services.BuildServiceProvider();
        var singleton = provider.GetRequiredService<CountingRoot>();

        // The throwing root is NOT provider-owned, so HostedGraph disposes it
        // itself -- which is the path where the throw could escape and skip
        // the provider.
        var root = new CountingRoot { ThrowOnDispose = true };
        var graph = new HostedGraph(provider, root, false, [], new HashSet<string>());

        Assert.Throws<InvalidOperationException>(graph.Dispose);
        Assert.Equal(1, singleton.Disposals);
    }
}
