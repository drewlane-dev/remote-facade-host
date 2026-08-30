using System.Runtime.InteropServices;
using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// Choosing which of a plugin's copies of an assembly to load.
///
/// Packages that ship per-platform builds put a reference assembly at the root
/// and the working one under runtimes/&lt;rid&gt;/lib. The runtime normally
/// chooses using the app's deps.json, but a plugin loaded with
/// Assembly.LoadFrom gets none of that, so the host has to choose.
/// </summary>
public class PluginRidTests
{
    [Fact]
    public void The_running_rid_is_tried_first()
    {
        Assert.Equal(RuntimeInformation.RuntimeIdentifier,
            PluginLoader.ManagedRidCandidates().First());
    }

    [Fact]
    public void The_chain_falls_back_to_the_operating_system_and_then_unix()
    {
        // The rungs that matter. Microsoft.Data.SqlClient ships under
        // runtimes/unix, which matches NO exact RID -- without the portable
        // rungs nothing is found and the root stub wins, which is exactly how
        // the first version of this failed.
        var rid = RuntimeInformation.RuntimeIdentifier;
        var chain = PluginLoader.ManagedRidCandidates().ToList();
        var os = rid.Split('-')[0];

        Assert.Contains(os, chain);

        if (os.StartsWith("win", StringComparison.OrdinalIgnoreCase))
        {
            Assert.DoesNotContain("unix", chain);
        }
        else
        {
            Assert.Contains("unix", chain);
        }
    }

    [Fact]
    public void A_musl_rid_also_offers_its_glibc_spelling()
    {
        var chain = PluginLoader.ManagedRidCandidates().ToList();
        var rid = chain[0];

        if (rid.Contains("-musl-"))
        {
            Assert.Contains(rid.Replace("-musl-", "-"), chain);
        }
    }

    [Fact]
    public void Candidates_are_ordered_most_specific_first()
    {
        // Order is the whole contract: a less specific match found first would
        // load the wrong build and nothing would report it.
        var chain = PluginLoader.ManagedRidCandidates().ToList();
        Assert.Equal(chain.OrderByDescending(c => c.Length).First().Length, chain[0].Length);
        Assert.Distinct(chain);
    }

    [Fact]
    public void A_plugin_with_no_runtimes_folder_selects_nothing()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Assert.Null(PluginLoader.RidAssetDirectory(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_newest_framework_folder_is_chosen()
    {
        // A package may ship net8.0 and net9.0 side by side. The newest is what
        // the SDK would have picked, and picking the oldest would work until it
        // silently did not.
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            foreach (var tfm in new[] { "net8.0", "net9.0" })
            {
                Directory.CreateDirectory(Path.Combine(root, "runtimes", rid, "lib", tfm));
            }

            var chosen = PluginLoader.RidAssetDirectory(root);

            Assert.NotNull(chosen);
            Assert.Equal("net9.0", Path.GetFileName(chosen));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_portable_rid_folder_is_found_when_no_exact_one_exists()
    {
        // The case that matters, and the one the first implementation missed.
        if (RuntimeInformation.RuntimeIdentifier.StartsWith("win")) return;

        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "runtimes", "unix", "lib", "net9.0"));

            var chosen = PluginLoader.RidAssetDirectory(root);

            Assert.NotNull(chosen);
            Assert.Contains(Path.Combine("runtimes", "unix", "lib", "net9.0"), chosen);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void An_exact_rid_folder_beats_a_portable_one()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var rid = RuntimeInformation.RuntimeIdentifier;
            Directory.CreateDirectory(Path.Combine(root, "runtimes", rid, "lib", "net9.0"));
            Directory.CreateDirectory(Path.Combine(root, "runtimes", "unix", "lib", "net9.0"));

            Assert.Contains(rid, PluginLoader.RidAssetDirectory(root)!);
        }
        finally { Directory.Delete(root, true); }
    }
}
