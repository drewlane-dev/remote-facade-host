using Microsoft.Extensions.DependencyInjection;

namespace RemoteFacadeHost;

public static class Activation
{
    /// <summary>
    /// Builds the plugin's service graph by running its own startup.
    ///
    /// The startup does the DI work -- all of it. That is the whole model:
    /// wiring is C# in the plugin, written against IServiceCollection like any
    /// other application, rather than a configuration language invented here.
    ///
    /// v3 removed the alternative -- a single class named by LIB_TYPE and
    /// constructed by the host. Everything that existed to serve it went with
    /// it: IOptions&lt;T&gt; binding from LIB_OPTIONS, the guard for a root
    /// interface whose implementation could not be identified, and the
    /// recursive auto-registration of concrete constructor dependencies. None
    /// of that is a behaviour change here, because all three keyed off the
    /// root type and were already dead code whenever a registrar was used.
    ///
    /// v4 removed the last of it: LIB_SERVICES, a JSON map of interface name
    /// to implementation name applied with Replace after this ran. It was the
    /// surviving piece of exactly the configuration language the paragraph
    /// above rejects, and it bought nothing a startup cannot say better. "Real
    /// wiring, one thing faked" is now what it always should have been --
    /// a startup that calls another startup and then Replace:
    ///
    ///     public static void Configure(IServiceCollection services)
    ///     {
    ///         RealStartup.Configure(services);
    ///         services.Replace(ServiceDescriptor.Singleton&lt;IClock, FixedClock&gt;());
    ///     }
    ///
    /// That is compile-checked, and it can express what the map could not: the
    /// lifetime to register at (the map always forced Singleton, silently
    /// changing a Transient dependency's semantics), a factory, a keyed
    /// service, or a substitution made conditionally.
    /// </summary>
    public static HostedGraph Build(string registrar)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The application's own registration method, and the only thing that
        // registers anything. Nothing runs after it.
        InvokeRegistrar(services, registrar);

        // Captured BEFORE building: a ServiceCollection is the list of
        // descriptors, and it is the only place registered service types can
        // be enumerated. A built provider does not expose them.
        var serviceNames = services
            .Select(d => d.ServiceType.FullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct()
            .ToList();

        // DI resolves the LAST registration for a service type, so "any
        // matching descriptor is Scoped" is the wrong question -- an interface
        // registered both AddScoped and AddSingleton (without Replace)
        // resolves as the singleton, and rejecting it here would refuse a call
        // the container would have served correctly. Grouping preserves
        // registration order, so g.Last() is exactly the descriptor GetService
        // would hand back.
        //
        // Reads only Lifetime and ServiceType.FullName. That restraint is
        // deliberate and was learned the hard way: ImplementationInstance and
        // ImplementationType THREW on a keyed descriptor in
        // Microsoft.Extensions.DependencyInjection.Abstractions 8.0.0
        // (dotnet/runtime#95789) and were changed to return null for 9.0.0+,
        // so the failure mode of a descriptor scan depends on which package
        // version is restored rather than on this code.
        var scopedNames = services
            .Where(d => d.ServiceType.FullName is not null)
            .GroupBy(d => d.ServiceType.FullName!)
            .Where(g => g.Last().Lifetime == ServiceLifetime.Scoped)
            .Select(g => g.Key)
            .ToHashSet();

        // Nothing is constructed up front: every call names the service it
        // wants, and the graph resolves it per call. That is what lets a
        // Transient registration yield a new instance per call, and what lets
        // a reset rebuild the graph without invalidating any client proxy.
        return new HostedGraph(services.BuildServiceProvider(), serviceNames, scopedNames);
    }

    /// <summary>
    /// Invokes a static registration method named as
    /// "Namespace.TypeName.MethodName", passing the service collection.
    ///
    /// Extension methods work unchanged: an extension is a static method on a
    /// static class, and reflection sees it that way.
    /// </summary>
    private static void InvokeRegistrar(IServiceCollection services, string registrar)
    {
        var lastDot = registrar.LastIndexOf('.');

        if (lastDot <= 0)
        {
            throw new InvalidOperationException(
                $"LIB_REGISTRAR '{registrar}' must be 'Namespace.TypeName.MethodName'.");
        }

        var typeName = registrar[..lastDot];
        var methodName = registrar[(lastDot + 1)..];

        var declaring = PluginLoader.Assembly?.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"LIB_REGISTRAR names type '{typeName}', which is not in the assembly. " +
                $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

        var method = declaring.GetMethods()
            .FirstOrDefault(m => m.Name == methodName
                                 && m.IsStatic
                                 && m.GetParameters().Length == 1
                                 && m.GetParameters()[0].ParameterType
                                        .IsAssignableFrom(typeof(IServiceCollection)))
            ?? throw new InvalidOperationException(
                $"LIB_REGISTRAR: '{typeName}' has no static method '{methodName}' " +
                "taking a single IServiceCollection parameter.");

        method.Invoke(null, [services]);
    }
}
