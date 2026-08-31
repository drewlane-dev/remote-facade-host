using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// Turning a native-load failure into something that names its own cause.
///
/// There is no longer a managed native resolver to test. Native assets are
/// found by the dynamic loader, from the search path entrypoint.sh builds out
/// of the plugin's runtimes/&lt;rid&gt;/native directories, and a managed hook
/// was measured never to fire alongside it: ResolvingUnmanagedDll runs only
/// after default probing fails, and default probing is dlopen, which reads
/// that same path. What remains is the message.
/// </summary>
public class NativeDiagnosticsTests
{
    [Theory]
    [InlineData("Unable to load shared library 'git2-5853918' or one of its dependencies.", "git2-5853918")]
    [InlineData("no quotes here at all", "")]
    [InlineData("one 'unterminated quote", "")]
    public void The_library_name_is_lifted_out_of_the_runtime_s_message(string message, string expected)
    {
        Assert.Equal(expected, NativeDiagnostics.LibraryName(new DllNotFoundException(message)));
    }

    [Fact]
    public void A_miss_names_the_library_the_rid_and_the_path_the_loader_actually_searched()
    {
        // The search path is the loader's, read back from the environment
        // rather than recomputed: reporting directories this process merely
        // believes were searched is how a diagnostic starts lying after the
        // entrypoint changes.
        var hint = NativeDiagnostics.DescribeMiss(
            new DllNotFoundException("Unable to load shared library 'git2-abc' or one of its dependencies."),
            "linux-musl-arm64",
            "/plugin/runtimes/linux-musl-arm64/native:/plugin");

        Assert.Contains("'git2-abc'", hint);
        Assert.Contains("linux-musl-arm64", hint);
        Assert.Contains("/plugin/runtimes/linux-musl-arm64/native", hint);
    }

    [Fact]
    public void An_empty_search_path_is_reported_as_its_own_fault()
    {
        // Distinct from "searched these and found nothing": an empty path means
        // the plugin has no runtimes/<rid>/native directory at all, or the
        // entrypoint that builds the path was bypassed. Both are fixed
        // somewhere other than the plugin's package references.
        var hint = NativeDiagnostics.DescribeMiss(
            new DllNotFoundException("Unable to load shared library 'git2-abc'."),
            "linux-musl-arm64", null);

        Assert.Contains("no native asset directories", hint);
        Assert.DoesNotContain("searched", hint);
    }

    [Fact]
    public void A_failure_that_is_not_a_missing_library_gets_no_hint_at_all()
    {
        // Scoped narrowly because /invoke error bodies are pinned byte-for-byte
        // against the previous release by test/baseline.sh. A hint appended to
        // an unrelated error would break that guard.
        Assert.Equal("", NativeDiagnostics.HintFor(new InvalidOperationException("unrelated")));
        Assert.Equal("", NativeDiagnostics.HintFor(new IOException("also unrelated")));
    }

    [Fact]
    public void A_missing_library_is_found_however_deep_it_sits_in_the_inner_exception_chain()
    {
        // The real shape: a DllNotFoundException surfaces as the inner
        // exception of a TypeInitializationException, which is what the caller
        // actually receives.
        var buried = new TypeInitializationException("Some.Native.Type",
            new InvalidOperationException("wrapper",
                new DllNotFoundException("Unable to load shared library 'git2-abc'.")));

        Assert.Contains("'git2-abc'", NativeDiagnostics.HintFor(buried));
    }
}
