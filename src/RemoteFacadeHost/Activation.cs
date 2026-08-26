using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RemoteFacadeHost;

public static class Activation
{
    /// <summary>
    /// Builds the plugin's service graph by running its own startup, then
    /// applying the two overrides the container configuration allows.
    ///
    /// The startup does the DI work. That is the whole model: wiring is C# in
    /// the plugin, written against IServiceCollection like any other
    /// application, rather than a configuration language invented here.
    ///
    /// v3 removed the alternative -- a single class named by LIB_TYPE and
    /// constructed by the host. Everything that existed to serve it went with
    /// it: IOptions&lt;T&gt; binding from LIB_OPTIONS, the guard for a root
    /// interface whose implementation could not be identified, and the
    /// recursive auto-registration of concrete constructor dependencies. None
    /// of that is a behaviour change here, because all three keyed off the
    /// root type and were already dead code whenever a registrar was used.
    /// </summary>
    /// <param name="servicesJson">
    /// Interface-to-implementation overrides, resolved out of the PLUGIN
    /// assembly and applied with Replace AFTER the startup runs. This is how
    /// "real wiring, one thing faked" is expressed without the plugin needing
    /// to know it is under test.
    /// </param>
    /// <param name="callbacksJson">
    /// Interface-to-URL map. Each named interface is served by a proxy that
    /// calls BACK into the test process, so a mock can live where the
    /// assertions are instead of being compiled into the plugin.
    /// </param>
    public static HostedGraph Build(string registrar, string servicesJson, string callbacksJson)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The application's own registration method. Runs FIRST so the
        // overrides below can replace any part of it.
        InvokeRegistrar(services, registrar);

        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                      string.IsNullOrWhiteSpace(servicesJson) ? "{}" : servicesJson)
                  ?? [];

        foreach (var (serviceName, implName) in map)
        {
            var serviceType = PluginLoader.Assembly?.GetType(serviceName)
                ?? throw new InvalidOperationException(
                    $"LIB_SERVICES names service type '{serviceName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            var implType = PluginLoader.Assembly?.GetType(implName)
                ?? throw new InvalidOperationException(
                    $"LIB_SERVICES names implementation '{implName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            // Replace, not Add: these are overrides on top of what the startup
            // already wired, and Replace says so unambiguously.
            services.Replace(ServiceDescriptor.Singleton(serviceType, implType));
        }

        var callbacks = JsonSerializer.Deserialize<Dictionary<string, string>>(
                            string.IsNullOrWhiteSpace(callbacksJson) ? "{}" : callbacksJson)
                        ?? [];

        foreach (var (interfaceName, url) in callbacks)
        {
            // Ambiguous configuration fails loudly rather than resolving to
            // whichever mechanism happened to be checked first.
            if (map.ContainsKey(interfaceName))
            {
                throw new InvalidOperationException(
                    $"'{interfaceName}' is named in BOTH LIB_SERVICES and LIB_CALLBACKS. " +
                    "Use one or the other.");
            }

            var interfaceType = PluginLoader.Assembly?.GetType(interfaceName)
                ?? throw new InvalidOperationException(
                    $"LIB_CALLBACKS names '{interfaceName}', which is not in the assembly. " +
                    $"Available: {string.Join(", ", PluginLoader.TypeNames())}");

            services.Replace(ServiceDescriptor.Singleton(
                interfaceType, CallbackProxy.Create(interfaceType, url)));
        }

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
