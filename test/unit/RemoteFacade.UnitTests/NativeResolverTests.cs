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
    [Fact]
    public void The_running_rid_is_always_tried_first()
    {
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        Assert.Equal(rid, NativeResolver.RidCandidates().First());
    }

    [Fact]
    public void A_musl_rid_falls_back_to_its_glibc_spelling()
    {
        // Only meaningful on musl, which is what the image runs. Asserted as a
        // property of the transformation so it holds on either host.
        var candidates = NativeResolver.RidCandidates().ToList();
        var rid = candidates[0];

        if (rid.Contains("-musl-"))
        {
            Assert.Contains(rid.Replace("-musl-", "-"), candidates);
        }
        else
        {
            Assert.Single(candidates);
        }
    }

    [Fact]
    public void A_dll_import_name_is_tried_with_the_lib_prefix_and_so_suffix()
    {
        // The variant that matters: LibGit2Sharp imports "git2-5853918" and
        // the file on disk is "libgit2-5853918.so".
        var names = NativeResolver.FileNames("git2-5853918").ToList();

        Assert.Contains("git2-5853918", names);
        Assert.Contains("libgit2-5853918.so", names);
        Assert.Contains("git2-5853918.so", names);
    }

    [Theory]
    [InlineData("Unable to load shared library 'git2-5853918' or one of its dependencies.", "'git2-5853918'")]
    [InlineData("no quotes here at all", "")]
    [InlineData("one 'unterminated quote", "")]
    public void The_library_name_is_lifted_out_of_the_runtime_s_message(string message, string expected)
    {
        Assert.Equal(expected, NativeResolver.LibraryName(new DllNotFoundException(message)));
    }

    [Fact]
    public void A_miss_names_the_library_the_rid_and_every_directory_searched()
    {
        var hint = NativeResolver.DescribeMiss(
            new DllNotFoundException("Unable to load shared library 'git2-abc' or one of its dependencies."),
            "linux-musl-x64",
            ["/plugin/runtimes/linux-musl-x64/native", "/plugin"],
            "/plugin");

        Assert.Contains("'git2-abc'", hint);
        Assert.Contains("linux-musl-x64", hint);
        Assert.Contains("/plugin/runtimes/linux-musl-x64/native", hint);
    }

    [Fact]
    public void With_no_native_directories_the_message_points_at_the_publish_instead()
    {
        // A different fault with a different fix: nothing was found to search,
        // so telling the reader which directories were searched would be
        // useless and telling them to check the publish is actionable.
        var hint = NativeResolver.DescribeMiss(
            new DllNotFoundException("Unable to load shared library 'git2-abc'."),
            "linux-musl-x64", [], "/plugin");

        Assert.Contains("no native asset directories", hint);
        Assert.Contains("Publish the plugin", hint);
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
