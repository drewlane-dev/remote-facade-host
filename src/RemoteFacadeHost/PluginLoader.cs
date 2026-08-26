using System.Reflection;

namespace RemoteFacadeHost;

public static class PluginLoader
{
    /// <summary>
    /// Loads the plugin assembly into the DEFAULT AssemblyLoadContext and returns
    /// the requested type.
    ///
    /// The default context is load-bearing, not a shortcut. A separate
    /// AssemblyLoadContext gives the plugin its own copies of shared assemblies,
    /// so this host's typeof(IOptions&lt;&gt;) becomes a DIFFERENT type identity
    /// from the plugin's — every dependency shape then has to be matched by
    /// string and resolved out of the plugin's assemblies, and the failures are
    /// cryptic nulls. Sharing the context unifies identities. The cost is that
    /// host and plugin must agree on shared package versions.
    /// </summary>
    private static bool _resolverAttached;

    public static Type Load(string dir, string assemblyFile, string typeName)
    {
        LoadAssembly(dir, assemblyFile);

        return Assembly!.GetType(typeName)
            ?? throw new InvalidOperationException(
                $"type '{typeName}' not found in {assemblyFile}. " +
                $"Available: {string.Join(", ", TypeNames())}");
    }

    /// <summary>
    /// Loads the plugin assembly without requiring a type name. Composition-root
    /// mode needs the assembly (for the registrar and for resolving service names)
    /// but names no single type.
    /// </summary>
    public static Assembly LoadAssembly(string dir, string assemblyFile)
    {
        // Guard against a second Load() re-subscribing: AssemblyResolve is
        // process-wide with no unsubscription here, so handlers would
        // otherwise accumulate and resolution order would become
        // registration order — a dependency could then silently resolve
        // from the wrong plugin directory. Dormant while Load runs once per
        // container, but cheap insurance against a later caller (e.g. an
        // instance-reset path) invoking Load again.
        if (!_resolverAttached)
        {
            _resolverAttached = true;
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var candidate = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };
        }

        var path = Path.Combine(dir, assemblyFile);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"assembly not found: {path}");
        }

        return Assembly = Assembly.LoadFrom(path);
    }

    /// <summary>The loaded assembly, exposed so /types can report on it.</summary>
    public static Assembly? Assembly { get; private set; }

    /// <summary>
    /// Public type names in the loaded assembly. Exists because a wrong LIB_TYPE
    /// is otherwise a dead end — notably for VB libraries, where RootNamespace is
    /// PREPENDED to declared namespaces, so the real name is often not what a C#
    /// developer would write.
    /// </summary>
    public static IEnumerable<string> TypeNames() =>
        Assembly?.GetExportedTypes().Select(t => t.FullName!) ?? [];
}
