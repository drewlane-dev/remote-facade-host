using System.Runtime.InteropServices;

namespace RemoteFacadeHost;

/// <summary>
/// Explains a native-load failure. It does not resolve anything.
///
/// There used to be a managed resolver here, subscribed to
/// ResolvingUnmanagedDll, because the CLR builds its native search list from
/// the HOST's .deps.json at startup and a plugin arriving later contributes
/// nothing to it. entrypoint.sh solves the same problem a layer lower, by
/// putting the plugin's runtimes/&lt;rid&gt;/native directories on
/// LD_LIBRARY_PATH before the process starts -- and measurement showed the two
/// were not complementary but redundant, in one direction:
///
///   ResolvingUnmanagedDll fires only after default probing has FAILED.
///   Default probing is dlopen. dlopen reads LD_LIBRARY_PATH. So whenever the
///   entrypoint has run, probing succeeds and the hook cannot fire -- observed
///   directly, with the hook logging every call it received and logging
///   nothing for the library that loaded.
///
/// The reverse is not true, which is why the script is the half that stayed. A
/// native library with a sibling dependency in the same directory cannot be
/// loaded by absolute path -- which is all a managed hook can do -- unless it
/// sets an $ORIGIN RUNPATH, because the dynamic loader resolves DT_NEEDED
/// against RUNPATH, LD_LIBRARY_PATH and the system paths, never against the
/// directory of the object doing the loading. Measured on a purpose-built
/// pair: without RUNPATH, dlopen("/abs/libparent.so") fails with "Error
/// loading shared library libchild.so"; with the directory on LD_LIBRARY_PATH
/// it succeeds. No managed hook can substitute for that.
///
/// The consequence, and it is deliberate: the entrypoint is now MANDATORY.
/// Running the image with --entrypoint dotnet leaves native assets unfindable.
/// </summary>
public static class NativeDiagnostics
{
    /// <summary>
    /// Extra detail to append to an error message, but ONLY when the failure
    /// really is a missing native library. Returns "" for everything else.
    ///
    /// Narrow on purpose. The message that reaches the caller is
    /// "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an
    /// exception" -- which names the symptom and hides the cause, and the
    /// cause (a DllNotFoundException) is two levels down the InnerException
    /// chain where no test output will ever show it.
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
                return DescribeMiss(
                    dll,
                    RuntimeInformation.RuntimeIdentifier,
                    Environment.GetEnvironmentVariable("LD_LIBRARY_PATH"));
            }
        }

        return "";
    }

    /// <summary>
    /// The wording of a native-load failure, given its inputs rather than
    /// reading the environment, so the message can be tested directly.
    /// </summary>
    /// <param name="searchPath">
    /// LD_LIBRARY_PATH as the process actually received it -- the loader's own
    /// list, not a recomputation of what entrypoint.sh was expected to build.
    /// Reading it back is what keeps this honest if that script ever changes:
    /// a diagnostic that reports directories nobody searched is worse than
    /// none.
    /// </param>
    internal static string DescribeMiss(DllNotFoundException dll, string rid, string? searchPath) =>
        $" [native library '{LibraryName(dll)}' could not be loaded: host rid={rid}; " +
        (string.IsNullOrEmpty(searchPath)
            ? "no native asset directories were on the loader's search path. Publish the " +
              "plugin so its runtimes/<rid>/native folder travels with it, and do not " +
              "override the image's entrypoint, which is what puts that folder on the path."
            : $"searched {searchPath}. The plugin needs a build carrying native assets for {rid}.") +
        "]";

    /// <summary>
    /// The quoted library name out of a DllNotFoundException, or "" if the
    /// message is not shaped as expected.
    ///
    /// Only the name is taken. The full message runs to a dozen lines -- every
    /// path the runtime tried, plus advice about strace and LD_DEBUG -- and
    /// appending it buried the useful hint in a wall of text that no test
    /// failure summary would survive.
    /// </summary>
    internal static string LibraryName(DllNotFoundException ex)
    {
        var msg = ex.Message;
        var open = msg.IndexOf('\'');
        if (open < 0) return "";

        var close = msg.IndexOf('\'', open + 1);
        return close < 0 ? "" : msg[(open + 1)..close];
    }
}
