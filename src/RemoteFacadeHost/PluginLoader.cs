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

    /// <summary>
    /// Loads the plugin assembly. Nothing names a single type: the registrar
    /// and the per-call service lookups are both resolved out of it by name.
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

        // BEFORE the plugin, and this ordering is the whole fix. Assembly
        // .LoadFrom probes the loaded assembly's own directory for its
        // dependencies, so the root copy resolves successfully and
        // AssemblyResolve never fires -- there is no failure to hook. The only
        // way to win is to have the right assembly already loaded.
        var preloaded = PreloadRidAssets(dir);

        if (preloaded.Count > 0)
        {
            Console.Error.WriteLine(
                $"[plugin] preloaded {preloaded.Count} RID-specific assemblies from " +
                Path.GetDirectoryName(preloaded[0]));
        }

        var path = Path.Combine(dir, assemblyFile);

        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"assembly not found: {path}");
        }

        return Assembly = Assembly.LoadFrom(path);
    }

    /// <summary>
    /// Loads the plugin's RID-specific MANAGED assemblies BEFORE the plugin
    /// itself, so its references bind to them rather than to the copies in the
    /// plugin root.
    ///
    /// The root copy is not always the real implementation. Packages that ship
    /// per-platform builds put a reference assembly at the root and the working
    /// one under runtimes/&lt;rid&gt;/lib/&lt;tfm&gt;. Microsoft.Data.SqlClient is
    /// the common case: its root assembly is a stub whose members throw
    /// "Microsoft.Data.SqlClient is not supported on this platform", and the
    /// implementation is 3x the size under runtimes/unix/lib.
    ///
    /// Normally the runtime picks between them from the app's deps.json. A
    /// plugin loaded by Assembly.LoadFrom gets none of that -- the HOST's
    /// deps.json governs, and it has never heard of the plugin's packages -- so
    /// without this the stub loads and every call fails at the first use.
    ///
    /// This is the managed twin of what NativeResolver does for native assets.
    /// Same cause, same shape of fix.
    /// </summary>
    /// <summary>
    /// The RID fallback chain for MANAGED assets, most specific first.
    ///
    /// Deliberately not NativeResolver's chain. A native binary must match the
    /// platform exactly, so that one tries only the running RID and its
    /// glibc/musl twin. Managed assets are portable, and packages ship them
    /// under portable RIDs: Microsoft.Data.SqlClient uses runtimes/unix, which
    /// matches no exact RID at all. Without the portable rungs the preload
    /// finds nothing and the stub still wins, which is exactly what the first
    /// version of this did.
    /// </summary>
    internal static IEnumerable<string> ManagedRidCandidates()
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        yield return rid;

        if (rid.Contains("-musl-"))
        {
            yield return rid.Replace("-musl-", "-");
        }

        var os = rid.Split('-')[0];

        if (rid.Contains("-musl"))
        {
            yield return $"{os}-musl";
        }

        yield return os;

        // Everything that is not Windows answers to "unix" in the RID graph.
        if (!os.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            yield return "unix";
        }
    }

    internal static IReadOnlyList<string> PreloadRidAssets(string dir)
    {
        var loaded = new List<string>();

        var framework = RidAssetDirectory(dir);

        if (framework is not null)
        {
            foreach (var dll in Directory.GetFiles(framework, "*.dll"))
            {
                try
                {
                    Assembly.LoadFrom(dll);
                    loaded.Add(dll);
                }
                catch (Exception ex)
                {
                    // A file that will not load is not fatal: the plugin may
                    // never touch it, and failing here would turn an unused
                    // asset into a container that refuses to start.
                    Console.Error.WriteLine($"[plugin] could not preload {dll}: {ex.Message}");
                }
            }
        }

        return loaded;
    }

    /// <summary>
    /// The one framework folder to preload from, or null when the plugin ships
    /// no RID-specific managed assets.
    ///
    /// The FIRST matching RID wins and the search stops. Continuing would load
    /// the same assemblies again from a less specific RID, and whichever landed
    /// first would silently decide the behaviour.
    /// </summary>
    internal static string? RidAssetDirectory(string dir)
    {
        foreach (var rid in ManagedRidCandidates())
        {
            var libs = Path.Combine(dir, "runtimes", rid, "lib");
            if (!Directory.Exists(libs)) continue;

            // Highest framework folder first: a package may ship net8.0 and
            // net9.0 side by side, and the newest is the one the SDK would have
            // chosen.
            var framework = Directory.GetDirectories(libs)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();

            if (framework is not null) return framework;
        }

        return null;
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
