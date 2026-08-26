using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace RemoteFacadeHost;

public static class Activation
{
    /// <summary>
    /// Constructs the type using a real service container, so dependencies may
    /// nest arbitrarily.
    ///
    /// IOptions&lt;T&gt; is bound from JSON and ILogger&lt;T&gt; is supplied
    /// automatically, as before. Everything else must be named in
    /// <paramref name="servicesJson"/>, an interface-to-implementation map whose
    /// types are resolved out of the PLUGIN assembly.
    ///
    /// This is also how a dependency is faked. A mock created in the test process
    /// cannot be injected into an instance living in this container — that would
    /// need a call back into the test process. A fake compiled into the plugin and
    /// named here is the supported substitute.
    /// </summary>
    public static HostedGraph Build(
        Type? rootType, string optionsJson, string servicesJson, string? registrar, string callbacksJson)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The application's own registration method, if named. Runs FIRST so the
        // explicit map below can override any part of it — that ordering is what
        // makes "real wiring, one thing faked" expressible.
        InvokeRegistrar(services, registrar);

        // Registrations from config, resolved out of the plugin assembly.
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(
                      string.IsNullOrWhiteSpace(servicesJson) ? "{}" : servicesJson)
                  ?? [];

        // The implementation each mapped service resolved to, kept as TYPES.
        // The IOptions<T> binding below needs it: when LIB_TYPE names an
        // interface, the interface has no constructor and the IMPLEMENTATION
        // is what actually asks for the options.
        var implByService = new Dictionary<Type, Type>();

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

            // Replace, not Add: these are overrides on top of whatever the
            // registrar already wired, and Replace says so unambiguously.
            services.Replace(ServiceDescriptor.Singleton(serviceType, implType));
            implByService[serviceType] = implType;
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

        // IOptions<T> for whatever closed generics the root asks for.
        //
        // "The root" is not always a type with a constructor. LIB_TYPE may
        // name an INTERFACE (a v1.1 feature), and an interface has none -- so
        // binding off rootType.GetConstructors() alone skipped the whole
        // block and the implementation silently received DEFAULT options.
        // Measured, same image and same LIB_OPTIONS={"RootPath":"/tmp/opt-
        // check"}: LIB_TYPE=CsLib.Store wrote to /tmp/opt-check, while
        // LIB_TYPE=CsLib.IStore wrote to /tmp -- ok:true, no warning, wrong
        // location. Silently discarding configuration is the worst available
        // outcome, so the binding must not depend on the root having a
        // constructor.
        //
        // What it depends on instead is the OPTIONS type -- and the type that
        // asks for it is the implementation. When LIB_TYPE is an interface,
        // LIB_SERVICES NAMES that implementation, so it is taken from there.
        // Deliberately NOT from the ServiceDescriptor that would serve the
        // resolution: descriptor inspection is banned in this file (see the
        // ownership comment below), and for good reason.
        //
        // An implementation supplied ONLY by LIB_REGISTRAR cannot be found
        // without that banned inspection, so that shape is rejected loudly at
        // startup rather than served with the wrong options -- see below.
        var optionsRoots = new List<Type>();

        if (rootType is not null)
        {
            optionsRoots.Add(rootType);

            if (implByService.TryGetValue(rootType, out var rootImpl))
            {
                optionsRoots.Add(rootImpl);
            }
        }

        var boundOptions = new HashSet<Type>();

