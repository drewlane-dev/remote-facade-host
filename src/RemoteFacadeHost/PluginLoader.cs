using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace RemoteFacadeHost;

public static class PluginLoader
{
    /// <summary>
    /// Loads the plugin assembly into the DEFAULT AssemblyLoadContext.
    ///
    /// The default context is load-bearing, not a shortcut. A separate
    /// AssemblyLoadContext gives the plugin its own copies of shared assemblies,
    /// so this host's typeof(IOptions&lt;&gt;) becomes a DIFFERENT type identity
    /// from the plugin's -- every dependency shape then has to be matched by
    /// string and resolved out of the plugin's assemblies, and the failures are
    /// cryptic nulls. Sharing the context unifies identities. The cost is that
    /// host and plugin must agree on shared package versions.
    /// </summary>
    private static bool _resolverAttached;

    /// <summary>
    /// The plugin's deps.json, which every other resolution decision reads.
    ///
    /// Named after LIB_ASSEMBLY rather than found by globbing: a publish
    /// directory can contain several deps.json files when a plugin carries the
    /// publish output of its own dependencies, and only the one belonging to
    /// the named assembly describes the plugin.
    ///
    /// Absence is fatal, and deliberately so. AssemblyDependencyResolver
    /// without a deps.json resolves against the plugin ROOT, which for a
    /// package shipping a reference stub there (Microsoft.Data.SqlClient is the
    /// standing example) silently loads the stub. Measured: the first call then
    /// returns "Microsoft.Data.SqlClient is not supported on this platform" --
    /// long after a healthy startup, and reading as a database fault rather
    /// than a packaging one. Refusing to start says the true thing at the only
    /// moment it is cheap to act on.
    /// </summary>
    internal static string RequireDependencyFile(string dir, string assemblyFile)
    {
        var deps = Path.Combine(dir, Path.GetFileNameWithoutExtension(assemblyFile) + ".deps.json");

        if (!File.Exists(deps))
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(deps)} was not found in {dir}. The host resolves the " +
                "plugin's dependencies -- its RID-specific assemblies and its native assets " +
                "-- from that file, and without it a package that ships a reference stub at " +
                "its root would load the stub and fail later with a message about the wrong " +
                "thing. Publish the plugin with 'dotnet publish', which emits it, and mount " +
                "the publish output rather than a project or bin directory.");
        }

        // Present is not the same as usable, and the difference is invisible
        // without this. A deps.json of "{}" parses, satisfies File.Exists, and
        // describes nothing -- so AssemblyDependencyResolver resolves nothing
        // and every package falls back to the plugin root, which is precisely
        // the state this whole requirement exists to prevent. Measured against
        // a container carrying one: the host started, reported healthy, and
        // the first call returned "Microsoft.Data.SqlClient is not supported
        // on this platform."
        //
        // The check is deliberately shallow -- valid JSON, with a non-empty
        // "targets" -- because that is the part this host actually depends on
        // and the part a truncated or hand-written file gets wrong. Validating
        // further would start rejecting documents the runtime itself accepts.
        JsonElement targets;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(deps));
            doc.RootElement.TryGetProperty("targets", out targets);
            targets = targets.ValueKind == JsonValueKind.Object ? targets.Clone() : default;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(deps)} in {dir} could not be read as JSON " +
                $"({ex.GetType().Name}: {ex.Message}). It is the plugin's dependency file; " +
                "a truncated or partially-copied one will do this. Re-publish the plugin " +
                "and mount the complete output.");
        }

        if (targets.ValueKind != JsonValueKind.Object || !targets.EnumerateObject().Any())
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(deps)} in {dir} describes no dependencies: it has no " +
                "non-empty \"targets\" section. The host resolves the plugin's RID-specific " +
                "assemblies and native assets from that section, and an empty one resolves " +
                "nothing -- every package would fall back to the copy in the plugin root, " +
                "which for a package shipping a reference stub there means loading the stub. " +
                "Re-publish the plugin with 'dotnet publish'.");
        }

        return deps;
    }

    /// <summary>
    /// Loads the plugin assembly, resolving its dependencies through
    /// <paramref name="deps"/>.
    ///
    /// LoadFromAssemblyPath, NOT Assembly.LoadFrom, and the difference is the
    /// whole reason this works. LoadFrom installs probing that searches the
    /// loaded assembly's own directory, so a package's ROOT copy resolves
    /// successfully and the Resolving hook below never fires -- there is no
    /// failure to hook, and the reference stub wins. Measured both ways: with
    /// LoadFrom the host had to pre-load the RID-specific assemblies before
    /// touching the plugin to get in front of that probing; with
    /// LoadFromAssemblyPath the hook fires and deps.json decides, so no
    /// pre-loading is needed at all.
    /// </summary>
    public static Assembly LoadAssembly(string dir, string assemblyFile, AssemblyDependencyResolver deps)
    {
        var path = Path.Combine(dir, assemblyFile);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"assembly not found: {path}");
        }

        // Guard against a second call re-subscribing: Resolving is process-wide
        // with no unsubscription here, so handlers would otherwise accumulate
        // and resolution order would become subscription order. Dormant while
        // this runs once per container, but cheap insurance against a later
        // reload path.
        if (!_resolverAttached)
        {
            _resolverAttached = true;

            AssemblyLoadContext.Default.Resolving += (context, name) =>
                deps.ResolveAssemblyToPath(name) is { } resolved
                    ? context.LoadFromAssemblyPath(resolved)
                    : null;
        }

        return Assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    /// <summary>The loaded assembly, exposed so /types can report on it.</summary>
    public static Assembly? Assembly { get; private set; }

    /// <summary>
    /// Public type names in the loaded assembly. Exists because a wrong
    /// LIB_REGISTRAR or service name is otherwise a dead end — notably for VB
    /// libraries, where RootNamespace is
    /// PREPENDED to declared namespaces, so the real name is often not what a C#
    /// developer would write.
    /// </summary>
    public static IEnumerable<string> TypeNames() =>
        Assembly?.GetExportedTypes().Select(t => t.FullName!) ?? [];
}
