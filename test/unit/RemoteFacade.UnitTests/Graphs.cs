using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// A singleton whose disposal is observable, and countable. Counting rather
/// than flagging is the point: "disposed exactly once" is the property
/// InstanceHolder exists to guarantee, and a bool cannot tell one disposal
/// from three.
/// </summary>
public sealed class Counted : IDisposable
{
    private int _disposals;
    public int Disposals => Volatile.Read(ref _disposals);
    public bool ThrowOnDispose { get; set; }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposals);
        if (ThrowOnDispose) throw new InvalidOperationException("plugin dispose blew up");
    }
}

public static class Graphs
{
    /// <summary>
    /// A HostedGraph around a real ServiceProvider holding one tracked
    /// singleton, plus a name so a test can tell WHICH graph it is looking at.
    ///
    /// v3 removed HostedGraph.Root, so identity comes from ServiceNames now.
    /// The singleton is resolved immediately and deliberately: a provider
    /// disposes only what it CREATED, so one that was registered but never
    /// asked for would never be disposed and every disposal assertion here
    /// would read zero while the code under test behaved perfectly.
    /// </summary>
    public static HostedGraph Named(string name, Counted tracked)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => tracked);
        var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<Counted>();

        return new HostedGraph(provider, [name], new HashSet<string>());
    }
}