        foreach (var optionsRoot in optionsRoots)
        {
            var ctor = optionsRoot.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (ctor is null)
            {
                continue;
            }

            foreach (var p in ctor.GetParameters())
            {
                var pt = p.ParameterType;

                if (!pt.IsGenericType || pt.GetGenericTypeDefinition() != typeof(IOptions<>))
                {
                    continue;
                }

                if (!boundOptions.Add(pt))
                {
                    continue;
                }

                var inner = pt.GetGenericArguments()[0];
                var value = JsonSerializer.Deserialize(optionsJson, inner,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                var wrapped = typeof(Options).GetMethod("Create")!
                    .MakeGenericMethod(inner).Invoke(null, [value])!;

                services.AddSingleton(pt, wrapped);
            }
        }

        // The one shape left where LIB_OPTIONS cannot be honoured: LIB_TYPE
        // names an INTERFACE whose implementation comes from LIB_REGISTRAR,
        // which this host cannot identify without inspecting descriptors.
        // Rather than bind nothing and serve default options -- the exact
        // silent failure this block exists to end -- say so at startup. A
        // registrar is C#, so it can configure its own options directly.
        //
        // Each clause is load-bearing, and two of them were added after this
        // guard's first version fired on shapes it has no business refusing:
        //
        //   IsInterface -- NOT "has no public constructor". A concrete type
        //   with only a private constructor also has none, and refusing it
        //   here MASKED the accurate "A suitable constructor for type 'X'
        //   could not be located" it would otherwise have failed with, which
        //   names the real mistake. Measured: CsLib.PrivateCtorRoot with a
        //   non-empty LIB_OPTIONS reported the options problem instead.
        //
        //   !implByService.ContainsKey -- the implementation IS identifiable
        //   when LIB_SERVICES names it, whether or not that implementation
        //   happens to ask for options. Without this clause an interface root
        //   WITH a perfectly good LIB_SERVICES mapping died telling the
        //   operator to add the mapping they had already added, purely
        //   because nothing in the graph wanted an IOptions<T>. Measured.
        //
        // Only non-empty LIB_OPTIONS is fatal: "{}" (and unset, which becomes
        // "{}") asks for nothing, so an interface root with no options
        // configured at all keeps working exactly as before.
        if (rootType is not null
            && rootType.IsInterface
            && !implByService.ContainsKey(rootType)
            && boundOptions.Count == 0
            && !string.IsNullOrWhiteSpace(optionsJson)
            && optionsJson.Trim() != "{}")
        {
            throw new InvalidOperationException(
                $"LIB_OPTIONS is set, but LIB_TYPE '{rootType.FullName}' is an interface with no " +
                "implementation named for it in LIB_SERVICES, so there is no constructor to bind " +
                "the options from and they would be silently discarded. Either name the " +
                "implementation in LIB_SERVICES as {\"Full.IService\":\"Full.Implementation\"}, " +
                "or configure its options in your LIB_REGISTRAR startup and unset LIB_OPTIONS.");
        }

        // Auto-register concrete dependencies as themselves, recursively.
        // ActivatorUtilities resolves every parameter from the container and
        // throws for anything unregistered — it does NOT construct concrete
        // types on its own. Without this, a perfectly ordinary
        // GitManager(GitConfigManager) would fail at startup and force the
        // caller to self-register every concrete dependency by hand.
        //
        // Anything already named in LIB_SERVICES is left alone, so substituting
        // a fake stays a one-line change:
        //   {"MyApp.GitConfigManager":"MyApp.Testing.FakeGitConfigManager"}
        // (the fake must derive from it — a sealed class with non-virtual
        // members cannot be substituted by any mechanism, here or in-process).
        var seen = new HashSet<Type>();
        var pending = new Queue<Type>();
        if (rootType is not null) pending.Enqueue(rootType);

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();

            if (!seen.Add(current))
            {
                continue;
            }

            var currentCtor = current.GetConstructors()
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (currentCtor is null)
            {
                continue;
            }

            foreach (var p in currentCtor.GetParameters())
            {
                var pt = p.ParameterType;

                var alreadyHandled =
                    map.ContainsKey(pt.FullName ?? string.Empty)
                    // Also: anything the registrar (or an earlier turn of this
                    // same walk) already put in the container. Checking only
                    // `map` missed a registrar's own factory registration for a
                    // concrete type -- e.g. services.AddSingleton<GitConfigManager>(sp
                    // => new GitConfigManager(...)) -- when that same type also
                    // showed up as a nested constructor parameter: the walk would
                    // add a second, plain AddSingleton(pt, pt) descriptor, and
                    // since the container resolves the LAST matching descriptor,
                    // that silently threw away the registrar's deliberate wiring.
                    || services.Any(d => d.ServiceType == pt)
                    || pt.IsInterface
                    || pt.IsAbstract
                    || pt.IsPrimitive
                    || pt == typeof(string)
                    || (pt.IsGenericType
                        && (pt.GetGenericTypeDefinition() == typeof(IOptions<>)
                            || pt.GetGenericTypeDefinition() == typeof(ILogger<>)));

                if (alreadyHandled)
                {
                    continue;
                }

                services.AddSingleton(pt, pt);
                pending.Enqueue(pt);
            }
        }

        // Captured BEFORE building: a ServiceCollection is the list of
        // descriptors, and it is the only place the registered service types
        // can be enumerated. A built provider does not expose them.
        var serviceNames = services
            .Select(d => d.ServiceType.FullName)
            .Where(n => n is not null)
            .Select(n => n!)
            .Distinct()
            .ToList();

