using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace RemoteFacadeHost;

/// <summary>
/// Makes a plugin's native assets loadable.
///
/// THE PROBLEM. This process is launched as RemoteFacadeHost.dll, so the CLR
/// builds its native search list (NATIVE_DLL_SEARCH_DIRECTORIES) from the
/// HOST's .deps.json, once, at startup. A plugin arrives later via
/// Assembly.LoadFrom and contributes nothing to that list -- so the
/// runtimes/{rid}/native directory sitting right next to the plugin is never
/// probed. Only two directories are: the shared framework's, and the
/// assembly's own. A package like LibGit2Sharp then dies in its type
/// initializer with a DllNotFoundException the first time anything touches a
/// repository, and nothing about that message points at the real cause.
///
/// WHY THIS HOOK AND NOT ANOTHER. Three other options were tried and rejected:
///
///   NativeLibrary.SetDllImportResolver -- registers per-assembly and throws
///   InvalidOperationException on a second registration. LibGit2Sharp installs
///   its own, so the slot is already taken and the host cannot have it.
///
///   Pre-loading the .so by absolute path -- does not satisfy the later
///   load-by-name. dlopen dedupes by SONAME, and these binaries are built
///   without one, so the already-open handle is never matched and the
///   by-name load fails anyway.
///
///   Setting LD_LIBRARY_PATH from C# -- inert. The dynamic loader reads it
///   once at process start; changing it in-process affects children, never
///   the current process. That is why the entrypoint script does this job
///   too, and why it cannot be done here alone.
///
/// ResolvingUnmanagedDll has none of those problems. It is a multicast event,
/// so subscribing never collides with LibGit2Sharp's resolver; it fires as a
/// last resort, only after that resolver has declined AND default probing has
/// failed, so it cannot mask a library that would have loaded normally; and it
/// hands back a handle directly, so the missing SONAME is irrelevant.
/// </summary>
public static class NativeResolver
{
    private static bool _installed;
    private static string[] _probeDirs = [];
    private static string _pluginDir = "";

    /// <summary>
    /// Subscribes the resolver for a plugin directory. Idempotent: a second
    /// call is ignored rather than stacking another handler, so a later
    /// reload path cannot make resolution order depend on subscription order.
    /// </summary>
    public static void Install(string pluginDir)
    {
        if (_installed) return;
        _installed = true;

        _pluginDir = Directory.Exists(pluginDir)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(pluginDir)) + Path.DirectorySeparatorChar
            : "";
        _probeDirs = ProbeDirs(pluginDir).ToArray();

        // One line at startup, so a container that later fails to load a
        // native library has the "what did it even look at" answer already in
        // its log rather than requiring a rerun to find out.
        Console.Error.WriteLine(
            $"[native] rid={RuntimeInformation.RuntimeIdentifier} " +
            (_probeDirs.Length == 0
                ? $"no native asset directories under {pluginDir}"
                : $"probing: {string.Join(", ", _probeDirs)}"));

