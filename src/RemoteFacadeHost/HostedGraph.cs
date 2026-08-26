using Microsoft.Extensions.DependencyInjection;

namespace RemoteFacadeHost;

/// <summary>
/// Owns the provider built from the plugin's composition root, plus the root
/// instance when one is configured.
///
/// This exists because the provider has to OUTLIVE construction. v1.0 built a
/// provider, constructed one object from it, and dropped it — which is why a
/// registrar could register a service that nothing could ever reach.
/// </summary>
public sealed class HostedGraph(
    ServiceProvider provider, object? root, bool rootOwnedByProvider, IReadOnlyList<string> serviceNames,
    IReadOnlySet<string> scopedNames)
    : IDisposable
{
    /// <summary>The LIB_TYPE instance, or null in composition-root mode.</summary>
    public object? Root { get; } = root;

    /// <summary>Full names of every service type the startup registered.</summary>
    public IReadOnlyList<string> ServiceNames { get; } = serviceNames;

    /// <summary>
    /// Resolves a service by full type name, returning the TYPE it found
    /// alongside the instance -- the same type this method already looked up
    /// internally to resolve it. Handing it back means a caller dispatching
    /// against the result (Program.cs's /invoke) never has to re-derive it
    /// with a second, independent lookup.
    ///
    /// A miss lists what IS registered: a name that does not match is the most
    /// likely mistake in composition-root mode, and "not found" alone leaves
    /// the caller guessing between a typo, a missing registration, and the
    /// wrong assembly.
    /// </summary>
    public (object Instance, Type Type) Resolve(string serviceName)
    {
        if (scopedNames.Contains(serviceName))
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is registered Scoped, and a remote " +
                "call has no scope to live in. Register it Singleton or " +
                "Transient, or resolve it inside a method on a service that is.");
        }

        var type = PluginLoader.Assembly?.GetType(serviceName)
                   ?? Type.GetType(serviceName);

        if (type is null)
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is not a type in the plugin assembly. " +
                $"Registered services: {string.Join(", ", ServiceNames)}");
        }

        var resolved = provider.GetService(type);

        if (resolved is null)
        {
            throw new InvalidOperationException(
                $"service '{serviceName}' is not registered. " +
                $"Registered services: {string.Join(", ", ServiceNames)}");
        }

        return (resolved, type);
    }

    /// <summary>
    /// Disposes the root, then the provider.
    ///
    /// v1.0 disposed an IDisposable root on every reset; the container does
    /// NOT do this for you when the root was built by ActivatorUtilities --
    /// that instance was never handed to the container, so nothing tracks it.
    /// A root holding a file lock would otherwise leak it on every
    /// DELETE /instance, which is exactly the cross-test poisoning Reset
    /// exists to prevent.
    ///
    /// Double-dispose is made IMPOSSIBLE, not merely harmless: a root
    /// resolved FROM the provider (rootOwnedByProvider) is already tracked by
    /// it and will be disposed below, when the provider is, so it is
    /// deliberately skipped here. Disposing it in both places would work only
    /// if the plugin's Dispose() happens to be idempotent, and nothing
    /// guarantees that of a third-party type.
    ///
    /// The root's disposal is wrapped in try/finally so the provider is
    /// ALWAYS disposed. Without it, a root whose Dispose() throws took the
    /// whole provider down with it -- measured: the root's sentinel was
    /// written, a registered IDisposable singleton was NEVER disposed, and
    /// the host kept reporting itself healthy. That is not a provider leak,
    /// it is a leak of every singleton the graph holds, which defeats the
    /// guarantee DELETE /instance exists to provide. The root's exception
    /// still propagates, so Reset()'s documented 500-to-the-operator (and
    /// Release()'s swallow-and-log on the deferred path) are unchanged.
    ///
    /// The provider is disposed ASYNCHRONOUSLY. ServiceProvider.Dispose()
    /// (the synchronous one) THROWS for any tracked singleton implementing
    /// only IAsyncDisposable -- "'X' type only implements IAsyncDisposable.
    /// Use DisposeAsync to dispose the container." Measured end to end: once
    /// such a service had been resolved, EVERY DELETE /instance returned 500
    /// and disposal aborted part-way through, leaving the singletons after it
    /// in the list alive. v1.0 could not hit this because it never disposed
    /// the provider at all, so it is a real regression the byte-baseline
    /// cannot see (the baseline never issues a DELETE). ServiceProvider
    /// implements IAsyncDisposable, and DisposeAsync() handles BOTH
    /// IDisposable and IAsyncDisposable singletons, so it is strictly the
    /// wider of the two; the IDisposable branch below is the fallback for any
    /// provider type that does not offer it. Blocking on it is safe here:
    /// ASP.NET Core installs no SynchronizationContext, so there is no
    /// context for the continuation to deadlock waiting on, and this already
    /// runs on a request thread (Reset) or a completed call's continuation
    /// (Release), neither of which the disposal needs back.
    ///
    /// The root gets the same treatment for the same reason: an
    /// IAsyncDisposable-ONLY root that the provider does not own was
    /// previously disposed by nobody at all.
    /// </summary>
    public void Dispose()
    {
        try
        {
            if (!rootOwnedByProvider)
            {
                switch (root)
                {
                    case IDisposable disposableRoot:
                        disposableRoot.Dispose();
                        break;
                    case IAsyncDisposable asyncDisposableRoot:
                        asyncDisposableRoot.DisposeAsync().AsTask().GetAwaiter().GetResult();
                        break;
                }
            }
        }
        finally
        {
            if (provider is IAsyncDisposable asyncProvider)
            {
                asyncProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                provider.Dispose();
            }
        }
    }
}