        // DI resolves the LAST registration for a service type, so "any
        // matching descriptor is Scoped" is the wrong question -- an
        // interface registered both AddScoped and AddSingleton (without
        // Replace) resolves as the singleton, and rejecting it here would
        // refuse a call the container would have served correctly. Grouping
        // preserves registration order, so g.Last() is exactly the
        // descriptor GetService would hand back. Reads only Lifetime and
        // ServiceType.FullName -- the properties Task 2 confirmed are safe
        // to inspect; ImplementationInstance is the one that throws on a
        // keyed descriptor, and this touches neither it nor anything else.
        var scopedNames = services
            .Where(d => d.ServiceType.FullName is not null)
            .GroupBy(d => d.ServiceType.FullName!)
            .Where(g => g.Last().Lifetime == ServiceLifetime.Scoped)
            .Select(g => g.Key)
            .ToHashSet();

        var provider = services.BuildServiceProvider();

        if (rootType is null)
        {
            // Composition-root mode: nothing to construct up front. Every call
            // names the service it wants.
            return new HostedGraph(provider, null, rootOwnedByProvider: false, serviceNames, scopedNames);
        }

        object rootInstance;
        bool rootOwnedByProvider;
        try
        {
            // Ask the CONTAINER first. This is what makes a factory
            // registration -- services.AddSingleton(sp => new Thing("x")) --
            // actually get used, and what allows LIB_TYPE to name an
            // interface. ActivatorUtilities can do neither: it constructs the
            // type directly and ignores any registration for it.
            //
            // Ownership for disposal is "fromProvider is not null" -- and
            // ONLY that, deliberately. An earlier version of this tried to
            // refine it by inspecting the ServiceDescriptor that served the
            // resolution (to tell an instance registration apart from a
            // type/factory one). That broke on an open-generic mapping,
            // which has no descriptor whose ServiceType even equals the
            // closed root type -- ownership silently computed false and the
            // root was disposed twice. It was also fragile in a way that
            // does not even hold still across .NET versions: reading
            // ServiceDescriptor.ImplementationInstance/ImplementationType on
            // a KEYED descriptor THREW in Microsoft.Extensions.
            // DependencyInjection.Abstractions 8.0.0 (dotnet/runtime#95789)
            // and was changed to return null instead for 9.0.0+, which is
            // what this project (net10.0, package version 10.0.0) actually
            // gets -- so the exact failure mode of a descriptor scan depends
            // on which DI package version is restored, not just on this
            // code. Descriptor shapes -- and their throw-or-null behaviour
            // -- are an open set a scan can never be complete against.
            //
            // The rule that needs no inspection is .NET's own: the provider
            // disposes what it CREATED. "fromProvider is not null" means the
            // container produced this instance -- whether by constructing a
            // type mapping, invoking a factory, resolving an open generic, or
            // anything else it knows how to build -- and it will dispose that
            // instance itself when the provider is disposed, regardless of
            // which of those shapes it was. The one case the container does
            // NOT own is an INSTANCE registration --
            // services.AddSingleton<Root>(existingInstance) -- because the
            // container did not create that object; GetService still returns
            // it (fromProvider is not null), so by this rule HostedGraph
            // treats it as provider-owned and does not dispose it either.
            // That is deliberate, not a gap: the caller who constructed the
            // instance owns its lifetime, exactly as in any ordinary
            // ASP.NET Core application. See the instance-registration test
            // below for the consequence spelled out.
            var fromProvider = provider.GetService(rootType);
            rootOwnedByProvider = fromProvider is not null;
            rootInstance = fromProvider ?? ActivatorUtilities.CreateInstance(provider, rootType);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                $"cannot construct {rootType.FullName}: {ex.Message}. " +
                "Register it or its missing dependency in your LIB_REGISTRAR " +
                "startup, or name an implementation in LIB_SERVICES as " +
                "{\"Full.IService\":\"Full.Implementation\"}.", ex);
        }

        return new HostedGraph(provider, rootInstance, rootOwnedByProvider, serviceNames, scopedNames);
    }

    /// <summary>
    /// Invokes a static registration method named as
    /// "Namespace.TypeName.MethodName", passing the service collection.
    ///
    /// Extension methods work unchanged: an extension is a static method on a
    /// static class, and reflection sees it that way.
    /// </summary>
    private static void InvokeRegistrar(IServiceCollection services, string? registrar)
    {
        if (string.IsNullOrWhiteSpace(registrar))
        {
            return;
        }

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