        AssemblyLoadContext.Default.ResolvingUnmanagedDll += Resolve;
    }

    /// <summary>
    /// Directories to search, most specific first: this runtime's own RID,
    /// then the plugin root itself (where a RID-specific `dotnet publish`
    /// puts native assets instead of under runtimes/).
    /// </summary>
    private static IEnumerable<string> ProbeDirs(string pluginDir)
    {
        foreach (var rid in RidCandidates())
        {
            var d = Path.Combine(pluginDir, "runtimes", rid, "native");
            if (Directory.Exists(d)) yield return d;
        }

        if (Directory.Exists(pluginDir)) yield return pluginDir;
    }

    internal static IEnumerable<string> RidCandidates()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        yield return rid;

        // A package predating musl RIDs ships only linux-x64. Trying it costs
        // one failed dlopen: musl's loader refuses a glibc-linked object with
        // unresolved symbols, TryLoad returns false, and the search moves on.
        // It cannot half-load and crash.
        if (rid.Contains("-musl-")) yield return rid.Replace("-musl-", "-");
    }

    /// <summary>
    /// The last-resort probe. Returns IntPtr.Zero to decline, which lets the
    /// runtime raise its own DllNotFoundException -- deliberately, so a
    /// genuinely missing library still fails with the standard exception the
    /// caller's own error handling expects.
    /// </summary>
    private static nint Resolve(Assembly requesting, string name)
    {
        foreach (var dir in _probeDirs)
        {
            foreach (var file in FileNames(name))
            {
                var candidate = Path.Combine(dir, file);
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }
        }

        // Report a miss ONLY for an assembly that came from the plugin
        // directory. A miss is not automatically a fault: the shared framework
        // probes for genuinely optional native libraries during startup --
        // System.Net.Quic asks for libmsquic on every boot and copes fine when
        // it is absent -- so logging every decline would print two lines of
        // pure noise per container start and bury the one line that means
        // something. Measured, not assumed: those msquic lines are what the
        // first run of this resolver actually produced.
        if (IsFromPlugin(requesting))
        {
            Console.Error.WriteLine(
                $"[native] could not resolve '{name}' requested by " +
                $"{requesting.GetName().Name}; rid={RuntimeInformation.RuntimeIdentifier}; " +
                (_probeDirs.Length == 0
                    ? "no native asset directories were found next to the plugin"
                    : $"searched: {string.Join(", ", _probeDirs)}"));
        }

        return nint.Zero;
    }

    /// <summary>
    /// Extra detail to append to an error message, but ONLY when the failure
    /// really is a missing native library. Returns "" for everything else.
    ///
    /// Narrow on purpose. The message that reaches the caller is
    /// "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an
    /// exception" -- which names the symptom and hides the cause, and the
    /// cause (a DllNotFoundException) is two levels down the InnerException
    /// chain where no test output will ever show it. The container log has
    /// the good message from Resolve, but a failing test shows the HTTP
    /// response, not the log.
    ///
    /// Scoped to DllNotFoundException so it cannot perturb any other
    /// response: /invoke error bodies are pinned byte-for-byte against the
    /// previous release by test/baseline.sh, and a broader change here would
    /// break that guard for cases that have nothing to do with native assets.
    /// </summary>
    public static string HintFor(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is DllNotFoundException dll)
            {
                return DescribeMiss(dll, RuntimeInformation.RuntimeIdentifier, _probeDirs, _pluginDir);
            }
        }

        return "";
    }

    /// <summary>
    /// The wording of a native-load failure, given its inputs rather than
    /// reading the statics.
    ///
    /// Split out so the message can be tested directly. Install() subscribes a
    /// process-global event and latches a flag that nothing can undo, so a test
    /// that went through it could run exactly once per process and would leave
    /// the resolver installed for every test after it.
    /// </summary>
    internal static string DescribeMiss(
        DllNotFoundException dll, string rid, IReadOnlyList<string> probeDirs, string pluginDir) =>
        $" [native library {LibraryName(dll)} could not be loaded: host rid={rid}; " +
        (probeDirs.Count == 0
            ? $"no native asset directories were found under {pluginDir}. " +
              "Publish the plugin so its runtimes/<rid>/native folder travels with it."
            : $"searched {string.Join(", ", probeDirs)}. " +
              $"The plugin needs a build carrying native assets for {rid}.") +
        "]";

    /// <summary>
    /// The quoted library name out of a DllNotFoundException, or "" if the
    /// message is not shaped as expected.
    ///
    /// Only the name is taken. The full message runs to a dozen lines -- every
    /// path the runtime tried, plus advice about strace and LD_DEBUG -- and
    /// appending it buried the useful hint in a wall of text that no test
    /// failure summary would survive. The paths in it are also the wrong ones
    /// to report here: they are the runtime's, and this resolver's own
    /// directories are named below.
    /// </summary>
    internal static string LibraryName(DllNotFoundException ex)
    {
        var msg = ex.Message;
        var open = msg.IndexOf('\'');
        if (open < 0) return "";

        var close = msg.IndexOf('\'', open + 1);
        return close < 0 ? "" : $"'{msg[(open + 1)..close]}'";
    }

    /// <summary>
    /// Whether an assembly was loaded out of the plugin directory. Location is
    /// empty for an assembly with no backing file, which is not a plugin
    /// assembly here and correctly reports false.
    /// </summary>
    private static bool IsFromPlugin(Assembly asm) =>
        _pluginDir.Length > 0
        && !string.IsNullOrEmpty(asm.Location)
        && Path.GetFullPath(asm.Location).StartsWith(_pluginDir, StringComparison.Ordinal);

    /// <summary>
    /// Name variants for one DllImport name. A P/Invoke of "git2-6777db8"
    /// must find "libgit2-6777db8.so"; the runtime applies these
    /// decorations during its own probing, and having declined that probing
    /// we have to apply them ourselves.
    /// </summary>
    internal static IEnumerable<string> FileNames(string name)
    {
        yield return name;
        yield return $"{name}.so";
        yield return $"lib{name}.so";
        yield return $"lib{name}";
    }
}
