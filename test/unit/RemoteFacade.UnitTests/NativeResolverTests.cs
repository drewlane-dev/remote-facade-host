using System.Runtime.Loader;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The pure half of native resolution. Install() is deliberately not exercised
/// here: it subscribes a process-global event and latches a flag nothing can
/// undo, so a test going through it would run once per process and leave the
/// resolver installed for everything after. The container suite covers the
/// wiring; this covers the decisions.
/// </summary>
public class NativeResolverTests
{
    [Theory]
    [InlineData("Unable to load shared library 'git2-5853918' or one of its dependencies.", "git2-5853918")]
    [InlineData("no quotes here at all", "")]
    [InlineData("one 'unterminated quote", "")]
    public void The_library_name_is_lifted_out_of_the_runtime_s_message(string message, string expected)
    {
        Assert.Equal(expected, NativeResolver.LibraryName(new DllNotFoundException(message)));
    }

    [Fact]
    public void A_miss_names_the_library_the_rid_and_the_reason()
    {
        var hint = NativeResolver.DescribeMiss(
            new DllNotFoundException("Unable to load shared library 'git2-abc' or one of its dependencies."),
            "linux-musl-x64",
            "the plugin's deps.json declares no native asset by that name for this rid");

        Assert.Contains("'git2-abc'", hint);
        Assert.Contains("linux-musl-x64", hint);
        Assert.Contains("declares no native asset", hint);
    }

    [Fact]
    public void A_miss_names_both_causes_rather_than_guessing_between_them()
    {
        // ResolveUnmanagedDllToPath returns null for "deps.json declares no
        // such asset for this rid" AND for "it declares one that is not on
        // disk". An earlier version of this message asserted the first; the
        // container case that deletes runtimes/ while KEEPING deps.json proved
        // it wrong, because that is the second and it still reported the first.
        var explained = NativeResolver.Explain(
            new AssemblyDependencyResolver(typeof(NativeResolverTests).Assembly.Location));

        Assert.Contains("no package in the plugin ships that asset", explained);
        Assert.Contains("absent from the publish", explained);
    }

    [Fact]
    public void A_failure_that_is_not_a_missing_library_gets_no_hint_at_all()
    {
        // Scoped narrowly because /invoke error bodies are pinned byte-for-byte
        // against the previous release by test/baseline.sh. A hint appended to
        // an unrelated error would break that guard.
        Assert.Equal("", NativeResolver.HintFor(new InvalidOperationException("unrelated")));
        Assert.Equal("", NativeResolver.HintFor(new IOException("also unrelated")));
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

        Assert.Contains("'git2-abc'", NativeResolver.HintFor(buried));
    }
}
