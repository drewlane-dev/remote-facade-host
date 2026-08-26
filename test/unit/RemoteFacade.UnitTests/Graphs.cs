using Microsoft.Extensions.DependencyInjection;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// A root whose disposal is observable, and countable. Counting rather than
/// flagging is the point: "disposed exactly once" is the property
/// InstanceHolder exists to guarantee, and a bool cannot tell one disposal
/// from three.
/// </summary>
public sealed class CountingRoot : IDisposable
{
    private int _disposals;
    public int Disposals => Volatile.Read(ref _disposals);
    public bool ThrowOnDispose { get; init; }

    public void Dispose()
    {
        Interlocked.Increment(ref _disposals);
        if (ThrowOnDispose) throw new InvalidOperationException("root dispose blew up");
    }
}

public static class Graphs
{
    /// <summary>
    /// A HostedGraph around a real ServiceProvider. Real, not a fake: the
    /// disposal ordering these tests assert is the provider's own behaviour,
    /// and a stand-in would only prove the stand-in works.
    /// </summary>
    public static HostedGraph Around(object? root, bool ownedByProvider = false) =>
        new(new ServiceCollection().BuildServiceProvider(),
            root, ownedByProvider, [], new HashSet<string>());
}
