using System.Text.Json;

namespace RemoteFacadeHost;

public sealed record InvokeRequest(string Method, JsonElement[] Args, string? Service = null);

/// <summary>
/// What the host is serving, as the two read-only endpoints report it.
///
/// Both facts are fixed for the process: LIB_REGISTRAR is read once at
/// startup, and PluginLoader holds ONE assembly for the container's lifetime.
/// They are passed to the controller rather than read from statics inside it
/// so the endpoints can be driven without a loaded plugin -- reflection over
/// a real assembly is the container suite's job, not a unit test's.
/// </summary>
/// <param name="TypeNames">
/// Deferred rather than a materialized list: PluginLoader.TypeNames() calls
/// GetExportedTypes(), which THROWS for an assembly with an unloadable type
/// reference. Capturing it at startup would turn that into a failure to boot;
/// today it fails on GET /types, which is the endpoint whose entire purpose is
/// telling you what is wrong with the assembly you loaded.
/// </param>
public sealed record ServedPlugin(string Registrar, Func<IEnumerable<string>> TypeNames);
