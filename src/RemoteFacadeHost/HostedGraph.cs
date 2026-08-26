using Microsoft.Extensions.DependencyInjection;

namespace RemoteFacadeHost;

/// <summary>
/// Owns the provider built from the plugin's composition root.
///
/// The provider has to OUTLIVE construction. v1.0 built a provider,
/// constructed one object from it, and dropped it -- which is why a registrar
/// could register a service that nothing could ever reach.
/// </summary>
public sealed class HostedGraph(
    ServiceProvider provider, IReadOnlyList<string> serviceNames, IReadOnlySet<string> scopedNames)
    : IDisposable
{
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
    /// likely mistake, and "not found" alone leaves the caller guessing
    /// between a typo, a missing registration, and the wrong assembly.
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
    /// Disposes the provider, and with it every singleton it created.
    ///
    /// v3 removed the separate root-disposal branch along with LIB_TYPE. A
    /// root built by ActivatorUtilities was never handed to the container, so
    /// nothing tracked it and this class had to dispose it explicitly while
    /// carefully NOT disposing one the provider owned. With every service now
    /// coming from the container, .NET's own rule covers all of it: the
    /// provider disposes what it created.
    ///
    /// Disposal is ASYNCHRONOUS. ServiceProvider.Dispose() (the synchronous
    /// one) THROWS for any tracked singleton implementing only
    /// IAsyncDisposable -- "'X' type only implements IAsyncDisposable. Use
    /// DisposeAsync to dispose the container." Measured end to end: once such
    /// a service had been resolved, EVERY DELETE /instance returned 500 and
    /// disposal aborted part-way through, leaving the singletons after it in
    /// the list alive. DisposeAsync() handles BOTH IDisposable and
    /// IAsyncDisposable singletons, so it is strictly the wider of the two;
    /// the IDisposable branch below is the fallback for any provider type that
    /// does not offer it.
    ///
    /// Blocking on it is safe here: ASP.NET Core installs no
    /// SynchronizationContext, so there is no context for the continuation to
    /// deadlock waiting on, and this already runs on a request thread (Reset)
    /// or a completed call's continuation (Release), neither of which the
    /// disposal needs back.
    /// </summary>
    public void Dispose()
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
