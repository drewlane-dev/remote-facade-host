using RemoteFacadeHost;

namespace RemoteFacade.UnitTests;

/// <summary>
/// The deps.json requirement.
///
/// Resolution is driven by the plugin's own deps.json: it is what names the
/// RID-specific build of a managed assembly and the native assets under
/// runtimes/&lt;rid&gt;/native. Without it, AssemblyDependencyResolver falls
/// back to treating the plugin root as the whole of the plugin, which for a
/// package shipping a reference stub at that root means loading the stub.
///
/// Measured, on a plugin directory with its deps.json deleted:
/// Microsoft.Data.SqlClient resolved to the root assembly and the first call
/// returned {"ok":false,"error":"Microsoft.Data.SqlClient is not supported on
/// this platform."} -- an error that reads as a database fault, arriving long
/// after startup, on a host that reported itself healthy.
///
/// So its absence is fatal at startup rather than survivable. It costs nothing
/// legitimate: dotnet publish emits one for every project, including VB.
/// </summary>
public class PluginDependencyTests
{
    /// <summary>
    /// A directory of empty files, plus an optional deps.json with real
    /// content. The default content is a minimal but STRUCTURALLY VALID
    /// document: "{}" is a case under test below, not a stand-in for a real
    /// file.
    /// </summary>
    private static string Dir(string[] files, string? deps = null, string depsName = "MyApp.deps.json")
    {
        var dir = Path.Combine(Path.GetTempPath(), "rfh-deps-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        foreach (var f in files) File.WriteAllText(Path.Combine(dir, f), "");
        if (deps is not null) File.WriteAllText(Path.Combine(dir, depsName), deps);
        return dir;
    }

    private const string Valid = """
        {"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},
         "targets":{".NETCoreApp,Version=v10.0":{"MyApp/1.0.0":{}}},
         "libraries":{"MyApp/1.0.0":{"type":"project"}}}
        """;

    [Fact]
    public void A_published_plugin_directory_yields_its_dependency_file()
    {
        var dir = Dir(["MyApp.dll"], Valid);

        Assert.Equal(
            Path.Combine(dir, "MyApp.deps.json"),
            PluginLoader.RequireDependencyFile(dir, "MyApp.dll"));
    }

    [Fact]
    public void A_directory_without_one_is_refused_and_told_how_to_produce_it()
    {
        // The name is derived from LIB_ASSEMBLY, not guessed: a directory can
        // hold several deps.json files when a plugin carries its own
        // dependencies' publish output, and only the one belonging to the named
        // assembly governs.
        var dir = Dir(["MyApp.dll"], Valid, "Something.Else.deps.json");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PluginLoader.RequireDependencyFile(dir, "MyApp.dll"));

        Assert.Contains("MyApp.deps.json", ex.Message);
        Assert.Contains("dotnet publish", ex.Message);
    }

    [Fact]
    public void The_refusal_names_the_directory_it_actually_looked_in()
    {
        // LIB_DIR is a bind mount, and the most common cause of this is
        // mounting the wrong side of it -- a project folder rather than a
        // publish output. Naming the path is what makes that visible.
        var dir = Dir(["MyApp.dll"]);

        var ex = Assert.Throws<InvalidOperationException>(
            () => PluginLoader.RequireDependencyFile(dir, "MyApp.dll"));

        Assert.Contains(dir, ex.Message);
    }

    [Fact]
    public void A_dependency_file_that_describes_nothing_is_refused_like_a_missing_one()
    {
        // "{}" parses, exists, and is useless. Measured against a container:
        // the host started, reported healthy, and the first call came back
        // {"ok":false,"error":"Microsoft.Data.SqlClient is not supported on
        // this platform."} -- the identical silent-stub failure that requiring
        // the file at all exists to prevent. Checking only File.Exists would
        // have let it through.
        var dir = Dir(["MyApp.dll"], "{}");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PluginLoader.RequireDependencyFile(dir, "MyApp.dll"));

        Assert.Contains("describes no dependencies", ex.Message);
    }

    [Fact]
    public void A_dependency_file_that_is_not_json_says_so_rather_than_leaking_a_parse_error()
    {
        // A truncated or partially-written file is the realistic shape of this
        // -- a copy that failed halfway. A raw JsonException naming a line and
        // column says nothing about which file, or that it belongs to the
        // plugin rather than to the host.
        var dir = Dir(["MyApp.dll"], "{ not json");

        var ex = Assert.Throws<InvalidOperationException>(
            () => PluginLoader.RequireDependencyFile(dir, "MyApp.dll"));

        Assert.Contains("MyApp.deps.json", ex.Message);
        Assert.Contains("could not be read", ex.Message);
    }
}
